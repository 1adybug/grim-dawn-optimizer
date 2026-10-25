pub mod archive;

use archive::{invalid, is_link, Payload, APP_EXE};
use sha2::{Digest, Sha256};
use std::collections::HashSet;
use std::ffi::OsString;
use std::fs::{self, File, OpenOptions};
use std::io::{self, Read, Write};
use std::path::{Component, Path, PathBuf};
use std::sync::atomic::{AtomicU64, Ordering};

static SEQUENCE: AtomicU64 = AtomicU64::new(0);

#[derive(Debug, PartialEq)]
pub enum Action {
    Verify,
    Extract(PathBuf),
    Launch(Vec<OsString>),
}

impl Action {
    pub fn parse(args: Vec<OsString>) -> io::Result<Self> {
        if args
            .iter()
            .skip(1)
            .any(|arg| arg == "--verify-only" || arg == "--extract-only")
        {
            return Err(invalid(
                "Verification/extraction flags must be the first and only operation",
            ));
        }
        match args.first().and_then(|arg| arg.to_str()) {
            Some("--verify-only") if args.len() == 1 => Ok(Self::Verify),
            Some("--extract-only") if args.len() == 2 => {
                let value = args[1].to_string_lossy();
                if value.trim().is_empty() || matches!(value.as_ref(), "." | "..") {
                    return Err(invalid("An explicit output subdirectory is required"));
                }
                let path = PathBuf::from(&args[1]);
                validate_extraction_target(&path)?;
                Ok(Self::Extract(path))
            }
            Some("--verify-only" | "--extract-only") => {
                Err(invalid("Usage: --verify-only | --extract-only <directory>"))
            }
            _ => Ok(Self::Launch(args)),
        }
    }
}

fn validate_extraction_target(path: &Path) -> io::Result<()> {
    if path.as_os_str().is_empty() || path.as_os_str().to_string_lossy().trim().is_empty() {
        return Err(invalid("An explicit output subdirectory is required"));
    }
    let target = std::path::absolute(path)?;
    let current = fs::canonicalize(std::env::current_dir()?)?;
    let existing_target = match fs::canonicalize(&target) {
        Ok(path) => Some(path),
        Err(error) if error.kind() == io::ErrorKind::NotFound => None,
        Err(error) => return Err(error),
    };
    if existing_target.is_some_and(|path| current.starts_with(path)) {
        return Err(invalid(
            "Cannot extract over the working directory or one of its parents",
        ));
    }
    Ok(())
}

fn ensure_directory(path: &Path) -> io::Result<()> {
    let mut current = PathBuf::new();
    for component in path.components() {
        if matches!(component, Component::ParentDir) {
            return Err(invalid("Parent directory traversal is not allowed"));
        }
        current.push(component);
        if matches!(component, Component::Prefix(_) | Component::RootDir) {
            continue;
        }
        match fs::symlink_metadata(&current) {
            Ok(metadata) if metadata.is_dir() && !is_link(&metadata) => {}
            Ok(_) => {
                return Err(invalid(format!(
                    "Unsafe portable directory: {}",
                    current.display()
                )))
            }
            Err(error) if error.kind() == io::ErrorKind::NotFound => {
                match fs::create_dir(&current) {
                    Ok(()) => {}
                    Err(error) if error.kind() == io::ErrorKind::AlreadyExists => {
                        let metadata = fs::symlink_metadata(&current)?;
                        if !metadata.is_dir() || is_link(&metadata) {
                            return Err(invalid("Unsafe portable directory"));
                        }
                    }
                    Err(error) => return Err(error),
                }
            }
            Err(error) => return Err(error),
        }
    }
    Ok(())
}

fn unique_sibling(target: &Path, purpose: &str) -> io::Result<PathBuf> {
    let name = target
        .file_name()
        .and_then(|name| name.to_str())
        .ok_or_else(|| invalid("Invalid output directory"))?;
    let timestamp = std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .map_err(io::Error::other)?
        .as_nanos();
    Ok(target.with_file_name(format!(
        ".{name}.{purpose}-{timestamp}-{}-{}",
        std::process::id(),
        SEQUENCE.fetch_add(1, Ordering::Relaxed)
    )))
}

