export interface LockInfo {
  terminalId: string;
  terminal: string;
  userId: string;
  user: string;
}

export interface TicketSummary {
  id: string;
  number: number;
  tableId: string | null;
  customerName: string | null;
  openedById: string;
  openedBy: string;
  total: number;
  openedAt: string;
  lastActivityAt: string;
  mine: boolean;
  lock: LockInfo | null;
  /** Lines saved but not yet printed. */
  newLines: number;
  /** Taken so far while settling (pending payments). */
  paid?: number;
  /** "Part paid · KSh 370 left": what's left when some of it is paid. */
  left?: number | null;
}

export interface Area {
  id: string;
  name: string;
  sortOrder: number;
}

export interface TableTile {
  id: string;
  name: string;
  areaId: string;
  sortOrder: number;
  tickets: TicketSummary[];
  idleMinutes: number | null;
}

export interface TableMap {
  version: number;
  serverTime: string;
  dayOpen: boolean;
  tableIdleMinutes: number;
  areas: Area[];
  tables: TableTile[];
}

/** Payload of ticket.changed and ticket.lock (small: the details are fetched). */
export interface TableEvent {
  ticketId: string;
  tableId: string | null;
  version: number;
  terminalId?: string | null;
}

export interface OrderLine {
  id: string;
  menuItemId: string | null;
  name: string;
  shortName: string;
  options: string | null;
  note: string | null;
  quantity: number;
  unitPrice: number;
  gross: number;
  status: 'New' | 'Printed' | 'Cancelled' | 'Voided';
  printRound: number | null;
  addedById: string;
  addedBy: string;
  addedOn: string;
  addedAt: string;
  printedAt: string | null;
  voided: boolean;
  /** Given free: shown as 0 with the price struck through. */
  isGift?: boolean;
  /** The price before a change on this ticket ("Price changed"). */
  originalUnitPrice?: number | null;
  /** This line's share of the ticket discount. */
  discount?: number;
  voidReason?: string | null;
  /** A request waiting for approval: "Void pending". */
  pending?: PendingRequest | null;
}

export interface PendingRequest {
  id: string;
  kind: 'Void' | 'Gift' | 'Discount' | 'PriceChange' | 'Reprint' | 'Move' | 'Merge';
  summary: string;
  requestedBy: string;
  mine: boolean;
}

export interface FailedJob {
  id: string;
  kind: 'Bill' | 'Dispense' | string;
  printerName: string;
  error: string | null;
}

export interface TicketDetail {
  id: string;
  number: number;
  status: string;
  tableId: string | null;
  tableName: string | null;
  customerName: string | null;
  openedBy: string;
  openedAt: string;
  lastActivityAt: string;
  note: string;
  total: number;
  lines: OrderLine[];
  lock: LockInfo | null;
  rowVersion: number;
  billNumber: number;
  newLines: number;
  failedJobs: FailedJob[];
  subtotal?: number;
  discountTotal?: number;
  discountLabel?: string | null;
  /** Ticket-level requests waiting: "Discount pending", "Reprint pending". */
  pending?: PendingRequest[];
  payments?: Payment[];
  /** Total after rounding; remaining = amountDue - payments. */
  amountDue?: number;
  rounding?: number;
  remaining?: number;
  receiptNumber?: number | null;
  defaultBroughtById?: string | null;
  vatLines?: { label: string; amount: number }[];
}

export interface Payment {
  id: string;
  type: 'Cash' | 'Mpesa' | 'Card';
  amount: number;
  tendered: number | null;
  change: number | null;
  mpesaCode: string | null;
  cardReference: string | null;
  broughtById: string;
  broughtBy: string;
  takenBy: string;
  status: 'Pending' | 'Final' | 'Removed';
  takenAt: string;
}

export interface LockState {
  editable: boolean;
  heldBy: LockInfo | null;
  message: string | null;
}
