//! The launcher on Windows: one per Windows session; starts the app and watches it.
//!
//! `tausi-launcher.exe` starts the app (or the previous version, or offers Repair: [`plan::choose`]),
//! gives a new version 60 seconds to open its window, restarts the app when it stops without saying
//! why, and recreates the sign-in entry and the desktop shortcut when they're gone.
//! `tausi-launcher.exe --repair --yes` repairs without asking; `--plan` prints what it would run
//! (App, Previous or Repair) and starts nothing. Both are for support and the installer test.

use std::path::{Path, PathBuf};
use std::process::{Child, Command};
use std::time::{Duration, Instant};

use tausi_launcher::feed;
use tausi_launcher::plan::{self, Installed, Run};
use tausi_launcher::state::{self, TerminalState};
use windows::core::{w, HSTRING, PCWSTR};
use windows::Win32::Foundation::{CloseHandle, GetLastError, ERROR_ALREADY_EXISTS, HANDLE};
use windows::Win32::System::Com::{CoCreateInstance, CoInitializeEx, CoTaskMemFree, IPersistFile, CLSCTX_INPROC_SERVER, COINIT_APARTMENTTHREADED};
use windows::Win32::System::Registry::{RegGetValueW, RegSetKeyValueW, HKEY, HKEY_CURRENT_USER, HKEY_LOCAL_MACHINE, REG_SZ, RRF_RT_REG_DWORD, RRF_RT_REG_SZ};
use windows::Win32::System::Threading::{CreateMutexW, WaitForSingleObject, INFINITE};
use windows::Win32::UI::Shell::{FOLDERID_Desktop, FOLDERID_PublicDesktop, IShellLinkW, SHGetKnownFolderPath, ShellExecuteExW, ShellLink, KF_FLAG_DEFAULT, SEE_MASK_NOCLOSEPROCESS, SHELLEXECUTEINFOW};
use windows::Win32::UI::WindowsAndMessaging::{MessageBoxW, IDOK, MB_ICONWARNING, MB_OKCANCEL, MB_SETFOREGROUND, MB_TOPMOST, SW_SHOWNORMAL};

const APP_EXE: &str = "tausi-terminal.exe";
const RUN_KEY: PCWSTR = w!("Software\\Microsoft\\Windows\\CurrentVersion\\Run");
const RUN_VALUE: PCWSTR = w!("Tausi POS");
const STORE: &str = "com.nexagensolutions.tausipos\\tausi.json";
/// Set by the installer while it replaces the app (hooks.nsh): don't start or restart it meanwhile.
const TERMINAL_KEY: PCWSTR = w!("Software\\NexaGen Solutions\\Tausi POS Terminal");
const INSTALL_WAIT: Duration = Duration::from_secs(10 * 60);

