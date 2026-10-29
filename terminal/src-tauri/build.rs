/// The app's own commands, declared so Tauri makes a permission for each (`allow-<command>`). Without
/// this, Tauri refuses every one of them to pages from the paired server (a remote origin), and the POS
/// screens can't read the device config: a white screen after the splash.
const COMMANDS: &[&str] = &[
    "get_device_config",
    "save_device_config",
    "set_server_url",
    "open_server",
    "print_raw",
    "list_printers",
    "check_printer",
    "kiosk_set",
    "kiosk_exit",
    "move_server",
    "update_check",
    "update_install",
];

fn main() {
    tauri_build::try_build(
        tauri_build::Attributes::new()
            .app_manifest(tauri_build::AppManifest::new().commands(COMMANDS)),
    )
    .expect("failed to run tauri-build");
}
