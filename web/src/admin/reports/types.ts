/** Shapes of the reports API (server: Tausi.Application.Reports). */

export type ColumnType =
  'Text' | 'Money' | 'Quantity' | 'Percent' | 'Integer' | 'Date' | 'DateTime' | 'Time' | 'Ticket';

export type Cell = string | number | boolean | null;

export interface ReportColumn {
  key: string;
  label: string;
  type: ColumnType;
  rightAligned: boolean;
}

export interface ReportRow {
  cells: Cell[];
  ticketId: string | null;
  muted: boolean;
}

export interface ReportGroup {
  title: string | null;
  rows: ReportRow[];
  subtotal: ReportRow | null;
  footer: string[] | null;
  subtitle: string | null;
  total: number | null;
}

export interface ReportTable {
  title: string | null;
  columns: ReportColumn[];
  groups: ReportGroup[];
  totals: ReportRow | null;
  footer: string[] | null;
}

export interface ReportResult {
  key: string;
  title: string;
  period: string;
  filterText: string[];
  runAt: string;
  tables: ReportTable[];
  notes: string[];
  isEmpty: boolean;
}

export interface ReportDefinition {
  key: string;
  title: string;
  permission: string;
  filters: string[];
  usesPeriod: boolean;
  receipt: boolean;
  kinds: string[] | null;
}

export interface WorkPeriodOption {
  id: string;
  date: string;
  openedAt: string;
  closedAt: string | null;
  open: boolean;
}

export interface ReportOption {
  id: string;
  name: string;
  detail: string | null;
}

export interface ReportOptions {
  periods: WorkPeriodOption[];
  users: ReportOption[];
  terminals: ReportOption[];
  locations: ReportOption[];
  categories: ReportOption[];
  suppliers: ReportOption[];
  tables: ReportOption[];
  roles: ReportOption[];
  counts: ReportOption[];
  stockItems: ReportOption[];
  printers: ReportOption[];
}

export type QuickRange =
  'Today' | 'Yesterday' | 'ThisWeek' | 'LastWeek' | 'ThisMonth' | 'LastMonth';

/** The filters sent to the server; unset ones are left out. */
export interface ReportFilters {
  workPeriodId?: string;
  from?: string;
  to?: string;
  quick?: QuickRange;
  userId?: string;
  terminalId?: string;
  locationId?: string;
  categoryId?: string;
  paymentType?: 'Cash' | 'Mpesa' | 'Card';
  supplierId?: string;
  creditTo?: 'AddedBy' | 'OpenedBy';
  includeOpen?: boolean;
  cashierId?: string;
  waiterId?: string;
  kind?: string;
  requesterId?: string;
  approverId?: string;
  asAt?: string;
  onlyBelowReorder?: boolean;
  stockCategory?: string;
  countId?: string;
  stockItemId?: string;
  status?: string;
  tableId?: string;
  minAmount?: number;
  maxAmount?: number;
  hasVoids?: boolean;
  hasReprints?: boolean;
  hasDiscount?: boolean;
  roleId?: string;
  action?: string;
  entity?: string;
  search?: string;
}

export interface TicketHeader {
  id: string;
  number: number;
  receiptNumber: number | null;
  table: string | null;
  status: string;
  openedBy: string;
  openedAt: string;
  openedOn: string;
  settledBy: string | null;
  settledAt: string | null;
  settledOn: string | null;
  businessDate: string | null;
  periodOpenedAt: string;
  total: number;
  discount: number;
  discountLabel: string | null;
  rounding: number;
  mergedIntoNumber: number | null;
  mergedIntoId: string | null;
  mergedInNumbers: number[];
  note: string;
}

export interface TicketLine {
  id: string;
  name: string;
  options: string | null;
  note: string | null;
  quantity: number;
  unitPrice: number;
  originalUnitPrice: number | null;
  gross: number;
  discount: number;
  amount: number;
  status: string;
  gift: boolean;
  addedBy: string;
  terminal: string;
  addedAt: string;
  printRound: number | null;
  printedAt: string | null;
  voidedBy: string | null;
  voidedAt: string | null;
  voidReason: string | null;
  cancelledBy: string | null;
  cancelledAt: string | null;
  priceChanges: {
    oldPrice: number | null;
    newPrice: number | null;
    by: string;
    approvedBy: string | null;
    at: string;
    reason: string | null;
  }[];
}

export interface TicketPrint {
  id: string;
  kind: string;
  label: string;
  printer: string;
  at: string;
  sentAt: string | null;
  by: string | null;
  status: string;
  reprint: boolean;
  text: string | null;
  error: string | null;
}

export interface TicketPayment {
  id: string;
  type: string;
  amount: number;
  tendered: number | null;
  change: number | null;
  reference: string | null;
  takenBy: string;
  broughtBy: string;
  shift: string | null;
  at: string;
  status: string;
  removedBy: string | null;
  removedAt: string | null;
}

export interface TicketApproval {
  id: string;
  kind: string;
  summary: string;
  amount: number | null;
  reason: string | null;
  requestedBy: string;
  requestedAt: string;
  status: string;
  decidedBy: string | null;
  decidedAt: string | null;
  note: string | null;
}

export interface TicketMovement {
  item: string;
  location: string;
  kind: string;
  quantity: number;
  quantityText: string;
  unitCost: number;
  value: number;
  at: string;
}

export interface TicketJournalEntry {
  id: string;
  businessDate: string | null;
  at: string;
  memo: string;
  lines: { code: string; account: string; debit: number; credit: number }[];
}

export interface TimelineEvent {
  at: string;
  kind: string;
  text: string;
  by: string | null;
  ref: string | null;
}

export interface TicketReport {
  ticket: TicketHeader;
  lines: TicketLine[];
  prints: TicketPrint[];
  payments: TicketPayment[];
  approvals: TicketApproval[];
  movements: TicketMovement[];
  journal: TicketJournalEntry[];
  timeline: TimelineEvent[];
}