pub fn run() -> i32 {
    let args: Vec<String> = std::env::args().collect();
    let unattended = args.iter().any(|a| a == "--yes");
    let paths = Paths::find();
    let Some(state_file) = state::default_path() else { return 1 };

    if args.iter().any(|a| a == "--plan") {
        let st = TerminalState::load(&state_file);
        let build = build_of(&paths.app());
        println!("{:?}", plan::choose(Installed {
            app_exists: paths.app().exists(),
            previous_exists: paths.previous_app().exists(),
            app_build: build.as_deref(),
            bad_build: st.bad_build.as_deref(),
        }));
        return 0;
    }

    if args.iter().any(|a| a == "--repair") {
        return match repair(&paths) {
            Ok(()) => 0,
            Err(e) => {
                if !unattended {
                    say(&format!("Tausi POS couldn't be repaired.\n\n{e}"));
                }
                eprintln!("{e}");
                1
            }
        };
    }

    // One launcher per Windows session: a second start (a shortcut) just brings the app to the front.
    if !first_launcher() {
        let _ = Command::new(paths.app()).spawn();
        return 0;
    }
    keep_sign_in_entry_and_shortcut(&paths);

    let mut restarts: Vec<u64> = Vec::new();
    loop {
        wait_while_installing();
        let st = TerminalState::load(&state_file);
        let build = build_of(&paths.app());
        let choice = plan::choose(Installed {
            app_exists: paths.app().exists(),
            previous_exists: paths.previous_app().exists(),
            app_build: build.as_deref(),
            bad_build: st.bad_build.as_deref(),
        });
        let exe = match choice {
            Run::App => paths.app(),
            Run::Previous => paths.previous_app(),
            Run::Repair => {
                if unattended || !ask_repair() {
                    return 1;
                }
                if let Err(e) = repair(&paths) {
                    say(&format!("Tausi POS couldn't be repaired.\n\n{e}\n\nAsk NexaGen Solutions for help, or reinstall it with TausiPOS-Setup.exe."));
                    return 1;
                }
                continue;
            }
        };

        let launched_at = state::now();
        let Ok(mut child) = Command::new(&exe).spawn() else {
            if choice == Run::App && paths.previous_app().exists() {
                mark_rolled_back(&state_file, build.as_deref(), "Windows couldn't start it (blocked or damaged).");
                continue;
            }
            say("Tausi POS couldn't be started. Repair it, or reinstall it with TausiPOS-Setup.exe.");
            return 1;
        };

        // A new version has 60 seconds to open its window; else back to the previous one.
        let start = Instant::now();
        let mut started = false;
        let mut code = None;
        while code.is_none() && !started {
            std::thread::sleep(Duration::from_millis(500));
            started = TerminalState::load(&state_file).started_at.is_some_and(|at| at >= launched_at);
            code = child.try_wait().ok().flatten().map(|s| s.code().unwrap_or(-1));
            if plan::roll_back(choice, started, start.elapsed(), paths.previous_app().exists()) {
                let _ = child.kill();
                mark_rolled_back(&state_file, build.as_deref(), "It didn't start within 60 seconds.");
                break;
            }
        }
        if !started && code.is_none() && start.elapsed() >= plan::START_TIMEOUT && choice == Run::App {
            continue; // rolled back: run the previous version
        }

        let code = match code {
            Some(c) => c,
            None => wait(&mut child),
        };
        let after = TerminalState::load(&state_file);
        if !after.stopped_unexpectedly(launched_at) {
            return 0; // closed on purpose, or closed to update (the installer starts the new version)
        }
        let now = state::now();
        let _ = TerminalState::change(&state_file, |s| s.exited(&format!("Stopped unexpectedly (exit code {code})"), now));
        if !plan::may_restart(&restarts, now) {
            if unattended || !ask_repair() {
                return 1;
            }
            let _ = repair(&paths);
            restarts.clear();
            continue;
        }
        restarts.push(now);
        std::thread::sleep(Duration::from_secs(3));
    }
}

/// While an installer is replacing the app, wait (at most 10 minutes: one cut off leaves the mark behind).
fn wait_while_installing() {
    let start = Instant::now();
    while installing() && start.elapsed() < INSTALL_WAIT {
        std::thread::sleep(Duration::from_secs(2));
    }
}

fn installing() -> bool {
    let mut value = 0u32;
    let mut size = 4u32;
    unsafe {
        RegGetValueW(HKEY_LOCAL_MACHINE, TERMINAL_KEY, w!("Installing"), RRF_RT_REG_DWORD, None, Some(&mut value as *mut u32 as *mut _), Some(&mut size))
            .is_ok()
            && value != 0
    }
}

fn wait(child: &mut Child) -> i32 {
    child.wait().ok().and_then(|s| s.code()).unwrap_or(-1)
}

fn mark_rolled_back(state_file: &Path, build: Option<&str>, error: &str) {
    let now = state::now();
    let _ = TerminalState::change(state_file, |s| s.rolled_back(build.unwrap_or("missing"), error, now));
}

fn build_of(exe: &Path) -> Option<String> {
    let meta = std::fs::metadata(exe).ok()?;
    let modified = meta.modified().ok()?.duration_since(std::time::UNIX_EPOCH).ok()?.as_secs();
    Some(plan::build_of(meta.len(), modified))
}

/// Where things are: the app's folder (from the installer's registry entry), the previous version
/// beside it (`Terminal.previous`, kept by the installer), and this launcher.
struct Paths {
    install: PathBuf,
}

