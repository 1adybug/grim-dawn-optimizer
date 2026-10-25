use flate2::{read::GzDecoder, write::GzEncoder, Compression};
use sha2::{Digest, Sha256};
use std::collections::HashSet;
use std::fs;
use std::io::{self, Cursor, Read, Write};
use std::path::Path;

const MAGIC: &[u8; 8] = b"GDOPACK1";
const MAX_BYTES: u64 = 1024 * 1024 * 1024;
const MAX_FILES: u32 = 4096;
pub const APP_EXE: &str = "GrimDawnOptimizer.exe";

pub fn invalid(message: impl Into<String>) -> io::Error {
    io::Error::new(io::ErrorKind::InvalidData, message.into())
}

pub fn digest_hex(data: &[u8]) -> String {
    Sha256::digest(data)
        .iter()
        .map(|b| format!("{b:02x}"))
        .collect()
}

pub fn validate_path(path: &str) -> io::Result<()> {
    if path.is_empty() || path.len() > 1024 || path.contains('\\') {
        return Err(invalid("Invalid portable archive path"));
    }
    for part in path.split('/') {
        let base = part.split('.').next().unwrap_or("").to_ascii_uppercase();
        let reserved = matches!(
            base.as_str(),
            "CON" | "PRN" | "AUX" | "NUL" | "CONIN$" | "CONOUT$"
        ) || ["COM", "LPT"].iter().any(|prefix| {
            base.strip_prefix(prefix).is_some_and(|n| {
                matches!(
                    n,
                    "1" | "2" | "3" | "4" | "5" | "6" | "7" | "8" | "9" | "¹" | "²" | "³"
                )
            })
        });
        if part.is_empty()
            || part == "."
            || part == ".."
            || part.ends_with(['.', ' '])
            || part
                .chars()
                .any(|c| c.is_control() || "<>:\"|?*".contains(c))
            || reserved
        {
            return Err(invalid(format!("Unsafe portable archive path: {path}")));
        }
    }
    Ok(())
}

pub fn is_link(metadata: &fs::Metadata) -> bool {
    #[cfg(windows)]
    {
        use std::os::windows::fs::MetadataExt;
        metadata.file_attributes() & 0x400 != 0 // Includes junctions/reparse points.
    }
    #[cfg(not(windows))]
    {
        metadata.file_type().is_symlink()
    }
}

pub fn pack_directory(root: &Path) -> io::Result<Vec<u8>> {
    fn visit(root: &Path, directory: &Path, files: &mut Vec<(String, Vec<u8>)>) -> io::Result<()> {
        for entry in fs::read_dir(directory)? {
            let path = entry?.path();
            let metadata = fs::symlink_metadata(&path)?;
            if is_link(&metadata) {
                return Err(invalid("Portable payload may not contain links"));
            }
            if metadata.is_dir() {
                visit(root, &path, files)?;
            } else if metadata.is_file() {
                let relative = path
                    .strip_prefix(root)
                    .map_err(|_| invalid("Invalid payload root"))?
                    .to_str()
                    .ok_or_else(|| invalid("Non-Unicode payload path"))?
                    .replace('\\', "/");
                files.push((relative, fs::read(path)?));
            } else {
                return Err(invalid("Unsupported portable payload entry"));
            }
        }
        Ok(())
    }
    let mut files = Vec::new();
    visit(root, root, &mut files)?;
    files.sort_by(|a, b| a.0.cmp(&b.0));
    pack_files(&files)
}

