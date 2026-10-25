use super::*;
use archive::{digest_hex, pack_files, validate_path};
use flate2::{write::GzEncoder, Compression};
use std::sync::{Arc, Barrier};

struct Temp(PathBuf);
impl Temp {
    fn new() -> Self {
        let path = std::env::temp_dir().join(format!(
            "gdo-portable-test-{}-{}",
            std::process::id(),
            SEQUENCE.fetch_add(1, Ordering::Relaxed)
        ));
        fs::create_dir(&path).unwrap();
        Self(path)
    }
}
impl Drop for Temp {
    fn drop(&mut self) {
        let _ = fs::remove_dir_all(&self.0);
    }
}

fn fixture() -> Payload {
    let bytes = pack_files(&[
        (
            APP_EXE.to_owned(),
            b"not an executable; tests must never spawn this".to_vec(),
        ),
        ("trainer-native/helper.dll".to_owned(), vec![7; 150_000]),
        ("LICENSES/license.txt".to_owned(), b"license".to_vec()),
    ])
    .unwrap();
    Payload::read(&bytes, &digest_hex(&bytes)).unwrap()
}

#[test]
fn rejects_windows_absolute_traversal_device_and_stream_paths() {
    for path in [
        "../outside",
        "/absolute",
        "C:/outside",
        "C:outside",
        "a/../b",
        "a\\b",
        "//server/share",
        "a//b",
        "./a",
        "NUL",
        "aux.txt",
        "COM1.txt",
        "lpt².log",
        "CONIN$",
        "dir/trailing.",
        "dir/trailing ",
        "a:b",
        "nul\0x",
        "a/*",
    ] {
        assert!(validate_path(path).is_err(), "accepted {path:?}");
    }
    assert!(validate_path("LICENSES/中文 license.txt").is_ok());
}

#[test]
fn decoder_checks_paths_independently_of_packer() {
    let path = "../outside";
    let mut raw = b"GDOPACK1".to_vec();
    raw.extend_from_slice(&1_u32.to_le_bytes());
    raw.extend_from_slice(&(path.len() as u32).to_le_bytes());
    raw.extend_from_slice(&0_u64.to_le_bytes());
    raw.extend_from_slice(&Sha256::digest([]));
    raw.extend_from_slice(path.as_bytes());
    let mut encoder = GzEncoder::new(Vec::new(), Compression::fast());
    encoder.write_all(&raw).unwrap();
    let bytes = encoder.finish().unwrap();
    assert!(Payload::read(&bytes, &digest_hex(&bytes)).is_err());
}

#[test]
fn rejects_corruption_duplicates_and_file_directory_collisions() {
    let bytes = pack_files(&[(APP_EXE.into(), vec![1])]).unwrap();
    assert!(Payload::read(&bytes, &"0".repeat(64)).is_err());
    assert!(Payload::read(
        &bytes[..bytes.len() - 1],
        &digest_hex(&bytes[..bytes.len() - 1])
    )
    .is_err());
    let mut raw = Vec::new();
    flate2::read::GzDecoder::new(bytes.as_slice())
        .read_to_end(&mut raw)
        .unwrap();
    *raw.last_mut().unwrap() ^= 1;
    let mut encoder = GzEncoder::new(Vec::new(), Compression::fast());
    encoder.write_all(&raw).unwrap();
    let altered = encoder.finish().unwrap();
    assert!(Payload::read(&altered, &digest_hex(&altered)).is_err());
    for files in [
        vec![
            (APP_EXE.into(), vec![1]),
            ("grimDawnOptimizer.EXE".into(), vec![2]),
        ],
        vec![
            (APP_EXE.into(), vec![1]),
            ("a".into(), vec![2]),
            ("a/file".into(), vec![3]),
        ],
    ] {
        let bytes = pack_files(&files).unwrap();
        assert!(Payload::read(&bytes, &digest_hex(&bytes)).is_err());
    }
}

#[test]
fn corrupted_cache_is_repaired_and_valid_cache_is_not_rewritten() {
    let root = Temp::new();
    let payload = fixture();
    let target = root.0.join("中文 output");
    extract(&payload, &target).unwrap();
    let modified = fs::metadata(target.join(APP_EXE))
        .unwrap()
        .modified()
        .unwrap();
    extract(&payload, &target).unwrap();
    assert_eq!(
        modified,
        fs::metadata(target.join(APP_EXE))
            .unwrap()
            .modified()
            .unwrap()
    );
    fs::write(target.join(APP_EXE), b"damaged").unwrap();
    extract(&payload, &target).unwrap();
    assert!(verified_directory(&payload, &target).unwrap());
    assert!(fs::read_dir(&root.0).unwrap().any(|entry| entry
        .unwrap()
        .file_name()
        .to_string_lossy()
        .contains(".replaced-")));
}

