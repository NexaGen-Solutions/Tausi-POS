export interface MenuCategory {
  id: string;
  name: string;
  sortOrder: number;
  active: boolean;
  activeItems: number;
  rowVersion: number;
}

export interface MenuItem {
  id: string;
  name: string;
  shortName: string;
  categoryId: string;
  sortOrder: number;
  vatRate: number | null;
  /** Exempt from VAT (bills say "VAT exempt"); a rate of 0 without it is zero-rated. */
  vatExempt: boolean;
  active: boolean;
  erpItemCode: string | null;
  prices: Record<string, number>;
  ownGroupIds: string[];
  inheritedGroupIds: string[];
  rowVersion: number;
}

export interface PriceList {
  id: string;
  name: string;
  active: boolean;
  isDefault: boolean;
  rowVersion: number;
}

export interface MenuOption {
  id: string;
  name: string;
  priceAddOn: number;
  sortOrder: number;
  active: boolean;
}

export interface MenuGroup {
  id: string;
  name: string;
  minSelect: number;
  maxSelect: number;
  sortOrder: number;
  active: boolean;
  options: MenuOption[];
  categoryIds: string[];
  itemIds: string[];
  rowVersion: number;
}

export interface AdminMenu {
  categories: MenuCategory[];
  items: MenuItem[];
  priceLists: PriceList[];
  groups: MenuGroup[];
  canEdit: boolean;
  canEditPrices: boolean;
  businessVatRate: number;
  currency: string;
}

export interface SavedRef {
  id: string;
  rowVersion: number;
}

export interface PriceLogEntry {
  at: string;
  itemName: string | null;
  optionName: string | null;
  priceListName: string | null;
  oldPrice: number | null;
  newPrice: number | null;
  changedBy: string;
  approvedBy: string | null;
  where: string;
  source: string;
}

export interface ImportChange {
  field: string;
  old: string | null;
  new: string | null;
  isPrice: boolean;
}

export interface ImportRow {
  line: number;
  status: 'New' | 'Updated' | 'Unchanged' | 'Existing' | 'Error';
  name: string;
  category: string;
  changes: ImportChange[];
  error: string | null;
}

export interface ImportPreview {
  headers: string[];
  mapping: string[];
  fields: { field: string; label: string }[];
  needsMapping: boolean;
  mappingProblem: string | null;
  rows: ImportRow[];
  newCategories: string[];
  toDeactivate: string[];
  new: number;
  updated: number;
  unchanged: number;
  errors: number;
  priceChanges: number;
  /** Already on the menu with "Update existing items" off: skipped. */
  existing: number;
}

export interface ImportResult {
  new: number;
  updated: number;
  deactivated: number;
  priceChanges: number;
  skipped: number;
  existing: number;
}

export function choiceText(min: number, max: number): string {
  if (min === max) return `${min}`;
  if (min === 0) return `up to ${max}`;
  return `${min} to ${max}`;
}

/** Parses a typed price ("1,200", "250.50"); null for empty, NaN for nonsense. */
export function parsePrice(text: string): number | null {
  const cleaned = text.replace(/[,\s]/g, '');
  if (cleaned === '') return null;
  return /^-?\d+(\.\d{1,2})?$/.test(cleaned) ? Number(cleaned) : Number.NaN;
}
