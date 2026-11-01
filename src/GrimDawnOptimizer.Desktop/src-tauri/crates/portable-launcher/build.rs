#[allow(dead_code)]
#[path = "src/archive.rs"]
mod archive;

use std::{env, fs, path::PathBuf};

fn main() {
    println!("cargo:rerun-if-env-changed=GDO_PORTABLE_SOURCE");
    println!("cargo:rerun-if-changed=src/archive.rs");
    let bytes = if let Some(source) = env::var_os("GDO_PORTABLE_SOURCE") {
        let source = PathBuf::from(source);
        for required in [
            "GrimDawnOptimizer.exe",
            "grim-dawn-engine.exe",
            "LICENSE",
            "THIRD-PARTY-NOTICES.md",
            "LICENSES/MinHook-BSD.txt",
            "trainer-native/CairnBridge.v6.x86.dll",
            "trainer-native/CairnBridge.v6.x64.dll",
            "trainer-native/CairnInject.x86.exe",
            "trainer-native/CairnInject.x64.exe",
        ] {
            assert!(
                source.join(required).is_file(),
                "Portable file missing: {required}"
            );
        }
        println!("cargo:rerun-if-changed={}", source.display());
        let packed = archive::pack_directory(&source).expect("Cannot pack portable payload");
        archive::Payload::read(&packed, &archive::digest_hex(&packed))
            .expect("Invalid portable payload");
        packed
    } else {
        // Library tests do not need a GUI binary or a prepared engine. The empty
        // payload is deliberately invalid at runtime and cannot be published by
        // package-portable.ps1, whose mandatory --verify-only check rejects it.
        Vec::new()
    };
    let out = PathBuf::from(env::var_os("OUT_DIR").expect("OUT_DIR"));
    fs::write(out.join("payload.gz"), &bytes).expect("Cannot write payload");
    println!(
        "cargo:rustc-env=GDO_PAYLOAD_SHA256={}",
        archive::digest_hex(&bytes)
    );
    if env::var("CARGO_CFG_TARGET_OS").as_deref() == Ok("windows") {
        let icon = PathBuf::from(env::var_os("CARGO_MANIFEST_DIR").expect("manifest dir"))
            .join("../../icons/icon.ico");
        println!("cargo:rerun-if-changed={}", icon.display());
        tauri_winres::WindowsResource::new()
            .set_icon(icon.to_str().expect("icon path"))
            .set("ProductName", "Grim Dawn Optimizer")
            .set("FileDescription", "Grim Dawn Optimizer Portable")
            .set("FileVersion", env!("CARGO_PKG_VERSION"))
            .set("ProductVersion", env!("CARGO_PKG_VERSION"))
            .set_manifest(r#"<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<assembly xmlns="urn:schemas-microsoft-com:asm.v1" manifestVersion="1.0">
<trustInfo xmlns="urn:schemas-microsoft-com:asm.v3"><security><requestedPrivileges><requestedExecutionLevel level="asInvoker" uiAccess="false"/></requestedPrivileges></security></trustInfo>
<application xmlns="urn:schemas-microsoft-com:asm.v3"><windowsSettings><dpiAware xmlns="http://schemas.microsoft.com/SMI/2005/WindowsSettings">true/pm</dpiAware><dpiAwareness xmlns="http://schemas.microsoft.com/SMI/2016/WindowsSettings">PerMonitorV2</dpiAwareness><longPathAware xmlns="http://schemas.microsoft.com/SMI/2016/WindowsSettings">true</longPathAware></windowsSettings></application>
</assembly>"#)
            .compile_for(&["grim-dawn-portable-launcher"]).expect("Cannot compile portable Windows resources");
    }
}
