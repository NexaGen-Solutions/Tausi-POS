//! Tausi POS terminal: a full-screen window that shows the POS screens served by Tausi Server.
//!
//! On first start (no server address stored) it shows the bundled setup screen, which asks for
//! the server address and shows a pairing code. Once paired, the server address and device token
//! live in the store, and the window opens `<server>/pos` directly on every start.
//!
//! Locked down: the window can only show the bundled pages or the paired server's pages, and only
//! the paired server's pages may call the app's commands.
//!
//! Kiosk mode (on unless switched off on this PC): full screen, no frame, and closing (Alt+F4 too)
//! is blocked; leaving needs the hidden gesture and an admin-site login (the web side), then
//! `kiosk_exit`. Updates come from the paired server, never newer than it, and are installed
//! when the web side says the PIN screen has been idle long enough.

use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::Mutex;

use serde::{Deserialize, Serialize};
use tauri::ipc::CapabilityBuilder;
use tauri::{AppHandle, Manager, WebviewUrl, WebviewWindowBuilder, WindowEvent};
use tauri_plugin_autostart::{MacosLauncher, ManagerExt};
use tauri_plugin_store::StoreExt;
use tauri_plugin_updater::{Update, UpdaterExt};
use tausi_print::{PrintError, PrintRequest};
use url::Url;

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
    /// The address given to the installer (`TausiPOS-Setup.exe /S SERVER=...`), to pre-fill the first screen.
    #[serde(default, skip_deserializing)]
    pub suggested_server_url: Option<String>,
    /// Kiosk mode: on unless switched off on this PC.
    #[serde(default, skip_deserializing)]
    pub kiosk: Option<bool>,
}

/// Set by `kiosk_exit` so the window may close.
struct ExitAllowed(AtomicBool);

/// A downloaded update waiting for the PIN screen to be idle.
struct PendingUpdate(Mutex<Option<(Update, Vec<u8>)>>);

/// The address the installer was given, in C:\ProgramData\Tausi POS\Terminal\server.txt.
fn installer_server_file() -> Option<PathBuf> {
    std::env::var_os("ProgramData").map(|d| {
        PathBuf::from(d)
            .join("Tausi POS")
            .join("Terminal")
            .join("server.txt")
    })
}

/// The first line of the installer's server file, if it is a usable address.
pub fn read_installer_server(path: &Path) -> Option<String> {
    let text = std::fs::read_to_string(path).ok()?;
    origin_of(text.lines().next()?.trim())
}

/// The paired server's origin ("http://pos-server:5080"), the only remote place the window may go.
struct AllowedServer(Mutex<Option<String>>);

fn load_config(app: &AppHandle) -> Result<DeviceConfig, String> {
    let store = app.store(STORE_FILE).map_err(|e| e.to_string())?;
    let device_token = match store
        .get("deviceTokenProtected")
        .and_then(|v| v.as_str().map(String::from))
    {
        Some(protected) => secrets::unprotect(&protected),
        None => store
            .get("deviceToken")
            .and_then(|v| v.as_str().map(String::from)),
    };
    Ok(DeviceConfig {
        server_url: store
            .get("serverUrl")
            .and_then(|v| v.as_str().map(String::from)),
        device_token,
        suggested_server_url: installer_server_file().and_then(|p| read_installer_server(&p)),
        kiosk: Some(kiosk_enabled(app)),
    })
}

/// Kiosk mode for this PC: the store's "kiosk" (default on), or TAUSI_KIOSK=0 to switch it off.
fn kiosk_enabled(app: &AppHandle) -> bool {
    if std::env::var("TAUSI_KIOSK").is_ok_and(|v| v == "0") {
        return false;
    }
    app.store(STORE_FILE)
        .ok()
        .and_then(|s| s.get("kiosk"))
        .and_then(|v| v.as_bool())
        .unwrap_or(true)
}

