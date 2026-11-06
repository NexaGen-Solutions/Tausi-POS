//! The terminal installer the paired server offers (`/api/updates/terminal/latest.json`, the Tauri
//! updater's format), and its signature check: the launcher installs nothing that NexaGen's updater
//! key didn't sign.

use base64::Engine;
use serde::Deserialize;

/// The updater's public key (from `tauri.conf.json`, set by `build.rs`).
pub const UPDATER_PUBLIC_KEY: &str = env!("TAUSI_UPDATER_PUBKEY");

#[derive(Deserialize, Debug, PartialEq)]
pub struct Offer {
    pub version: String,
    pub url: String,
    pub signature: String,
}

#[derive(Deserialize)]
struct Feed {
    version: String,
    platforms: std::collections::HashMap<String, Platform>,
}

#[derive(Deserialize)]
struct Platform {
    url: String,
    signature: String,
}

/// The Windows installer in the server's answer; None when it offers none.
pub fn parse(json: &[u8]) -> Option<Offer> {
    let feed: Feed = serde_json::from_slice(json).ok()?;
    let platform = feed.platforms.get("windows-x86_64")?;
    Some(Offer { version: feed.version, url: platform.url.clone(), signature: platform.signature.clone() })
}

/// The address the app was paired with (`serverUrl` in its store, `%APPDATA%\com.nexagensolutions.tausipos\tausi.json`).
pub fn server_of_store(json: &[u8]) -> Option<String> {
    let store: serde_json::Value = serde_json::from_slice(json).ok()?;
    store.get("serverUrl")?.as_str().map(|s| s.trim_end_matches('/').to_string()).filter(|s| !s.is_empty())
}

/// Both the key and the signature are base64 of minisign's text files, as the Tauri updater has them.
pub fn verify(data: &[u8], signature_base64: &str, public_key_base64: &str) -> Result<(), String> {
    let text = |b64: &str| -> Result<String, String> {
        let bytes = base64::engine::general_purpose::STANDARD.decode(b64.trim()).map_err(|e| e.to_string())?;
        String::from_utf8(bytes).map_err(|e| e.to_string())
    };
    let key = minisign_verify::PublicKey::decode(&text(public_key_base64)?).map_err(|e| e.to_string())?;
    let signature = minisign_verify::Signature::decode(&text(signature_base64)?).map_err(|e| e.to_string())?;
    key.verify(data, &signature, true).map_err(|_| "The installer's signature doesn't match: it wasn't made by NexaGen Solutions.".to_string())
}

#[cfg(test)]
mod tests {
    use super::*;

    const KEY: &str = include_str!("../tests/fixtures/test.pub");
    const DATA: &[u8] = include_bytes!("../tests/fixtures/installer.bin");
    const SIGNATURE: &str = include_str!("../tests/fixtures/installer.bin.sig");

    #[test]
    fn reads_the_windows_installer_from_the_servers_feed() {
        let json = br#"{"version":"1.0.1","notes":"","pub_date":null,"platforms":{"windows-x86_64":{"signature":"c2ln","url":"http://10.0.0.5:5080/api/updates/terminal/files/TausiPOS-Setup.exe"}}}"#;
        assert_eq!(
            parse(json),
            Some(Offer { version: "1.0.1".into(), url: "http://10.0.0.5:5080/api/updates/terminal/files/TausiPOS-Setup.exe".into(), signature: "c2ln".into() })
        );
        assert_eq!(parse(br#"{"version":"1.0.1","platforms":{}}"#), None);
        assert_eq!(parse(b""), None, "the server answers 204 when it has no terminal update");
    }

    #[test]
    fn finds_the_paired_server_in_the_apps_store() {
        assert_eq!(server_of_store(br#"{"serverUrl":"http://10.0.0.5:5080/","deviceTokenProtected":"x"}"#).as_deref(), Some("http://10.0.0.5:5080"));
        assert_eq!(server_of_store(br#"{"kiosk":true}"#), None);
    }

    #[test]
    fn accepts_a_signed_installer_and_refuses_a_changed_one() {
        assert_eq!(verify(DATA, SIGNATURE, KEY), Ok(()));
        let mut changed = DATA.to_vec();
        changed[0] ^= 1;
        assert!(verify(&changed, SIGNATURE, KEY).is_err());
        assert!(verify(DATA, SIGNATURE, UPDATER_PUBLIC_KEY).is_err(), "signed with another key");
    }
}
