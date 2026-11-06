//! What the Tausi POS launcher decides, kept apart from Windows so it can be tested anywhere.
//!
//! The launcher (`tausi-launcher.exe`, in its own folder) is what the shortcuts and the sign-in entry
//! start. It starts the terminal app; rolls back to the previous version when a new one doesn't
//! start within 60 seconds; restarts the app when it stops unexpectedly; and offers Repair when the
//! app is missing, reinstalling it from the paired server. The app and the launcher share one small
//! state file per Windows user ([`state`]).

pub mod feed;
pub mod plan;
pub mod state;
