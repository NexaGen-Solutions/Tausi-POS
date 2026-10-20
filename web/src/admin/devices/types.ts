export interface TerminalRow {
  id: string;
  name: string;
  status: 'Online' | 'Offline' | 'Revoked' | 'NotPaired';
  lastSeenAt: string | null;
  currentUser: string | null;
  appVersion: string | null;
  priceListId: string | null;
  priceListName: string | null;
  dispensePrinterId: string | null;
  dispensePrinterName: string | null;
  dispenseHeader: string | null;
  effectiveDispenseHeader: string;
  receiptPrinterId: string | null;
  receiptPrinterName: string | null;
  active: boolean;
  paired: boolean;
  localPrinters: string[];
  routingPreview: string;
  rowVersion: number;
}

export type ConnectionMode = 'Network9100' | 'WindowsShare' | 'ViaTerminal' | 'File';

export interface PrinterRow {
  id: string;
  name: string;
  connectionMode: ConnectionMode;
  address: string;
  owningTerminalId: string | null;
  owningTerminalName: string | null;
  connectionText: string;
  paperWidthMm: number;
  cutAfterSlip: boolean;
  openDrawerOnReceipt: boolean;
  active: boolean;
  status: 'Unknown' | 'Online' | 'Error';
  lastError: string | null;
  lastPrintedAt: string | null;
  failedJobs: number;
  usedBy: { terminal: string; roles: string[] }[];
  dispenseTemplate: string | null;
  rowVersion: number;
}

export interface PrintJobRow {
  id: string;
  createdAt: string;
  kind: 'Bill' | 'Dispense' | 'Void' | 'Receipt' | 'CashUp' | 'Test';
  ticketId: string | null;
  error: string | null;
  attempts: number;
  status: string;
  printerName: string;
}

/** Same words as the server's PrintRouting.Preview, for the live preview while editing. */
export function routingPreview(
  terminal: string,
  receipt: string | null,
  header: string,
  dispense: string | null,
) {
  return (
    `Print on ${terminal}: 1. Bill → ${receipt ?? 'no printer set'} · ` +
    `2. Dispense slip "${header}" → ${dispense ?? 'no printer set'}`
  );
}

export function ago(iso: string, now = Date.now()): string {
  const minutes = Math.max(0, Math.round((now - new Date(iso).getTime()) / 60_000));
  if (minutes < 1) return 'just now';
  if (minutes < 60) return `${minutes} min ago`;
  const hours = Math.round(minutes / 60);
  if (hours < 24) return `${hours} h ago`;
  return `${Math.round(hours / 24)} days ago`;
}
