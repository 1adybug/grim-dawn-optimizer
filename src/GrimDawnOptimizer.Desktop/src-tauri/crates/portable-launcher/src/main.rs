#![cfg_attr(windows, windows_subsystem = "windows")]

use grim_dawn_portable_launcher::{archive::Payload, execute, Action};
use std::{ffi::OsString, io, path::PathBuf, process::Command};

const PAYLOAD: &[u8] = include_bytes!(concat!(env!("OUT_DIR"), "/payload.gz"));

fn run(args: Vec<OsString>) -> io::Result<()> {
    let action = Action::parse(args)?;
    let payload = Payload::read(PAYLOAD, env!("GDO_PAYLOAD_SHA256"))?;
    let cache = if matches!(action, Action::Launch(_)) {
        let local = std::env::var_os("LOCALAPPDATA")
            .ok_or_else(|| io::Error::other("Cannot locate LocalAppData"))?;
        PathBuf::from(local).join("GrimDawnOptimizer/portable")
    } else {
        PathBuf::new()
    };
    execute(&payload, action, &cache, |executable, arguments| {
        Command::new(executable).args(arguments).spawn()?;
        Ok(())
    })
}

#[cfg(windows)]
fn show_error(message: &str) {
    #[link(name = "user32")]
    unsafe extern "system" {
        fn MessageBoxW(
            window: *mut std::ffi::c_void,
            text: *const u16,
            title: *const u16,
            kind: u32,
        ) -> i32;
    }
    let text: Vec<u16> = format!("无法启动 Grim Dawn Optimizer。\n\n{message}\n\n请重新下载完整的便携 EXE，或检查本地缓存目录权限。")
        .encode_utf16().chain(Some(0)).collect();
    let title: Vec<u16> = "Grim Dawn Optimizer"
        .encode_utf16()
        .chain(Some(0))
        .collect();
    unsafe {
        MessageBoxW(std::ptr::null_mut(), text.as_ptr(), title.as_ptr(), 0x10);
    }
}

fn main() {
    let args: Vec<_> = std::env::args_os().skip(1).collect();
    // Validation commands never open a window, including malformed arguments.
    let headless = args
        .iter()
        .any(|arg| arg == "--verify-only" || arg == "--extract-only");
    if let Err(error) = run(args) {
        eprintln!("Portable launcher: {error}");
        #[cfg(windows)]
        if !headless {
            show_error(&error.to_string());
        }
        #[cfg(not(windows))]
        let _ = headless;
        std::process::exit(1);
    }
}
