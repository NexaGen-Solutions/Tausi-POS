export interface StockLocation {
  id: string;
  name: string;
  active: boolean;
}

export interface StockPack {
  name: string;
  factor: number;
}

export interface StockLevel {
  locationId: string;
  onHand: number;
  onHandText: string;
  reorderLevel: number | null;
}

export interface StockItemRow {
  id: string;
  name: string;
  category: StockCategory;
  baseUnit: string;
  packs: StockPack[];
  active: boolean;
  onHand: number;
  onHandText: string;
  reorderLevel: number | null;
  averageCost: number;
  value: number;
  belowReorder: boolean;
  negative: boolean;
  hasMovements: boolean;
  levels: StockLevel[];
  rowVersion: number;
}

export interface StockOverview {
  locations: StockLocation[];
  items: StockItemRow[];
  noRecipe: { menuItemId: string; name: string }[];
  totalValue: number;
  canEdit: boolean;
}

export interface StockMovementRow {
  at: string;
  kind: string;
  quantity: number;
  quantityText: string;
  location: string;
  user: string | null;
  reference: string;
  unitCost: number;
}

export interface RecipeLine {
  stockItemId: string;
  stockItem: string;
  quantity: number;
  unit: string;
  units: string[];
  cost: number;
}

export interface Recipe {
  menuItemId: string;
  menuItem: string;
  lines: RecipeLine[];
  costPerSale: number;
  price: number | null;
  marginPercent: number | null;
  /** The price less its VAT: the margin is worked out on this. */
  priceWithoutVat: number | null;
}

export interface RecipeMenuItem {
  id: string;
  name: string;
  category: string;
  hasRecipe: boolean;
}

export interface StockActivity {
  id: string;
  kind: 'Waste' | 'Transfer' | 'Delivery' | 'Reversal' | 'Count';
  at: string;
  summary: string;
  location: string;
  value: number | null;
  status: 'Pending' | 'Posted' | 'Rejected';
  by: string;
  approvedBy: string | null;
  note: string | null;
  approveKey: string;
}

export interface Supplier {
  id: string;
  name: string;
  phone: string;
  kraPin: string;
  notes: string;
  active: boolean;
  owed: number;
  rowVersion: number;
}

export type DeliveryPayment = 'OnCredit' | 'Cash' | 'Mpesa' | 'Bank';

export interface DeliveryLine {
  stockItemId: string;
  stockItem: string;
  unit: string;
  factor: number;
  quantity: number;
  unitCost: number;
  lineTotal: number;
}

export interface Delivery {
  id: string;
  date: string;
  supplierId: string;
  supplier: string;
  invoiceNumber: string;
  locationId: string;
  location: string;
  total: number;
  payment: DeliveryPayment;
  paymentReference: string | null;
  status: 'Pending' | 'Posted' | 'Rejected';
  reversalStatus: 'Pending' | 'Posted' | 'Rejected' | null;
  reversalReason: string | null;
  recordedBy: string;
  approvedBy: string | null;
  createdAt: string;
  rejectNote: string | null;
  lines: DeliveryLine[];
}

export interface CountUnit {
  name: string;
  factor: number;
}

export interface CountLine {
  stockItemId: string;
  stockItem: string;
  baseUnit: string;
  units: CountUnit[];
  entered: Record<string, number>;
  counted: number | null;
  countedText: string | null;
  expected: number | null;
  expectedText: string | null;
  difference: number | null;
  value: number | null;
  unitCost: number | null;
}

export interface StockCount {
  id: string;
  locationId: string;
  location: string;
  kind: 'Full' | 'Partial';
  isOpening: boolean;
  status: 'Draft' | 'Submitted' | 'Approved' | 'Rejected';
  asAt: string;
  countedBy: string;
  submittedAt: string | null;
  approvedBy: string | null;
  approvedAt: string | null;
  rejectNote: string | null;
  dayOpen: boolean;
  blind: boolean;
  uncounted: number;
  itemsShort: number;
  varianceValue: number | null;
  canApprove: boolean;
  lines: CountLine[];
  rowVersion: number;
}

export interface CountListItem {
  id: string;
  asAt: string;
  location: string;
  kind: string;
  isOpening: boolean;
  countedBy: string;
  approvedBy: string | null;
  status: string;
  varianceValue: number | null;
}

export type StockCategory = 'Beer' | 'Spirits' | 'SoftDrinks' | 'Food' | 'Other';
export type StockUnit = 'Bottle' | 'Can' | 'Piece' | 'Ml' | 'G';

export const stockCategories: StockCategory[] = ['Beer', 'Spirits', 'SoftDrinks', 'Food', 'Other'];
export const stockUnits: StockUnit[] = ['Bottle', 'Can', 'Piece', 'Ml', 'G'];
export const wasteReasons = ['Broken', 'Expired', 'Spilled', 'Staff use', 'Other'];

export const stockKey = (locationId: string) => ['stock', 'overview', locationId] as const;
