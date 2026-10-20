//! Raw ESC/POS printing for Tausi Terminal.
//!
//! The bytes reach the printer unchanged:
//! - Windows: the spooler in RAW mode (`OpenPrinter` / `StartDocPrinter` "RAW" / `WritePrinter`).
//! - Linux (development): `lp -d <printer> -o raw`.
//! - Anywhere: a printer named `file:<path>` writes the bytes to that file (for testing).

use base64::Engine;
use std::fmt;

#[cfg(windows)]
mod windows_spooler;
#[cfg(not(windows))]
mod unix_lp;

/// What to print and where, as the server sends it.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct PrintRequest {
    pub printer: String,
    pub data: Vec<u8>,
}

/// Why printing failed, in words a cashier or admin can act on.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum PrintError {
    NoPrinterName,
    NothingToPrint,
    BadData(String),
    NotFound(String),
    Failed(String),
    Unsupported(String),
}

impl fmt::Display for PrintError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            PrintError::NoPrinterName => write!(f, "No printer name was given."),
            PrintError::NothingToPrint => write!(f, "There was nothing to print."),
            PrintError::BadData(why) => write!(f, "The print job was damaged on the way ({why})."),
            PrintError::NotFound(name) => write!(f, "There is no printer called \"{name}\" on this PC."),
            PrintError::Failed(why) => write!(f, "{why}"),
            PrintError::Unsupported(why) => write!(f, "{why}"),
        }
    }
}

impl std::error::Error for PrintError {}

impl PrintRequest {
    /// Builds a request from the server's message (the bytes come base64-encoded).
    pub fn from_base64(printer: &str, data_base64: &str) -> Result<Self, PrintError> {
        let printer = printer.trim();
        if printer.is_empty() {
            return Err(PrintError::NoPrinterName);
        }
        let data = base64::engine::general_purpose::STANDARD
            .decode(data_base64.trim())
            .map_err(|e| PrintError::BadData(e.to_string()))?;
        if data.is_empty() {
            return Err(PrintError::NothingToPrint);
        }
        Ok(Self { printer: printer.to_string(), data })
    }

    /// `file:<path>` printers write to a file instead (testing without a printer).
    pub fn file_target(&self) -> Option<&str> {
        self.printer.strip_prefix("file:").filter(|p| !p.is_empty())
    }
}

/// Sends the bytes to the printer.
pub fn print_raw(request: &PrintRequest) -> Result<(), PrintError> {
    if let Some(path) = request.file_target() {
        return std::fs::write(path, &request.data)
            .map_err(|e| PrintError::Failed(format!("Could not write to {path}: {e}")));
    }
    #[cfg(windows)]
    {
        windows_spooler::print(&request.printer, &request.data, "Tausi POS")
    }
    #[cfg(not(windows))]
    {
        unix_lp::print(&request.printer, &request.data)
    }
}

/// The printers installed on this PC, by name.
pub fn list_printers() -> Result<Vec<String>, PrintError> {
    #[cfg(windows)]
    {
        windows_spooler::list()
    }
    #[cfg(not(windows))]
    {
        unix_lp::list()
    }
}

/// Is there a printer by that name here? (`file:` printers always "exist".)
pub fn check_printer(name: &str) -> Result<(), PrintError> {
    let name = name.trim();
    if name.is_empty() {
        return Err(PrintError::NoPrinterName);
    }
    if name.starts_with("file:") {
        return Ok(());
    }
    let printers = list_printers()?;
    if printers.iter().any(|p| p.eq_ignore_ascii_case(name)) {
        Ok(())
    } else {
        Err(PrintError::NotFound(name.to_string()))
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn builds_a_request_from_the_servers_message() {
        let req = PrintRequest::from_base64(" POS-80 ", "G0BIaQo=").unwrap();
        assert_eq!(req.printer, "POS-80");
        assert_eq!(req.data, vec![0x1B, 0x40, b'H', b'i', b'\n']);
    }

    #[test]
    fn refuses_a_missing_printer_name() {
        assert_eq!(PrintRequest::from_base64("  ", "G0A="), Err(PrintError::NoPrinterName));
    }

    #[test]
    fn refuses_damaged_or_empty_data() {
        assert!(matches!(PrintRequest::from_base64("P", "not base64!"), Err(PrintError::BadData(_))));
        assert_eq!(PrintRequest::from_base64("P", ""), Err(PrintError::NothingToPrint));
    }

    #[test]
    fn file_printers_write_the_bytes() {
        let path = std::env::temp_dir().join(format!("tausi-print-test-{}.bin", std::process::id()));
        let req = PrintRequest { printer: format!("file:{}", path.display()), data: vec![1, 2, 3] };
        print_raw(&req).unwrap();
        assert_eq!(std::fs::read(&path).unwrap(), vec![1, 2, 3]);
        std::fs::remove_file(path).ok();
        assert!(check_printer("file:/tmp/x").is_ok());
    }

    #[test]
    fn errors_read_plainly() {
        assert_eq!(PrintError::NotFound("POS-80".into()).to_string(), "There is no printer called \"POS-80\" on this PC.");
        assert_eq!(PrintError::NoPrinterName.to_string(), "No printer name was given.");
    }
}