/// The device token is kept encrypted with Windows DPAPI (this Windows user, this PC).
fn store_config(app: &AppHandle, config: &DeviceConfig) -> Result<(), String> {
    let store = app.store(STORE_FILE).map_err(|e| e.to_string())?;
    match &config.server_url {
        Some(v) => store.set("serverUrl", v.clone()),
        None => {
            store.delete("serverUrl");
        }
    }
    store.delete("deviceToken");
    match &config.device_token {
        Some(v) => match secrets::protect(v) {
            Some(protected) => store.set("deviceTokenProtected", protected),
            None => store.set("deviceToken", v.clone()),
        },
        None => {
            store.delete("deviceTokenProtected");
        }
    }
    store.save().map_err(|e| e.to_string())
}

/// "http://host:port" from a server address, or None if it isn't a valid http(s) address.
pub fn origin_of(server: &str) -> Option<String> {
    let url = Url::parse(server).ok()?;
    if !matches!(url.scheme(), "http" | "https") || url.host_str().is_none() {
        return None;
    }
    Some(url.origin().ascii_serialization())
}

/// May the window show this address? The bundled pages, the dev server in development, and the
/// paired server; nothing else.
pub fn navigation_allowed(url: &Url, server_origin: Option<&str>, dev: bool) -> bool {
    match url.scheme() {
        "tauri" | "about" | "data" | "blob" => return true,
        _ => {}
    }
    let origin = url.origin().ascii_serialization();
    if origin == "http://tauri.localhost" || origin == "https://tauri.localhost" {
        return true;
    }
    if dev && (origin == "http://localhost:5173" || origin == "http://127.0.0.1:5173") {
        return true;
    }
    server_origin.is_some_and(|s| s == origin)
}

