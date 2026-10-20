//! Tausi POS terminal: a full-screen window that shows the POS screens served by Tausi Server.
//!
//! On first start (no server address stored) it shows the bundled setup screen, which asks for
//! the server address and shows a pairing code. Once paired, the server address and device token
//! live in the store, and the window opens `<server>/pos` directly on every start.

use std::path::Path;

use serde::{Deserialize, Serialize};
use tauri::{AppHandle, Manager, WebviewUrl, WebviewWindowBuilder};
use tauri_plugin_autostart::{MacosLauncher, ManagerExt};
use tauri_plugin_store::StoreExt;

const STORE_FILE: &str = "tausi.json";
const MAIN_WINDOW_TITLE: &str = "Tausi POS";

/// Before the rename to Tausi POS the app had another identifier, so another app-data folder, and
/// another store file. The store holds the device token: copying it keeps the terminal paired.
const OLD_IDENTIFIER: &str = "com.nexagen.terminal";
const OLD_STORE_FILE: &str = "nexagen.json";
const MAIN_WINDOW: &str = "main";

/// Copies the store from the app-data folder of the old identifier (a sibling of `data_dir`) when
/// this app has no store yet. Returns true when it copied. Never overwrites an existing store.
pub fn copy_old_store(data_dir: &Path) -> std::io::Result<bool> {
    let new_store = data_dir.join(STORE_FILE);
    if new_store.exists() {
        return Ok(false);
    }
    let Some(parent) = data_dir.parent() else {
        return Ok(false);
    };
    let old_store = parent.join(OLD_IDENTIFIER).join(OLD_STORE_FILE);
    if !old_store.is_file() {
        return Ok(false);
    }
    std::fs::create_dir_all(data_dir)?;
    std::fs::copy(&old_store, &new_store)?;
    Ok(true)
}

/// An environment variable, or its name from before the rename to Tausi POS.
fn env_var(name: &str, old_name: &str) -> Option<String> {
    std::env::var(name).ok().or_else(|| {
        let old = std::env::var(old_name).ok();
        if old.is_some() {
            eprintln!("{old_name} is an old name from before the rename to Tausi POS. Use {name}.");
        }
        old
    })
}

#[derive(Serialize, Deserialize, Default, Clone, Debug)]
#[serde(rename_all = "camelCase")]
pub struct DeviceConfig {
    pub server_url: Option<String>,
    pub device_token: Option<String>,
}

fn load_config(app: &AppHandle) -> Result<DeviceConfig, String> {
    let store = app.store(STORE_FILE).map_err(|e| e.to_string())?;
    Ok(DeviceConfig {
        server_url: store
            .get("serverUrl")
            .and_then(|v| v.as_str().map(String::from)),
        device_token: store
            .get("deviceToken")
            .and_then(|v| v.as_str().map(String::from)),
    })
}

/// The address the window should show: the server's POS screens once paired, else the setup screen.
fn start_url(config: &DeviceConfig) -> Result<WebviewUrl, String> {
    // In development the window always loads the Vite dev server, which proxies to the server.
    if cfg!(debug_assertions) {
        return Ok(WebviewUrl::App("index.html".into()));
    }
    match (&config.server_url, &config.device_token) {
        (Some(server), Some(_)) => {
            let url = url::Url::parse(server)
                .and_then(|base| base.join("/pos"))
                .map_err(|e| e.to_string())?;
            Ok(WebviewUrl::External(url))
        }
        _ => Ok(WebviewUrl::App("index.html".into())),
    }
}

#[tauri::command]
fn get_device_config(app: AppHandle) -> Result<DeviceConfig, String> {
    load_config(&app)
}

#[tauri::command]
fn save_device_config(app: AppHandle, config: DeviceConfig) -> Result<(), String> {
    let store = app.store(STORE_FILE).map_err(|e| e.to_string())?;
    match config.server_url {
        Some(v) => store.set("serverUrl", v),
        None => {
            store.delete("serverUrl");
        }
    }
    match config.device_token {
        Some(v) => store.set("deviceToken", v),
        None => {
            store.delete("deviceToken");
        }
    }
    store.save().map_err(|e| e.to_string())
}

/// Moves the window to the paired server's POS screens.
#[tauri::command]
fn open_server(app: AppHandle) -> Result<(), String> {
    let config = load_config(&app)?;
    let window = app
        .get_webview_window(MAIN_WINDOW)
        .ok_or("no main window")?;
    match start_url(&config)? {
        WebviewUrl::External(url) => window.navigate(url).map_err(|e| e.to_string()),
        _ => window
            .eval("window.location.replace('/pos')")
            .map_err(|e| e.to_string()),
    }
}

