//! Verify the signed release against the exact public key shipped to clients.
//! Never executes or installs the input file.
use base64::{engine::general_purpose::STANDARD, Engine};
use minisign_verify::{PublicKey, Signature};
use std::{error::Error, fs};

fn main() -> Result<(), Box<dyn Error>> {
    let args: Vec<_> = std::env::args().skip(1).collect();
    if args.len() != 3 {
        return Err("Usage: verify-update <installer> <signature> <tauri.conf.json>".into());
    }
    let configuration: serde_json::Value = serde_json::from_slice(&fs::read(&args[2])?)?;
    let public = STANDARD.decode(
        configuration["plugins"]["updater"]["pubkey"]
            .as_str()
            .ok_or("Missing updater public key")?,
    )?;
    let signature = STANDARD.decode(fs::read_to_string(&args[1])?.trim())?;
    let key = PublicKey::decode(std::str::from_utf8(&public)?)?;
    let signature = Signature::decode(std::str::from_utf8(&signature)?)?;
    key.verify(&fs::read(&args[0])?, &signature, false)?;
    println!("PASS: installer signature matches the bundled updater public key.");
    Ok(())
}
