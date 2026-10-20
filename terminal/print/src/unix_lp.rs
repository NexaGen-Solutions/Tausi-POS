//! Linux and macOS (development): print with CUPS's `lp` in raw mode.

use crate::PrintError;
use std::io::{ErrorKind, Write};
use std::process::{Command, Stdio};

pub fn print(printer: &str, data: &[u8]) -> Result<(), PrintError> {
    let mut child = Command::new("lp")
        .args(["-d", printer, "-o", "raw"])
        .stdin(Stdio::piped())
        .stdout(Stdio::null())
        .stderr(Stdio::piped())
        .spawn()
        .map_err(spawn_error)?;
    child
        .stdin
        .take()
        .expect("stdin is piped")
        .write_all(data)
        .map_err(|e| PrintError::Failed(format!("Could not send the job to lp: {e}")))?;
    let output = child
        .wait_with_output()
        .map_err(|e| PrintError::Failed(format!("lp did not finish: {e}")))?;
    if output.status.success() {
        Ok(())
    } else {
        Err(lp_failure(printer, &String::from_utf8_lossy(&output.stderr)))
    }
}

pub fn list() -> Result<Vec<String>, PrintError> {
    let output = Command::new("lpstat").arg("-e").output().map_err(spawn_error)?;
    if !output.status.success() {
        // No printers set up is not an error: the list is just empty.
        return Ok(Vec::new());
    }
    Ok(String::from_utf8_lossy(&output.stdout)
        .lines()
        .map(str::trim)
        .filter(|l| !l.is_empty())
        .map(String::from)
        .collect())
}

/// What a failure to start lp/lpstat means.
pub fn spawn_error(e: std::io::Error) -> PrintError {
    if e.kind() == ErrorKind::NotFound {
        PrintError::Unsupported("Printing needs CUPS (the lp command), which is not installed on this PC.".into())
    } else {
        PrintError::Failed(format!("Could not start lp: {e}"))
    }
}

/// What lp's complaint means.
pub fn lp_failure(printer: &str, stderr: &str) -> PrintError {
    let message = stderr.trim();
    if message.contains("does not exist") || message.contains("Unknown destination") {
        PrintError::NotFound(printer.to_string())
    } else if message.is_empty() {
        PrintError::Failed(format!("{printer} did not take the job."))
    } else {
        PrintError::Failed(format!("{printer} did not take the job: {message}"))
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn a_missing_lp_is_explained() {
        let error = spawn_error(std::io::Error::from(ErrorKind::NotFound));
        assert!(matches!(error, PrintError::Unsupported(_)));
        assert!(error.to_string().contains("CUPS"));
    }

    #[test]
    fn an_unknown_printer_is_not_found() {
        assert_eq!(
            lp_failure("POS-80", "lp: The printer or class does not exist.\n"),
            PrintError::NotFound("POS-80".into())
        );
    }

    #[test]
    fn other_failures_keep_lps_words() {
        assert_eq!(
            lp_failure("POS-80", "lp: Error - scheduler not responding.\n").to_string(),
            "POS-80 did not take the job: lp: Error - scheduler not responding."
        );
        assert_eq!(lp_failure("POS-80", "").to_string(), "POS-80 did not take the job.");
    }
}
