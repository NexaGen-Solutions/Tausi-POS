//! Nexagen Terminal: a full-screen window that shows the POS screens served by Nexagen Server.
//!
//! On first start (no server address stored) it shows the bundled setup screen, which asks for
//! the server address and shows a pairing code. Once paired, the server address and device token
//! live in the store, and the window opens `<server>/pos` directly on every start.

use serde::{Deserialize, Serialize};
use tauri::{AppHandle, Manager, WebviewUrl, WebviewWindowBuilder};
use tauri_plugin_autostart::{MacosLauncher, ManagerExt};
use tauri_plugin_store::StoreExt;

const STORE_FILE: &str = "nexagen.json";
const MAIN_WINDOW: &str = "main";

#[derive(Serialize, Deserialize, Default, Clone, Debug)]
#[serde(rename_all = "camelCase")]
pub struct DeviceConfig {
    pub server_url: Option<String>,
    pub device_token: Option<String>,
}

fn load_config(app: &AppHandle) -> Result<DeviceConfig, String> {
    let store = app.store(STORE_FILE).map_err(|e| e.to_string())?;
    Ok(DeviceConfig {
        server_url: store.get("serverUrl").and_then(|v| v.as_str().map(String::from)),
        device_token: store.get("deviceToken").and_then(|v| v.as_str().map(String::from)),
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
    let window = app.get_webview_window(MAIN_WINDOW).ok_or("no main window")?;
    match start_url(&config)? {
        WebviewUrl::External(url) => window.navigate(url).map_err(|e| e.to_string()),
        _ => window.eval("window.location.replace('/pos')").map_err(|e| e.to_string()),
    }
}

/// Prints raw ESC/POS bytes to a printer attached to this PC ("Via terminal" printers).
#[tauri::command]
fn print_raw(printer: String, data_base64: String) -> Result<(), String> {
    // TODO(printing): send the bytes to the local Windows printer queue in RAW mode
    // (OpenPrinter / StartDocPrinter with datatype "RAW" / WritePrinter).
    let _ = data_base64;
    Err(format!("Printing via this terminal is not implemented yet (printer \"{printer}\")."))
}

#[cfg_attr(mobile, tauri::mobile_entry_point)]
pub fn run() {
    tauri::Builder::default()
        .plugin(tauri_plugin_store::Builder::new().build())
        .plugin(tauri_plugin_autostart::init(MacosLauncher::LaunchAgent, None))
        .plugin(tauri_plugin_updater::Builder::new().build())
        .invoke_handler(tauri::generate_handler![
            get_device_config,
            save_device_config,
            open_server,
            print_raw
        ])
        .setup(|app| {
            let handle = app.handle().clone();
            let config = load_config(&handle).unwrap_or_default();

            // Counter PCs: full screen, no title bar. In development a normal 1280x800 window
            // is easier to work with; set NEXAGEN_FULLSCREEN=1 to see the real thing.
            let full_screen = !cfg!(debug_assertions) || std::env::var("NEXAGEN_FULLSCREEN").is_ok();
            WebviewWindowBuilder::new(app, MAIN_WINDOW, start_url(&config)?)
                .title("Nexagen Terminal")
                .inner_size(1280.0, 800.0)
                .fullscreen(full_screen)
                .decorations(!full_screen)
                .build()?;

            if !cfg!(debug_assertions) {
                // Start with Windows. Failing to register is not worth stopping the app for.
                let _ = handle.autolaunch().enable();
            }
            Ok(())
        })
        .run(tauri::generate_context!())
        .expect("error while running Nexagen Terminal");
}
