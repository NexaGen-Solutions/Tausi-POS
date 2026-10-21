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
  name: string;
  options: string | null;
  quantity: number;
  unitPrice: number;
  gross: number;
  addedBy: string;
  addedOn: string;
  addedAt: string;
  printed: boolean;
  voided: boolean;
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
}

export interface LockState {
  editable: boolean;
  heldBy: LockInfo | null;
  message: string | null;
}
