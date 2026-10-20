//! Windows: the print spooler in RAW mode, so the ESC/POS bytes reach the printer unchanged.

use crate::PrintError;
use windows::core::{HSTRING, PWSTR};
use windows::Win32::Graphics::Printing::{
    ClosePrinter, EndDocPrinter, EndPagePrinter, EnumPrintersW, OpenPrinterW, StartDocPrinterW,
    StartPagePrinter, WritePrinter, DOC_INFO_1W, PRINTER_ENUM_CONNECTIONS, PRINTER_ENUM_LOCAL,
    PRINTER_HANDLE, PRINTER_INFO_4W,
};

fn wide(s: &str) -> Vec<u16> {
    s.encode_utf16().chain(std::iter::once(0)).collect()
}

/// Closes the printer handle however we leave.
struct Printer(PRINTER_HANDLE);

impl Drop for Printer {
    fn drop(&mut self) {
        unsafe {
            let _ = ClosePrinter(self.0);
        }
    }
}

fn open(name: &str) -> Result<Printer, PrintError> {
    let mut handle = PRINTER_HANDLE::default();
    unsafe { OpenPrinterW(&HSTRING::from(name), &mut handle, None) }
        .map_err(|_| PrintError::NotFound(name.to_string()))?;
    Ok(Printer(handle))
}

pub fn print(name: &str, data: &[u8], document: &str) -> Result<(), PrintError> {
    let printer = open(name)?;
    let mut doc_name = wide(document);
    let mut data_type = wide("RAW");
    let info = DOC_INFO_1W {
        pDocName: PWSTR(doc_name.as_mut_ptr()),
        pOutputFile: PWSTR::null(),
        pDatatype: PWSTR(data_type.as_mut_ptr()),
    };
    unsafe {
        if StartDocPrinterW(printer.0, 1, &info) == 0 {
            return Err(last_error(&format!("{name} refused the print job")));
        }
        let result = (|| {
            if !StartPagePrinter(printer.0).as_bool() {
                return Err(last_error(&format!("{name} refused the print job")));
            }
            let mut written = 0u32;
            let ok = WritePrinter(printer.0, data.as_ptr().cast(), data.len() as u32, &mut written).as_bool();
            let _ = EndPagePrinter(printer.0);
            if !ok || written as usize != data.len() {
                return Err(last_error(&format!("Not everything reached {name}")));
            }
            Ok(())
        })();
        let _ = EndDocPrinter(printer.0);
        result
    }
}

pub fn list() -> Result<Vec<String>, PrintError> {
    let flags = PRINTER_ENUM_LOCAL | PRINTER_ENUM_CONNECTIONS;
    let mut needed = 0u32;
    let mut returned = 0u32;
    unsafe {
        // First call asks how big the buffer must be.
        let _ = EnumPrintersW(flags, None, 4, None, &mut needed, &mut returned);
        if needed == 0 {
            return Ok(Vec::new());
        }
        let mut buffer = vec![0u8; needed as usize];
        EnumPrintersW(flags, None, 4, Some(&mut buffer), &mut needed, &mut returned)
            .map_err(|e| PrintError::Failed(format!("Could not list the printers on this PC: {e}")))?;
        let infos = std::slice::from_raw_parts(buffer.as_ptr() as *const PRINTER_INFO_4W, returned as usize);
        Ok(infos
            .iter()
            .filter_map(|i| i.pPrinterName.to_string().ok())
            .collect())
    }
}

fn last_error(what: &str) -> PrintError {
    PrintError::Failed(format!("{what}: {}", windows::core::Error::from_thread().message()))
}
