/** The exact amount, then the next round amounts above it: 370 → 370, 400, 500, 1,000. */
export function quickCash(remaining: number): number[] {
  if (remaining <= 0) return [];
  const exact = Math.ceil(remaining);
  const next = [100, 500, 1000].map((step) => (Math.floor(exact / step) + 1) * step);
  return [exact, ...next].filter((v, i, all) => all.indexOf(v) === i).slice(0, 4);
}
