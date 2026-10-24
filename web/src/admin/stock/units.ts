import type { StockItemRow } from './types';

/** The base unit, then the packs from smallest to largest: "bottle", "crate". */
export function unitsOf(item: Pick<StockItemRow, 'baseUnit' | 'packs'>): string[] {
  return [item.baseUnit, ...[...item.packs].sort((a, b) => a.factor - b.factor).map((p) => p.name)];
}

/** How many base units one of this unit holds. */
export function factorOf(item: Pick<StockItemRow, 'baseUnit' | 'packs'>, unit: string): number {
  return unit === item.baseUnit ? 1 : (item.packs.find((p) => p.name === unit)?.factor ?? 1);
}
