//! What to run, and what to do when it stops or doesn't start.

use std::time::Duration;

/// How long a new version gets to open its window before the launcher goes back to the previous one.
pub const START_TIMEOUT: Duration = Duration::from_secs(60);

/// Restarts allowed within [`RESTART_WINDOW`] before the launcher stops and offers Repair instead.
pub const MAX_RESTARTS: usize = 3;
pub const RESTART_WINDOW: Duration = Duration::from_secs(10 * 60);

#[derive(Debug, PartialEq, Eq, Clone, Copy)]
pub enum Run {
    /// The installed app.
    App,
    /// The copy of the version before the last update (`Terminal.previous`).
    Previous,
    /// Nothing that runs: offer Repair.
    Repair,
}

/// What the installed files look like now.
#[derive(Debug, Clone, Copy)]
pub struct Installed<'a> {
    pub app_exists: bool,
    pub previous_exists: bool,
    /// The app's size and date ([`build_of`]), to compare with a build that failed to start.
    pub app_build: Option<&'a str>,
    pub bad_build: Option<&'a str>,
}

/// The app if it's there and isn't the build that failed to start; else the previous version; else Repair.
pub fn choose(installed: Installed) -> Run {
    let failed_before = installed.app_build.is_some() && installed.app_build == installed.bad_build;
    if installed.app_exists && !failed_before {
        Run::App
    } else if installed.previous_exists {
        Run::Previous
    } else {
        Run::Repair
    }
}

/// An exe's size and modification time ("6754930:1730000000"): enough to notice a different build.
pub fn build_of(len: u64, modified_secs: u64) -> String {
    format!("{len}:{modified_secs}")
}

/// A new version that hasn't opened its window in time is given up only when there's one to go back to.
pub fn roll_back(ran: Run, started: bool, waited: Duration, previous_exists: bool) -> bool {
    ran == Run::App && !started && waited >= START_TIMEOUT && previous_exists
}

/// The times (seconds) of restarts after unexpected stops: one more is allowed unless there were
/// already [`MAX_RESTARTS`] in the last [`RESTART_WINDOW`].
pub fn may_restart(restarts: &[u64], now: u64) -> bool {
    restarts.iter().filter(|&&at| now.saturating_sub(at) < RESTART_WINDOW.as_secs()).count() < MAX_RESTARTS
}

#[cfg(test)]
mod tests {
    use super::*;

    fn installed(app: bool, previous: bool, build: Option<&'static str>, bad: Option<&'static str>) -> Installed<'static> {
        Installed { app_exists: app, previous_exists: previous, app_build: build, bad_build: bad }
    }

    #[test]
    fn runs_the_app_when_it_is_there() {
        assert_eq!(choose(installed(true, true, Some("1:1"), None)), Run::App);
        assert_eq!(choose(installed(true, false, Some("1:1"), Some("2:2"))), Run::App, "a different build from the one that failed");
    }

    #[test]
    fn an_update_cut_off_part_way_leaves_the_previous_version_working() {
        // The installer stopped after it removed the app (power cut, antivirus): the folder has no app.
        assert_eq!(choose(installed(false, true, None, None)), Run::Previous);
    }

    #[test]
    fn a_build_that_failed_to_start_runs_the_previous_version_until_another_is_installed() {
        assert_eq!(choose(installed(true, true, Some("9:9"), Some("9:9"))), Run::Previous);
    }

    #[test]
    fn nothing_to_run_offers_repair() {
        assert_eq!(choose(installed(false, false, None, None)), Run::Repair);
        assert_eq!(choose(installed(true, false, Some("9:9"), Some("9:9"))), Run::Repair);
    }

    #[test]
    fn a_new_version_that_does_not_start_in_60_seconds_rolls_back_when_it_can() {
        let late = START_TIMEOUT + Duration::from_secs(1);
        assert!(roll_back(Run::App, false, late, true));
        assert!(!roll_back(Run::App, false, Duration::from_secs(30), true), "still within the minute");
        assert!(!roll_back(Run::App, true, late, true), "it started");
        assert!(!roll_back(Run::App, false, late, false), "nothing to go back to: keep waiting");
        assert!(!roll_back(Run::Previous, false, late, true), "already the previous version");
    }

    #[test]
    fn restarts_stop_after_three_in_ten_minutes() {
        assert!(may_restart(&[], 1000));
        assert!(may_restart(&[900, 950], 1000));
        assert!(!may_restart(&[700, 900, 950], 1000));
        assert!(may_restart(&[10, 20, 950], 1000), "the old ones are over ten minutes ago");
    }
}