impl Paths {
    fn find() -> Paths {
        let install = read_string(HKEY_LOCAL_MACHINE, w!("Software\\NexaGen Solutions\\Tausi POS"), PCWSTR::null())
            .map(PathBuf::from)
            .unwrap_or_else(|| PathBuf::from(std::env::var("ProgramFiles").unwrap_or_else(|_| "C:\\Program Files".into())).join("Tausi POS").join("Terminal"));
        Paths { install }
    }
    fn app(&self) -> PathBuf {
        self.install.join(APP_EXE)
    }
    fn previous_app(&self) -> PathBuf {
        let mut previous = self.install.clone().into_os_string();
        previous.push(".previous");
        PathBuf::from(previous).join(APP_EXE)
    }
}

/// Downloads the terminal installer from the paired server, checks NexaGen's signature, and runs it
/// (Windows asks for an administrator). The pairing lives in the user's app data, so it's kept.
fn repair(_paths: &Paths) -> Result<(), String> {
    let server = paired_server().ok_or("This PC isn't paired with a Tausi POS server, so there's nowhere to repair it from.")?;
    let temp = std::env::temp_dir().join("tausi-repair");
    std::fs::create_dir_all(&temp).map_err(|e| e.to_string())?;
    let feed_file = temp.join("latest.json");
    download(&format!("{server}/api/updates/terminal/latest.json"), &feed_file)?;
    let offer = feed::parse(&std::fs::read(&feed_file).unwrap_or_default())
        .ok_or_else(|| format!("The server at {server} has no terminal installer to repair from."))?;
    let installer = temp.join("TausiPOS-Setup.exe");
    download(&offer.url, &installer)?;
    let bytes = std::fs::read(&installer).map_err(|e| e.to_string())?;
    if let Err(e) = feed::verify(&bytes, &offer.signature, feed::UPDATER_PUBLIC_KEY) {
        let _ = std::fs::remove_file(&installer);
        return Err(e);
    }
    run_as_administrator(&installer, "/P")?;
    let _ = std::fs::remove_file(&installer);
    Ok(())
}

fn paired_server() -> Option<String> {
    let store = std::env::var_os("APPDATA").map(|d| PathBuf::from(d).join(STORE))?;
    std::fs::read(store).ok().and_then(|json| feed::server_of_store(&json)).or_else(|| {
        // Not paired yet: the address the installer was given (SERVER=...).
        let file = PathBuf::from(std::env::var_os("ProgramData")?).join("Tausi POS").join("Terminal").join("server.txt");
        std::fs::read_to_string(file).ok().map(|s| s.trim().trim_end_matches('/').to_string()).filter(|s| !s.is_empty())
    })
}

/// curl.exe comes with Windows 10 and 11: no web library in the launcher.
fn download(url: &str, to: &Path) -> Result<(), String> {
    let status = Command::new("curl.exe")
        .args(["--fail", "--silent", "--show-error", "--location", "--max-time", "600", "--output"])
        .arg(to)
        .arg(url)
        .status()
        .map_err(|e| format!("Couldn't download {url}: {e}"))?;
    if status.success() { Ok(()) } else { Err(format!("Couldn't download {url} from the server.")) }
}

fn run_as_administrator(exe: &Path, args: &str) -> Result<(), String> {
    let file = HSTRING::from(exe.as_os_str());
    let params = HSTRING::from(args);
    let mut info = SHELLEXECUTEINFOW {
        cbSize: std::mem::size_of::<SHELLEXECUTEINFOW>() as u32,
        fMask: SEE_MASK_NOCLOSEPROCESS,
        lpVerb: w!("runas"),
        lpFile: PCWSTR(file.as_ptr()),
        lpParameters: PCWSTR(params.as_ptr()),
        nShow: SW_SHOWNORMAL.0,
        ..Default::default()
    };
    unsafe {
        ShellExecuteExW(&mut info).map_err(|_| "The repair needs an administrator, and it wasn't allowed.".to_string())?;
        if !info.hProcess.is_invalid() {
            WaitForSingleObject(info.hProcess, INFINITE);
            let _ = CloseHandle(info.hProcess);
        }
    }
    Ok(())
}