/// The address the window should show: the server's POS screens once paired, else the setup screen.
fn start_url(config: &DeviceConfig) -> Result<WebviewUrl, String> {
    // In development the window always loads the Vite dev server, which proxies to the server.
    if cfg!(debug_assertions) {
        return Ok(WebviewUrl::App("index.html".into()));
    }
    match (&config.server_url, &config.device_token) {
        (Some(server), Some(_)) => {
            let url = Url::parse(server)
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

/// Stores the device token after pairing. The server address can only be set by `set_server_url`.
#[tauri::command]
fn save_device_config(app: AppHandle, config: DeviceConfig) -> Result<(), String> {
    let current = load_config(&app)?;
    let server = current.server_url.or(config.server_url);
    store_config(
        &app,
        &DeviceConfig {
            server_url: server,
            device_token: config.device_token,
            ..Default::default()
        },
    )
}

/// Changes the server address (the setup screen asks for an admin-site login first). The device
/// token is dropped, and the app restarts so only the new server is allowed.
#[tauri::command]
fn set_server_url(app: AppHandle, server_url: String) -> Result<(), String> {
    let origin = origin_of(&server_url).ok_or("Enter an address like http://POS-SERVER:5080")?;
    store_config(
        &app,
        &DeviceConfig {
            server_url: Some(origin),
            device_token: None,
            ..Default::default()
        },
    )?;
    if cfg!(debug_assertions) {
        return Ok(());
    }
    app.restart();
}

/// Moving to a new server PC: the paired server sends its terminals to the new address, where the
/// restored database knows their device tokens. Keeps the pairing, and restarts so only the new server is allowed.
#[tauri::command]
fn move_server(app: AppHandle, server_url: String) -> Result<(), String> {
    let origin = origin_of(&server_url).ok_or("not a server address")?;
    let current = load_config(&app)?;
    store_config(
        &app,
        &DeviceConfig {
            server_url: Some(origin),
            device_token: current.device_token,
            ..Default::default()
        },
    )?;
    if cfg!(debug_assertions) {
        return Ok(());
    }
    app.restart();
}

/// Moves the window to the paired server's POS screens (restarts so the server is allowed in).
#[tauri::command]
fn open_server(app: AppHandle) -> Result<(), String> {
    if cfg!(debug_assertions) {
        let window = app
            .get_webview_window(MAIN_WINDOW)
            .ok_or("no main window")?;
        return window
            .eval("window.location.replace('/pos')")
            .map_err(|e| e.to_string());
    }
    app.restart();
}

/// Prints raw ESC/POS bytes on a printer plugged into this PC ("Via terminal" printers).
#[tauri::command]
async fn print_raw(printer: String, data_base64: String) -> Result<(), String> {
    let request = PrintRequest::from_base64(&printer, &data_base64).map_err(|e| e.to_string())?;
    tauri::async_runtime::spawn_blocking(move || tausi_print::print_raw(&request))
        .await
        .map_err(|e| e.to_string())?
        .map_err(|e: PrintError| e.to_string())
}

/// The printers installed on this PC, for the admin's "via terminal" printer list.
#[tauri::command]
async fn list_printers() -> Result<Vec<String>, String> {
    tauri::async_runtime::spawn_blocking(tausi_print::list_printers)
        .await
        .map_err(|e| e.to_string())?
        .map_err(|e| e.to_string())
}

#[tauri::command]
async fn check_printer(printer: String) -> Result<(), String> {
    tauri::async_runtime::spawn_blocking(move || tausi_print::check_printer(&printer))
        .await
        .map_err(|e| e.to_string())?
        .map_err(|e| e.to_string())
}

/// Switches kiosk mode on or off on this PC (after an admin-site login on the web side). Applies at the next start.
#[tauri::command]
fn kiosk_set(app: AppHandle, enabled: bool) -> Result<(), String> {
    let store = app.store(STORE_FILE).map_err(|e| e.to_string())?;
    store.set("kiosk", enabled);
    store.save().map_err(|e| e.to_string())
}

/// Closes the app (after the gesture and an admin-site login on the web side).
#[tauri::command]
fn kiosk_exit(app: AppHandle) {
    app.state::<ExitAllowed>().0.store(true, Ordering::SeqCst);
    app.exit(0);
}

/// Is a release newer than this app, and not newer than the server? (Server first.)
pub fn wanted(
    current: &semver::Version,
    release: &semver::Version,
    server: &semver::Version,
) -> bool {
    release > current && release <= server
}

/// Checks the paired server for a terminal update (never newer than the server) and downloads it.
/// Returns its version when one is ready to install.
#[tauri::command]
async fn update_check(app: AppHandle, server_version: String) -> Result<Option<String>, String> {
    if let Some((update, _)) = app
        .state::<PendingUpdate>()
        .0
        .lock()
        .map_err(|e| e.to_string())?
        .as_ref()
    {
        return Ok(Some(update.version.clone()));
    }
    let server = load_config(&app)?.server_url.ok_or("not paired")?;
    let server_version = semver::Version::parse(server_version.trim_start_matches('v'))
        .map_err(|e| e.to_string())?;
    let endpoint = Url::parse(&format!("{server}/api/updates/terminal/latest.json"))
        .map_err(|e| e.to_string())?;
    let update = app
        .updater_builder()
        .endpoints(vec![endpoint])
        .map_err(|e| e.to_string())?
        .version_comparator(move |current, release| {
            wanted(&current, &release.version, &server_version)
        })
        .build()
        .map_err(|e| e.to_string())?
        .check()
        .await
        .map_err(|e| e.to_string())?;
    let Some(update) = update else {
        return Ok(None);
    };
    let bytes = update
        .download(|_, _| {}, || {})
        .await
        .map_err(|e| e.to_string())?;
    let version = update.version.clone();
    *app.state::<PendingUpdate>()
        .0
        .lock()
        .map_err(|e| e.to_string())? = Some((update, bytes));
    Ok(Some(version))
}

/// Installs the downloaded update (the installer restarts the app). The web side calls it only on an idle PIN screen.
#[tauri::command]
fn update_install(app: AppHandle) -> Result<(), String> {
    let pending = app
        .state::<PendingUpdate>()
        .0
        .lock()
        .map_err(|e| e.to_string())?
        .take();
    let Some((update, bytes)) = pending else {
        return Err("No update is ready.".into());
    };
    app.state::<ExitAllowed>().0.store(true, Ordering::SeqCst);
    update.install(bytes).map_err(|e| e.to_string())
}

#[cfg_attr(mobile, tauri::mobile_entry_point)]
pub fn run() {
    tauri::Builder::default()
        // One app per PC: a second start (the Run key and a shortcut) just brings it to the front.
        .plugin(tauri_plugin_single_instance::init(|app, _, _| {
            if let Some(window) = app.get_webview_window(MAIN_WINDOW) {
                let _ = window.set_focus();
            }
        }))
        .plugin(tauri_plugin_store::Builder::new().build())
        .plugin(tauri_plugin_autostart::init(
            MacosLauncher::LaunchAgent,
            None,
        ))
        .plugin(tauri_plugin_updater::Builder::new().build())
        .invoke_handler(tauri::generate_handler![
            get_device_config,
            save_device_config,
            set_server_url,
            open_server,
            print_raw,
            list_printers,
            check_printer,
            kiosk_set,
            kiosk_exit,
            move_server,
            update_check,
            update_install
        ])
        .manage(ExitAllowed(AtomicBool::new(false)))
        .manage(PendingUpdate(Mutex::new(None)))
        // Kiosk mode: closing the window (Alt+F4 too) does nothing until kiosk_exit allows it.
        .on_window_event(|window, event| {
            if let WindowEvent::CloseRequested { api, .. } = event {
                let app = window.app_handle();
                if kiosk_enabled(app) && !app.state::<ExitAllowed>().0.load(Ordering::SeqCst) {
                    api.prevent_close();
                }
            }
        })
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
            let server_origin = config.server_url.as_deref().and_then(origin_of);

            // Only the paired server's pages may call the app's commands.
            if let Some(origin) = &server_origin {
                app.add_capability(
                    CapabilityBuilder::new("paired-server")
                        .remote(format!("{origin}/*"))
                        .window(MAIN_WINDOW)
                        .permission("core:default")
                        .permission("store:default")
                        // The order screen gives back its ticket's edit lock when the window
                        // closes, then closes the window itself.
                        .permission("core:window:allow-destroy"),
                )?;
            }
            app.manage(AllowedServer(Mutex::new(server_origin)));

            // Counter PCs in kiosk mode: full screen, no title bar. In development a normal 1280x800
            // window is easier to work with; set TAUSI_FULLSCREEN=1 to see the real thing.
            let full_screen = (!cfg!(debug_assertions) && kiosk_enabled(&handle))
                || env_var("TAUSI_FULLSCREEN", "NEXAGEN_FULLSCREEN").is_some();
            let guard = handle.clone();
            let window = WebviewWindowBuilder::new(app, MAIN_WINDOW, start_url(&config)?)
                .title(MAIN_WINDOW_TITLE)
                .inner_size(1280.0, 800.0)
                .fullscreen(full_screen)
                .decorations(!full_screen)
                .focused(true)
                .on_navigation(move |url| {
                    let allowed = guard.state::<AllowedServer>();
                    let server = allowed.0.lock().map(|s| s.clone()).unwrap_or(None);
                    navigation_allowed(url, server.as_deref(), cfg!(debug_assertions))
                })
                .build()?;
            // Take keyboard focus on start, so a cashier can type a PIN without touching the screen.
            let _ = window.set_focus();

            if !cfg!(debug_assertions) {
                // The installer starts the app for every user (the machine's Run key). Older
                // versions registered it for one user: remove that, so it doesn't start twice.
                if handle.autolaunch().is_enabled().unwrap_or(false) {
                    let _ = handle.autolaunch().disable();
                }
            }
            Ok(())
        })
        .run(tauri::generate_context!())
        .expect("error while running Tausi POS");
}

/// The device token at rest: Windows DPAPI for this user on this PC. Elsewhere (development) it stays as it is.
mod secrets {
    use base64::Engine;

    #[cfg(windows)]
    fn crypt(data: &[u8], protect: bool) -> Option<Vec<u8>> {
        use windows::Win32::Foundation::{LocalFree, HLOCAL};
        use windows::Win32::Security::Cryptography::{
            CryptProtectData, CryptUnprotectData, CRYPTPROTECT_UI_FORBIDDEN, CRYPT_INTEGER_BLOB,
        };
        let input = CRYPT_INTEGER_BLOB {
            cbData: data.len() as u32,
            pbData: data.as_ptr() as *mut u8,
        };
        let mut output = CRYPT_INTEGER_BLOB::default();
        unsafe {
            let result = if protect {
                CryptProtectData(
                    &input,
                    None,
                    None,
                    None,
                    None,
                    CRYPTPROTECT_UI_FORBIDDEN,
                    &mut output,
                )
            } else {
                CryptUnprotectData(
                    &input,
                    None,
                    None,
                    None,
                    None,
                    CRYPTPROTECT_UI_FORBIDDEN,
                    &mut output,
                )
            };
            result.ok()?;
            let bytes = std::slice::from_raw_parts(output.pbData, output.cbData as usize).to_vec();
            let _ = LocalFree(Some(HLOCAL(output.pbData as *mut core::ffi::c_void)));
            Some(bytes)
        }
    }

    /// "dpapi:<base64>" on Windows; None elsewhere (the caller keeps the plain token).
    pub fn protect(token: &str) -> Option<String> {
        #[cfg(windows)]
        {
            crypt(token.as_bytes(), true).map(|b| {
                format!(
                    "dpapi:{}",
                    base64::engine::general_purpose::STANDARD.encode(b)
                )
            })
        }
        #[cfg(not(windows))]
        {
            let _ = token;
            None
        }
    }

    pub fn unprotect(stored: &str) -> Option<String> {
        let encoded = stored.strip_prefix("dpapi:")?;
        let bytes = base64::engine::general_purpose::STANDARD
            .decode(encoded)
            .ok()?;
        #[cfg(windows)]
        {
            crypt(&bytes, false).and_then(|b| String::from_utf8(b).ok())
        }
        #[cfg(not(windows))]
        {
            let _ = bytes;
            None
        }
    }
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

    #[test]
    fn updates_are_never_newer_than_the_server() {
        let v = |s: &str| semver::Version::parse(s).unwrap();
        assert!(wanted(&v("1.0.0"), &v("1.1.0"), &v("1.1.0")));
        assert!(wanted(&v("1.0.0"), &v("1.1.0"), &v("1.2.0")));
        assert!(
            !wanted(&v("1.0.0"), &v("1.2.0"), &v("1.1.0")),
            "server first"
        );
        assert!(
            !wanted(&v("1.1.0"), &v("1.1.0"), &v("1.1.0")),
            "already on it"
        );
        assert!(
            !wanted(&v("1.2.0"), &v("1.1.0"), &v("1.2.0")),
            "never downgrades"
        );
    }

    #[test]
    fn the_installer_server_address_is_read_and_checked() {
        let root = temp_root("server");
        let file = root.join("server.txt");
        std::fs::write(&file, "http://192.168.1.10:5080/\r\n").unwrap();
        assert_eq!(
            read_installer_server(&file).as_deref(),
            Some("http://192.168.1.10:5080")
        );
        std::fs::write(&file, "not an address").unwrap();
        assert_eq!(read_installer_server(&file), None);
        assert_eq!(read_installer_server(&root.join("missing.txt")), None);
        std::fs::remove_dir_all(&root).unwrap();
    }

    #[test]
    fn a_token_that_cant_be_decrypted_is_not_used() {
        assert_eq!(secrets::unprotect("plain"), None);
        assert_eq!(secrets::unprotect("dpapi:%%%"), None);
    }

    #[test]
    fn origins_are_normalised() {
        assert_eq!(
            origin_of("http://POS-SERVER:5080/pos").as_deref(),
            Some("http://pos-server:5080")
        );
        assert_eq!(
            origin_of("https://pos.example/").as_deref(),
            Some("https://pos.example")
        );
        assert_eq!(origin_of("ftp://x"), None);
        assert_eq!(origin_of("not a url"), None);
    }

    #[test]
    fn only_the_paired_server_and_bundled_pages_are_allowed() {
        let server = Some("http://pos-server:5080");
        let ok = |u: &str, dev: bool| navigation_allowed(&Url::parse(u).unwrap(), server, dev);
        assert!(ok("http://pos-server:5080/pos", false));
        assert!(ok("http://tauri.localhost/connect", false));
        assert!(ok("tauri://localhost/index.html", false));
        assert!(!ok("http://pos-server:5081/pos", false));
        assert!(!ok("https://example.com", false));
        assert!(!ok("http://localhost:5173/pos", false));
        assert!(ok("http://localhost:5173/pos", true));
        assert!(!navigation_allowed(
            &Url::parse("http://pos-server:5080/").unwrap(),
            None,
            false
        ));
    }
}