/// Prints raw ESC/POS bytes to a printer attached to this PC ("Via terminal" printers).
#[tauri::command]
fn print_raw(printer: String, data_base64: String) -> Result<(), String> {
    // TODO(printing): send the bytes to the local Windows printer queue in RAW mode
    // (OpenPrinter / StartDocPrinter with datatype "RAW" / WritePrinter).
    let _ = data_base64;
    Err(format!(
        "Printing via this terminal is not implemented yet (printer \"{printer}\")."
    ))
}

#[cfg_attr(mobile, tauri::mobile_entry_point)]
pub fn run() {
    tauri::Builder::default()
        .plugin(tauri_plugin_store::Builder::new().build())
        .plugin(tauri_plugin_autostart::init(
            MacosLauncher::LaunchAgent,
            None,
        ))
        .plugin(tauri_plugin_updater::Builder::new().build())
        .invoke_handler(tauri::generate_handler![
            get_device_config,
            save_device_config,
            open_server,
            print_raw
        ])
        .setup(|app| {
            let handle = app.handle().clone();
            // Keep the pairing from the app's old identifier, before the store is first opened.
            if let Ok(dir) = handle.path().app_data_dir() {
                match copy_old_store(&dir) {
                    Ok(true) => eprintln!("Copied the terminal settings from {OLD_IDENTIFIER}."),
                    Ok(false) => {}
                    Err(e) => {
                        eprintln!("Could not copy the terminal settings from {OLD_IDENTIFIER}: {e}")
                    }
                }
            }
            let config = load_config(&handle).unwrap_or_default();

            // Counter PCs: full screen, no title bar. In development a normal 1280x800 window
            // is easier to work with; set TAUSI_FULLSCREEN=1 to see the real thing.
            let full_screen = !cfg!(debug_assertions)
                || env_var("TAUSI_FULLSCREEN", "NEXAGEN_FULLSCREEN").is_some();
            let window = WebviewWindowBuilder::new(app, MAIN_WINDOW, start_url(&config)?)
                .title(MAIN_WINDOW_TITLE)
                .inner_size(1280.0, 800.0)
                .fullscreen(full_screen)
                .decorations(!full_screen)
                .focused(true)
                .build()?;
            // Take keyboard focus on start, so a cashier can type a PIN without touching the screen.
            let _ = window.set_focus();

            if !cfg!(debug_assertions) {
                // Start with Windows. Failing to register is not worth stopping the app for.
                let _ = handle.autolaunch().enable();
            }
            Ok(())
        })
        .run(tauri::generate_context!())
        .expect("error while running Tausi POS");
}

#[cfg(test)]
mod tests {
    use super::*;

    fn temp_root(name: &str) -> std::path::PathBuf {
        let dir = std::env::temp_dir().join(format!("tausi-store-{name}-{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&dir);
        std::fs::create_dir_all(&dir).unwrap();
        dir
    }

    #[test]
    fn copies_the_old_store_so_the_terminal_stays_paired() {
        let root = temp_root("copy");
        let old = root.join(OLD_IDENTIFIER);
        std::fs::create_dir_all(&old).unwrap();
        std::fs::write(
            old.join(OLD_STORE_FILE),
            r#"{"serverUrl":"http://pos:5080","deviceToken":"tok"}"#,
        )
        .unwrap();
        let data_dir = root.join("com.nexagensolutions.tausipos");

        assert!(copy_old_store(&data_dir).unwrap());
        let copied = std::fs::read_to_string(data_dir.join(STORE_FILE)).unwrap();
        assert!(copied.contains("\"deviceToken\":\"tok\""));
        assert!(
            old.join(OLD_STORE_FILE).exists(),
            "the old store is left in place"
        );
        std::fs::remove_dir_all(&root).unwrap();
    }

    #[test]
    fn never_overwrites_an_existing_store() {
        let root = temp_root("keep");
        let old = root.join(OLD_IDENTIFIER);
        std::fs::create_dir_all(&old).unwrap();
        std::fs::write(old.join(OLD_STORE_FILE), "old").unwrap();
        let data_dir = root.join("com.nexagensolutions.tausipos");
        std::fs::create_dir_all(&data_dir).unwrap();
        std::fs::write(data_dir.join(STORE_FILE), "new").unwrap();

        assert!(!copy_old_store(&data_dir).unwrap());
        assert_eq!(
            std::fs::read_to_string(data_dir.join(STORE_FILE)).unwrap(),
            "new"
        );
        std::fs::remove_dir_all(&root).unwrap();
    }

    #[test]
    fn does_nothing_on_a_fresh_install() {
        let root = temp_root("fresh");
        let data_dir = root.join("com.nexagensolutions.tausipos");

        assert!(!copy_old_store(&data_dir).unwrap());
        assert!(!data_dir.join(STORE_FILE).exists());
        std::fs::remove_dir_all(&root).unwrap();
    }
}