/// The installer's sign-in entry (for every user) or, if it's gone, one for this user; and a desktop
/// shortcut for this user when neither desktop has one.
fn keep_sign_in_entry_and_shortcut(paths: &Paths) {
    let Ok(me) = std::env::current_exe() else { return };
    let command = format!("\"{}\"", me.display());
    if read_string(HKEY_LOCAL_MACHINE, RUN_KEY, RUN_VALUE).is_none() && read_string(HKEY_CURRENT_USER, RUN_KEY, RUN_VALUE).is_none() {
        write_string(HKEY_CURRENT_USER, RUN_KEY, RUN_VALUE, &command);
    }
    let desktops = [known_folder(&FOLDERID_PublicDesktop), known_folder(&FOLDERID_Desktop)];
    if desktops.iter().flatten().all(|d| !d.join("Tausi POS.lnk").exists()) {
        if let Some(desktop) = &desktops[1] {
            let _ = shortcut(&me, &desktop.join("Tausi POS.lnk"), &paths.app());
        }
    }
}

fn shortcut(target: &Path, link: &Path, icon: &Path) -> windows::core::Result<()> {
    unsafe {
        let _ = CoInitializeEx(None, COINIT_APARTMENTTHREADED);
        let shell: IShellLinkW = CoCreateInstance(&ShellLink, None, CLSCTX_INPROC_SERVER)?;
        shell.SetPath(&HSTRING::from(target.as_os_str()))?;
        shell.SetIconLocation(&HSTRING::from(icon.as_os_str()), 0)?;
        shell.SetDescription(w!("Tausi POS"))?;
        let file: IPersistFile = windows::core::Interface::cast(&shell)?;
        file.Save(&HSTRING::from(link.as_os_str()), true)
    }
}

fn known_folder(id: &windows::core::GUID) -> Option<PathBuf> {
    unsafe {
        let path = SHGetKnownFolderPath(id, KF_FLAG_DEFAULT, None).ok()?;
        let result = path.to_string().ok().map(PathBuf::from);
        CoTaskMemFree(Some(path.0 as *const _));
        result
    }
}

fn read_string(root: HKEY, key: PCWSTR, value: PCWSTR) -> Option<String> {
    let mut buffer = vec![0u16; 1024];
    let mut size = (buffer.len() * 2) as u32;
    unsafe {
        RegGetValueW(root, key, value, RRF_RT_REG_SZ, None, Some(buffer.as_mut_ptr() as *mut _), Some(&mut size)).ok().ok()?;
    }
    let len = (size as usize / 2).saturating_sub(1);
    Some(String::from_utf16_lossy(&buffer[..len])).filter(|s| !s.is_empty())
}

fn write_string(root: HKEY, key: PCWSTR, value: PCWSTR, data: &str) {
    let wide: Vec<u16> = data.encode_utf16().chain(std::iter::once(0)).collect();
    unsafe {
        let _ = RegSetKeyValueW(root, key, value, REG_SZ.0, Some(wide.as_ptr() as *const _), (wide.len() * 2) as u32);
    }
}

fn first_launcher() -> bool {
    unsafe {
        let mutex: windows::core::Result<HANDLE> = CreateMutexW(None, true, w!("Local\\TausiPosLauncher"));
        // Kept open for the launcher's life: Windows releases it when the process ends.
        mutex.is_ok() && GetLastError() != ERROR_ALREADY_EXISTS
    }
}

fn ask_repair() -> bool {
    let answer = unsafe {
        MessageBoxW(
            None,
            w!("Tausi POS needs repairing.\n\nThe app is missing or keeps stopping. Press OK to repair it: it is downloaded again from your Tausi POS server and keeps this counter's pairing. Windows will ask for an administrator."),
            w!("Tausi POS"),
            MB_OKCANCEL | MB_ICONWARNING | MB_TOPMOST | MB_SETFOREGROUND,
        )
    };
    answer == IDOK
}

fn say(text: &str) {
    unsafe {
        MessageBoxW(None, &HSTRING::from(text), w!("Tausi POS"), MB_ICONWARNING | MB_TOPMOST | MB_SETFOREGROUND);
    }
}
