//! The terminal's health, shared by the app and the launcher (both run as the counter's Windows user):
//! `%LOCALAPPDATA%\Tausi POS\Terminal\state.json`. The app reports it to the server, which shows it
//! on the Terminals page.

use serde::{Deserialize, Serialize};
use std::path::{Path, PathBuf};
use std::time::{SystemTime, UNIX_EPOCH};

/// Why the app last stopped. The app writes the expected ones; the launcher writes the rest.
pub mod exit {
    pub const CLOSED: &str = "Closed";
    pub const CLOSED_BY_ADMIN: &str = "Closed by an admin";
    pub const UPDATE: &str = "Closed to update";
}

/// How the last update went.
pub mod update {
    /// The installer is running (or was cut off): the new version hasn't started yet.
    pub const INSTALLING: &str = "Installing";
    pub const SUCCESS: &str = "Success";
    pub const ROLLED_BACK: &str = "Rolled back";
    pub const FAILED: &str = "Failed";
}

#[derive(Serialize, Deserialize, Default, Clone, Debug, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct Exit {
    pub reason: String,
    pub at: u64,
}

#[derive(Serialize, Deserialize, Default, Clone, Debug, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct UpdateResult {
    pub from: String,
    pub to: String,
    pub result: String,
    pub error: Option<String>,
    pub at: u64,
}

#[derive(Serialize, Deserialize, Default, Clone, Debug, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct TerminalState {
    /// The version that last started.
    pub version: Option<String>,
    /// When the app last started (its window opened), seconds since 1970.
    pub started_at: Option<u64>,
    pub last_exit: Option<Exit>,
    pub last_update: Option<UpdateResult>,
    /// The installed app's size and date when it failed to start: the launcher runs the previous
    /// version until a different one is installed.
    pub bad_build: Option<String>,
}

pub fn now() -> u64 {
    SystemTime::now().duration_since(UNIX_EPOCH).map(|d| d.as_secs()).unwrap_or(0)
}

/// `%LOCALAPPDATA%\Tausi POS\Terminal\state.json` (the user's own folder: no administrator needed).
pub fn default_path() -> Option<PathBuf> {
    std::env::var_os("LOCALAPPDATA").map(|d| PathBuf::from(d).join("Tausi POS").join("Terminal").join("state.json"))
}

impl TerminalState {
    pub fn load(path: &Path) -> TerminalState {
        std::fs::read(path).ok().and_then(|bytes| serde_json::from_slice(&bytes).ok()).unwrap_or_default()
    }

    /// Written whole to a temporary file, then moved over the old one, so a power cut never leaves half a file.
    pub fn save(&self, path: &Path) -> std::io::Result<()> {
        if let Some(dir) = path.parent() {
            std::fs::create_dir_all(dir)?;
        }
        let temp = path.with_extension("json.tmp");
        std::fs::write(&temp, serde_json::to_vec_pretty(self).unwrap_or_default())?;
        std::fs::rename(temp, path)
    }

    /// Loads, changes and saves the state (both programs change it; each change is small and whole).
    pub fn change(path: &Path, f: impl FnOnce(&mut TerminalState)) -> std::io::Result<TerminalState> {
        let mut state = TerminalState::load(path);
        f(&mut state);
        state.save(path)?;
        Ok(state)
    }

    /// The app started: an update that was installing this version succeeded.
    pub fn started(&mut self, version: &str, at: u64) {
        self.version = Some(version.to_string());
        self.started_at = Some(at);
        if let Some(u) = &mut self.last_update {
            if u.result == update::INSTALLING && u.to == version {
                u.result = update::SUCCESS.to_string();
                u.error = None;
                u.at = at;
            }
        }
    }

    pub fn exited(&mut self, reason: &str, at: u64) {
        self.last_exit = Some(Exit { reason: reason.to_string(), at });
    }

    /// About to run the installer of `to`: if it never finishes, the launcher finds "Installing".
    pub fn installing(&mut self, from: &str, to: &str, at: u64) {
        self.last_update = Some(UpdateResult {
            from: from.to_string(),
            to: to.to_string(),
            result: update::INSTALLING.to_string(),
            error: None,
            at,
        });
        self.exited(exit::UPDATE, at);
    }

    /// The new version didn't start: the launcher went back to the previous one.
    pub fn rolled_back(&mut self, bad_build: &str, error: &str, at: u64) {
        self.bad_build = Some(bad_build.to_string());
        let u = self.last_update.get_or_insert_with(Default::default);
        u.result = update::ROLLED_BACK.to_string();
        u.error = Some(error.to_string());
        u.at = at;
    }

    /// The app stopped by itself after `launched_at`, without saying why.
    pub fn stopped_unexpectedly(&self, launched_at: u64) -> bool {
        !self.last_exit.as_ref().is_some_and(|e| e.at >= launched_at)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn temp(name: &str) -> PathBuf {
        let dir = std::env::temp_dir().join(format!("tausi-launcher-state-{name}-{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&dir);
        dir.join("state.json")
    }

    #[test]
    fn saves_and_loads_and_a_missing_or_broken_file_is_empty() {
        let path = temp("roundtrip");
        assert_eq!(TerminalState::load(&path), TerminalState::default());
        let mut state = TerminalState::default();
        state.started("1.0.1", 10);
        state.save(&path).unwrap();
        assert_eq!(TerminalState::load(&path), state);
        std::fs::write(&path, b"{ not json").unwrap();
        assert_eq!(TerminalState::load(&path), TerminalState::default());
    }

    #[test]
    fn starting_the_version_being_installed_marks_the_update_a_success() {
        let mut state = TerminalState::default();
        state.installing("1.0.0", "1.0.1", 5);
        assert_eq!(state.last_exit.as_ref().unwrap().reason, exit::UPDATE);
        state.started("1.0.0", 9);
        assert_eq!(state.last_update.as_ref().unwrap().result, update::INSTALLING, "the old version came back, not the new one");
        state.started("1.0.1", 12);
        assert_eq!(state.last_update.as_ref().unwrap().result, update::SUCCESS);
    }

    #[test]
    fn an_exit_counts_only_when_it_was_written_after_the_launch() {
        let mut state = TerminalState::default();
        assert!(state.stopped_unexpectedly(100));
        state.exited(exit::CLOSED_BY_ADMIN, 90);
        assert!(state.stopped_unexpectedly(100), "an exit from an earlier run");
        state.exited(exit::CLOSED_BY_ADMIN, 150);
        assert!(!state.stopped_unexpectedly(100));
    }

    #[test]
    fn a_rollback_keeps_what_was_being_installed_and_says_why() {
        let mut state = TerminalState::default();
        state.installing("1.0.0", "1.0.1", 5);
        state.rolled_back("6754930:1730000000", "It didn't start within 60 seconds.", 70);
        let u = state.last_update.unwrap();
        assert_eq!((u.from.as_str(), u.to.as_str(), u.result.as_str()), ("1.0.0", "1.0.1", update::ROLLED_BACK));
        assert_eq!(state.bad_build.as_deref(), Some("6754930:1730000000"));
    }
}