pub fn pack_files(files: &[(String, Vec<u8>)]) -> io::Result<Vec<u8>> {
    if files.len() > MAX_FILES as usize {
        return Err(invalid("Too many payload files"));
    }
    let mut raw = Vec::new();
    raw.extend_from_slice(MAGIC);
    raw.extend_from_slice(&(files.len() as u32).to_le_bytes());
    for (path, bytes) in files {
        validate_path(path)?;
        raw.extend_from_slice(&(path.len() as u32).to_le_bytes());
        raw.extend_from_slice(&(bytes.len() as u64).to_le_bytes());
        raw.extend_from_slice(&Sha256::digest(bytes));
        raw.extend_from_slice(path.as_bytes());
        raw.extend_from_slice(bytes);
        if raw.len() as u64 > MAX_BYTES {
            return Err(invalid("Portable payload is too large"));
        }
    }
    let mut encoder = GzEncoder::new(Vec::new(), Compression::default());
    encoder.write_all(&raw)?;
    encoder.finish()
}

pub struct Entry {
    pub path: String,
    pub offset: usize,
    pub length: usize,
    pub sha256: [u8; 32],
}

pub struct Payload {
    data: Vec<u8>,
    pub entries: Vec<Entry>,
    pub hash: String,
}

impl Payload {
    pub fn read(compressed: &[u8], expected_hash: &str) -> io::Result<Self> {
        let hash = digest_hex(compressed);
        if hash != expected_hash {
            return Err(invalid("Portable payload checksum mismatch"));
        }
        let mut data = Vec::new();
        GzDecoder::new(compressed)
            .take(MAX_BYTES + 1)
            .read_to_end(&mut data)?;
        if data.len() as u64 > MAX_BYTES {
            return Err(invalid("Portable payload is too large"));
        }
        let mut cursor = Cursor::new(data.as_slice());
        let mut magic = [0; 8];
        cursor.read_exact(&mut magic)?;
        if &magic != MAGIC {
            return Err(invalid("Invalid portable archive header"));
        }
        let mut count = [0; 4];
        cursor.read_exact(&mut count)?;
        let count = u32::from_le_bytes(count);
        if count == 0 || count > MAX_FILES {
            return Err(invalid("Invalid portable file count"));
        }
        let mut entries = Vec::new();
        let mut paths = HashSet::new();
        for _ in 0..count {
            let mut path_length = [0; 4];
            let mut file_length = [0; 8];
            let mut sha256 = [0; 32];
            cursor.read_exact(&mut path_length)?;
            cursor.read_exact(&mut file_length)?;
            cursor.read_exact(&mut sha256)?;
            let path_length = u32::from_le_bytes(path_length) as usize;
            let length = u64::from_le_bytes(file_length);
            if path_length > 1024 || length > MAX_BYTES {
                return Err(invalid("Invalid portable entry size"));
            }
            let mut path = vec![0; path_length];
            cursor.read_exact(&mut path)?;
            let path =
                String::from_utf8(path).map_err(|_| invalid("Invalid portable entry name"))?;
            validate_path(&path)?;
            if !paths.insert(path.to_lowercase()) {
                return Err(invalid("Duplicate portable entry"));
            }
            let offset = cursor.position() as usize;
            let end = offset
                .checked_add(length as usize)
                .filter(|end| *end <= data.len())
                .ok_or_else(|| invalid("Truncated portable entry"))?;
            if Sha256::digest(&data[offset..end]).as_slice() != sha256 {
                return Err(invalid("Portable file checksum mismatch"));
            }
            entries.push(Entry {
                path,
                offset,
                length: length as usize,
                sha256,
            });
            cursor.set_position(end as u64);
        }
        if cursor.position() != data.len() as u64 || !paths.contains(&APP_EXE.to_lowercase()) {
            return Err(invalid("Incomplete portable archive"));
        }
        for entry in &entries {
            for (index, _) in entry.path.match_indices('/') {
                if paths.contains(&entry.path[..index].to_lowercase()) {
                    return Err(invalid("Portable file/directory collision"));
                }
            }
        }
        Ok(Self {
            data,
            entries,
            hash,
        })
    }

    pub fn bytes(&self, entry: &Entry) -> &[u8] {
        &self.data[entry.offset..entry.offset + entry.length]
    }
}
