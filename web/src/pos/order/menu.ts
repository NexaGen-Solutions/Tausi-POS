/** The terminal's menu (GET /api/pos/menu): only items with a price on its list. */
export interface PosOption {
  id: string;
  name: string;
  priceAddOn: number;
  isDefault: boolean;
}

export interface PosGroup {
  id: string;
  name: string;
  minSelect: number;
  maxSelect: number;
  options: PosOption[];
}

export interface PosItem {
  id: string;
  name: string;
  shortName: string;
  price: number;
  vatRate: number;
  groups: PosGroup[];
}

export interface PosCategory {
  id: string;
  name: string;
  items: PosItem[];
}

export interface PosMenu {
  menuVersion: string;
  priceListName: string;
  categories: PosCategory[];
}

/** An item with a required choice (e.g. Temperature) opens the pop-up; others are added straight away. */
export function needsChoice(item: PosItem): boolean {
  return item.groups.some((g) => g.minSelect > 0);
}

/** The options a pop-up starts with: each group's defaults, up to its maximum. */
export function defaultChoice(item: PosItem): string[] {
  return item.groups.flatMap((g) =>
    g.options
      .filter((o) => o.isDefault)
      .slice(0, g.maxSelect)
      .map((o) => o.id),
  );
}

/** Every group within its minimum and maximum. */
export function choiceComplete(item: PosItem, chosen: string[]): boolean {
  return item.groups.every((g) => {
    const count = g.options.filter((o) => chosen.includes(o.id)).length;
    return count >= g.minSelect && count <= g.maxSelect;
  });
}

/** Unit price with the chosen add-ons, and the options as the server writes them ("Cold"). */
export function priced(
  item: PosItem,
  chosen: string[],
): { unitPrice: number; options: string | null } {
  const picked = item.groups.flatMap((g) => g.options.filter((o) => chosen.includes(o.id)));
  return {
    unitPrice: item.price + picked.reduce((sum, o) => sum + o.priceAddOn, 0),
    options: picked.length ? picked.map((o) => o.name).join(', ') : null,
  };
}

/** Search across all categories by name or short name. */
export function search(menu: PosMenu, text: string): PosItem[] {
  const q = text.trim().toLowerCase();
  if (!q) return [];
  return menu.categories
    .flatMap((c) => c.items)
    .filter((i) => i.name.toLowerCase().includes(q) || i.shortName.toLowerCase().includes(q));
}
