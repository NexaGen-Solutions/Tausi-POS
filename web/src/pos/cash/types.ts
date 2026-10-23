/** The cash-up screen's view of a shift (GET /api/pos/shifts/current and friends). */
export interface Shift {
  id: string;
  status: 'Open' | 'Closed';
  cashierId: string;
  cashier: string;
  terminalId: string;
  terminal: string;
  openedAt: string;
  closedAt: string | null;
  openingFloat: number;
  cashSettled: number;
  payOuts: number;
  drops: number;
  /** Null while a blind count hasn't been submitted. */
  expected: number | null;
  mpesaCount: number;
  mpesaTotal: number;
  cardCount: number;
  cardTotal: number;
  blind: boolean;
  countSubmitted: boolean;
  countNotes: Record<string, number>;
  countCoins: number;
  counted: number | null;
  difference: number | null;
  note: string | null;
  signedOffBy: string | null;
  receipts: number;
  firstReceipt: number | null;
  lastReceipt: number | null;
  denominations: number[];
  waiters: { userId: string; name: string; cash: number; openBills: number }[];
  movements: {
    id: string;
    kind: 'PayOut' | 'Drop';
    amount: number;
    category: string | null;
    note: string | null;
    by: string;
    at: string;
  }[];
  categories: { id: string; name: string }[];
}

export interface CurrentShift {
  shift: Shift | null;
  start: { defaultFloat: number; denominations: number[] };
}

/** Cash by note ("1000": 2) plus coins, as the server takes it. */
export interface CashCount {
  notes: Record<string, number>;
  coins: number;
}

export const currentShiftKey = ['shift', 'current'] as const;

export function countTotal(count: CashCount): number {
  return (
    Object.entries(count.notes).reduce((sum, [note, n]) => sum + Number(note) * (n || 0), 0) +
    (count.coins || 0)
  );
}
