/** Shapes of the cash book and accounts API (server: Tausi.Application.Money). */

export type MoneyAccount = 'Safe' | 'Bank' | 'Mpesa';

export const moneyAccounts: MoneyAccount[] = ['Safe', 'Bank', 'Mpesa'];

export interface NamedAmount {
  name: string;
  amount: number;
}

export interface DailySummary {
  date: string;
  salesInclVat: number;
  vat: number;
  netSales: number;
  costOfSales: number;
  grossProfit: number;
  grossMarginPercent: number | null;
  expenses: number;
  expensesByCategory: NamedAmount[];
  stockLossAndCash: number;
  stockLossParts: NamedAmount[];
  bankCharges: number;
  otherIncome: number;
  net: number;
  noRecipeItems: string[];
}

export interface CashBookTiles {
  cashOnHand: number;
  inOpenDrawers: number;
  mpesaReceived: number;
  mpesaBalance: number;
  bank: number;
  owedToSuppliers: number;
}

export interface CashBookRow {
  entryId: string;
  at: string;
  text: string;
  account: string;
  in: number | null;
  out: number | null;
  late: boolean;
  reversed: boolean;
  reversal: boolean;
  moneyDocumentId: string | null;
  /** How many journal entries the line stands for (sales are grouped per shift and type). */
  entries: number;
}

export interface CashBook {
  date: string;
  tiles: CashBookTiles;
  rows: CashBookRow[];
  summary: DailySummary;
  canRecord: boolean;
}

export interface OpeningLine {
  account: MoneyAccount | null;
  supplierId: string | null;
  amount: number;
}

export interface MoneyDocument {
  id: string;
  kind: string;
  title: string;
  status: 'Pending' | 'Posted' | 'Rejected';
  businessDate: string;
  amount: number;
  fee: number;
  from: string | null;
  to: string | null;
  category: string | null;
  supplierId: string | null;
  supplier: string | null;
  payee: string | null;
  reference: string | null;
  note: string | null;
  hasAttachment: boolean;
  createdBy: string;
  createdAt: string;
  approvedBy: string | null;
  rejectNote: string | null;
  reversalStatus: 'Pending' | 'Posted' | 'Rejected' | null;
  reversalReason: string | null;
  journalEntryId: string | null;
  reversalEntryId: string | null;
  /** E.g. "The Safe goes below 0 (−2,000)." Shown, never blocking. */
  warning: string | null;
  openingLines: OpeningLine[];
}

export interface EntryLine {
  account: string;
  code: string;
  debit: number;
  credit: number;
  memo: string;
  category: string | null;
}

export interface EntryDetail {
  id: string;
  businessDate: string | null;
  at: string;
  memo: string;
  by: string | null;
  late: boolean;
  reversesEntryId: string | null;
  reversedByEntryId: string | null;
  lines: EntryLine[];
  document: MoneyDocument | null;
}

export interface ExpenseCategory {
  id: string;
  name: string;
  active: boolean;
  inUse: boolean;
  rowVersion: number;
}

export interface AccountBalance {
  code: string;
  name: string;
  group: string;
  balance: number;
}

export interface Accounts {
  asAt: string;
  trial: { debits: number; credits: number; difference: number };
  groups: { name: string; accounts: AccountBalance[]; total: number }[];
}

export interface LedgerLine {
  entryId: string;
  date: string | null;
  at: string;
  entry: string;
  memo: string;
  debit: number;
  credit: number;
  balance: number;
  late: boolean;
}

export interface AccountLedger {
  code: string;
  name: string;
  from: string;
  to: string;
  opening: number;
  lines: LedgerLine[];
  closing: number;
}
