#![cfg_attr(windows, windows_subsystem = "windows")]

#[cfg(windows)]
mod windows_main;

#[cfg(windows)]
fn main() {
    std::process::exit(windows_main::run());
}

/// The launcher is for Windows counter PCs; elsewhere the app runs directly.
#[cfg(not(windows))]
fn main() {
    eprintln!("tausi-launcher runs on Windows only.");
    std::process::exit(2);
}