#[test]
fn unknown_cache_files_are_not_loaded_or_deleted() {
    let root = Temp::new();
    let payload = fixture();
    let target = root.0.join("cache");
    extract(&payload, &target).unwrap();
    fs::write(target.join("unexpected.dll"), b"unknown").unwrap();
    extract(&payload, &target).unwrap();
    assert!(!target.join("unexpected.dll").exists());
    let old = fs::read_dir(&root.0)
        .unwrap()
        .find_map(|entry| {
            let path = entry.unwrap().path();
            path.join("unexpected.dll").exists().then_some(path)
        })
        .unwrap();
    assert_eq!(fs::read(old.join("unexpected.dll")).unwrap(), b"unknown");
}

#[test]
fn concurrent_extractors_publish_only_complete_content() {
    let root = Temp::new();
    let payload = Arc::new(fixture());
    let target = root.0.join("shared");
    let barrier = Arc::new(Barrier::new(6));
    let threads: Vec<_> = (0..6)
        .map(|_| {
            let (payload, target, barrier) = (payload.clone(), target.clone(), barrier.clone());
            std::thread::spawn(move || {
                barrier.wait();
                extract(&payload, &target).unwrap();
            })
        })
        .collect();
    for thread in threads {
        thread.join().unwrap();
    }
    assert!(verified_directory(&payload, &target).unwrap());
    assert_eq!(fs::read_dir(&root.0).unwrap().count(), 2); // tree plus sibling lock
}

#[test]
fn verification_and_extraction_never_launch_any_process() {
    let root = Temp::new();
    let payload = fixture();
    for action in [Action::Verify, Action::Extract(root.0.join("extract"))] {
        execute(&payload, action, &root.0.join("cache"), |_, _| {
            panic!("must not launch")
        })
        .unwrap();
    }
    assert!(!root.0.join("cache").exists());
}

#[test]
fn launch_forwards_arguments_and_propagates_launch_failure() {
    let root = Temp::new();
    let payload = fixture();
    let args: Vec<OsString> = vec![
        "--custom".into(),
        "中文 path with spaces".into(),
        "\"quoted\"".into(),
    ];
    let result = execute(
        &payload,
        Action::Launch(args.clone()),
        &root.0,
        |path, forwarded| {
            assert_eq!(path, root.0.join(&payload.hash).join(APP_EXE));
            assert_eq!(forwarded, args);
            Err(io::Error::other("test launch failure"))
        },
    );
    assert!(result
        .unwrap_err()
        .to_string()
        .contains("test launch failure"));
}

#[test]
fn headless_flags_require_exact_arguments() {
    for target in ["", " ", "\t", ".", ".."] {
        assert!(Action::parse(vec!["--extract-only".into(), target.into()]).is_err());
    }
    let current = std::env::current_dir().unwrap();
    assert!(Action::parse(vec![
        "--extract-only".into(),
        current.clone().into_os_string()
    ])
    .is_err());
    assert!(extract(&fixture(), &current).is_err());
    assert!(current.is_dir());
    assert!(Action::parse(vec!["--unknown".into(), "--verify-only".into()]).is_err());
    assert_eq!(
        Action::parse(vec!["--verify-only".into()]).unwrap(),
        Action::Verify
    );
    assert!(Action::parse(vec!["--extract-only".into()]).is_err());
    assert!(Action::parse(vec!["--verify-only".into(), "extra".into()]).is_err());
    assert_eq!(
        Action::parse(vec!["--extract-only".into(), "中文 path".into()]).unwrap(),
        Action::Extract("中文 path".into())
    );
}

#[cfg(windows)]
#[test]
fn directory_junctions_are_rejected_without_writing_through_them() {
    let root = Temp::new();
    let outside = root.0.join("outside");
    let junction = root.0.join("junction");
    fs::create_dir(&outside).unwrap();
    // A junction needs no developer-mode/admin privilege. This command only
    // creates a test directory link and cannot launch an application or game.
    let status = std::process::Command::new("cmd")
        .args(["/c", "mklink", "/J"])
        .arg(&junction)
        .arg(&outside)
        .output()
        .unwrap();
    assert!(status.status.success());
    assert!(extract(&fixture(), &junction.join("cache")).is_err());
    assert_eq!(fs::read_dir(&outside).unwrap().count(), 0);
    fs::remove_dir(&junction).unwrap();
}