fn verified_directory(payload: &Payload, target: &Path) -> io::Result<bool> {
    fn entries(directory: &Path, root: &Path, found: &mut HashSet<String>) -> io::Result<bool> {
        for entry in fs::read_dir(directory)? {
            let path = entry?.path();
            let metadata = fs::symlink_metadata(&path)?;
            if is_link(&metadata) {
                return Ok(false);
            }
            if metadata.is_dir() {
                if !entries(&path, root, found)? {
                    return Ok(false);
                }
            } else if metadata.is_file() {
                found.insert(
                    path.strip_prefix(root)
                        .map_err(|_| invalid("Invalid cache path"))?
                        .to_string_lossy()
                        .replace('\\', "/"),
                );
            } else {
                return Ok(false);
            }
        }
        Ok(true)
    }
    let metadata = match fs::symlink_metadata(target) {
        Ok(metadata) => metadata,
        Err(error) if error.kind() == io::ErrorKind::NotFound => return Ok(false),
        Err(error) => return Err(error),
    };
    if !metadata.is_dir() || is_link(&metadata) {
        return Err(invalid("Portable cache may not be a file or link"));
    }
    let mut found = HashSet::new();
    if !entries(target, target, &mut found)? || found.len() != payload.entries.len() {
        return Ok(false);
    }
    for entry in &payload.entries {
        if !found.contains(&entry.path) {
            return Ok(false);
        }
        let mut file = File::open(target.join(&entry.path))?;
        if file.metadata()?.len() != entry.length as u64 {
            return Ok(false);
        }
        let mut hasher = Sha256::new();
        let mut buffer = [0; 65536];
        loop {
            let count = file.read(&mut buffer)?;
            if count == 0 {
                break;
            }
            hasher.update(&buffer[..count]);
        }
        if hasher.finalize().as_slice() != entry.sha256 {
            return Ok(false);
        }
    }
    Ok(true)
}

/// Serialize independent processes with an OS lock. A crashed process releases
/// the lock automatically. Complete trees are published only by directory rename.
/// Replaced trees are retained as siblings, never recursively deleted by a launcher.
pub fn extract(payload: &Payload, directory: &Path) -> io::Result<PathBuf> {
    validate_extraction_target(directory)?;
    let target = std::path::absolute(directory)?;
    let parent = target
        .parent()
        .ok_or_else(|| invalid("An output directory is required"))?;
    ensure_directory(parent)?;
    let name = target
        .file_name()
        .and_then(|name| name.to_str())
        .ok_or_else(|| invalid("Invalid output directory"))?;
    let lock_path = parent.join(format!(".{name}.lock"));
    if let Ok(metadata) = fs::symlink_metadata(&lock_path) {
        if is_link(&metadata) || !metadata.is_file() {
            return Err(invalid("Unsafe portable lock file"));
        }
    }
    let lock = OpenOptions::new()
        .create(true)
        .truncate(false)
        .read(true)
        .write(true)
        .open(lock_path)?;
    lock.lock()?;
    if verified_directory(payload, &target)? {
        return Ok(target);
    }
    let staging = unique_sibling(&target, "extract")?;
    fs::create_dir(&staging)?;
    for entry in &payload.entries {
        let path = staging.join(&entry.path);
        ensure_directory(
            path.parent()
                .ok_or_else(|| invalid("Invalid payload path"))?,
        )?;
        let mut file = File::create_new(path)?;
        file.write_all(payload.bytes(entry))?;
        file.sync_all()?;
    }
    if !verified_directory(payload, &staging)? {
        return Err(invalid("Extracted file verification failed"));
    }
    let previous = if target.try_exists()? {
        let previous = unique_sibling(&target, "replaced")?;
        fs::rename(&target, &previous)?;
        Some(previous)
    } else {
        None
    };
    if let Err(error) = fs::rename(&staging, &target) {
        if let Some(previous) = previous {
            let _ = fs::rename(previous, &target);
        }
        return Err(error);
    }
    Ok(target)
}

pub fn execute(
    payload: &Payload,
    action: Action,
    cache_root: &Path,
    mut launch: impl FnMut(&Path, &[OsString]) -> io::Result<()>,
) -> io::Result<()> {
    match action {
        Action::Verify => Ok(()),
        Action::Extract(directory) => {
            extract(payload, &directory)?;
            Ok(())
        }
        Action::Launch(args) => {
            let directory = extract(payload, &cache_root.join(&payload.hash))?;
            launch(&directory.join(APP_EXE), &args)
        }
    }
}

#[cfg(test)]
mod tests;
