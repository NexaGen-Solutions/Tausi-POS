/** Shapes of the setup API (server: Tausi.Application.Setup). */

export type StepKey = 'business' | 'admin' | 'devices' | 'menu' | 'stock' | 'licence';

export const stepKeys: StepKey[] = ['business', 'admin', 'devices', 'menu', 'stock', 'licence'];

export const skippable: StepKey[] = ['devices', 'menu', 'stock'];

export interface SetupStep {
  key: StepKey;
  status: 'Todo' | 'Done' | 'Skipped';
  summary: string | null;
}

export interface SetupStatus {
  complete: boolean;
  unlocked: boolean;
  lockedUntil: string | null;
  steps: SetupStep[];
  current: StepKey | null;
  businessType: string | null;
  businessName: string | null;
}

export interface NewStaffPin {
  id: string;
  name: string;
  role: string;
  pin: string;
}

export interface ServerAddress {
  address: string;
  interface: string;
}

export interface LicenceStatus {
  status: 'None' | 'Trial' | 'Active' | 'ExpiresSoon' | 'Grace' | 'Expired';
  isTrial: boolean;
  edition: string | null;
  business: string | null;
  licenceId: string | null;
  terminalLimit: number;
  terminalsUsed: number;
  expiresAt: string | null;
  daysLeft: number | null;
  supportUntil: string | null;
  overLimit: boolean;
  banner: string | null;
}

export interface SummaryLine {
  step: StepKey;
  title: string;
  text: string;
  skipped: boolean;
  laterLink: string | null;
}

export interface SambaConnection {
  server: string;
  database: string;
  windowsLogin: boolean;
  user: string;
  password: string;
}

export interface SambaOptions {
  menu: boolean;
  prices: boolean;
  modifiers: boolean;
  tables: boolean;
  users: boolean;
  categoriesFromScreenMenu: boolean;
  /** One item per SambaPOS item: its plain name and its first portion's main price. */
  firstPriceOnly: boolean;
  userRoles: Record<string, string>;
}

export interface SambaPreview {
  items: { name: string; category: string; status: string; prices: string[] }[];
  categories: string[];
  priceLists: string[];
  modifierGroups: string[];
  tables: string[];
  users: {
    id: string;
    name: string;
    sambaRole: string | null;
    role: string | null;
    status: string;
  }[];
  newItems: number;
  existingItems: number;
  newTables: number;
  newUsers: number;
  problems: string[];
}

export interface SambaResult {
  summary: string;
  people: NewStaffPin[];
  pinSheetPdf: string | null;
}

export interface StockSuggestion {
  menuItemId: string;
  menuItem: string;
  category: string;
  stockItemName: string;
  stockCategory: string;
  baseUnit: string;
  packName: string | null;
  packFactor: number | null;
  existingStockItemId: string | null;
  suggested: boolean;
  hint: string | null;
}

export interface LocationRow {
  id: string;
  name: string;
  active: boolean;
  sortOrder: number;
  inUse: boolean;
  rowVersion: number;
}
