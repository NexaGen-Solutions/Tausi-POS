/**
 * The server's VatCalculator, for live previews: prices include VAT; VAT = gross x rate / (100 + rate),
 * rounded half away from zero to cents. Works in whole cents to avoid floating-point surprises.
 */
export interface LineAmounts {
  gross: number;
  net: number;
  vat: number;
}

function roundHalfAwayFromZero(value: number): number {
  const sign = value < 0 ? -1 : 1;
  // A tiny nudge so values like 4.4999999 (meant as 4.5) round as written.
  return sign * Math.round(Math.abs(value) + 1e-9);
}

export function vatIncluded(gross: number, ratePercent: number): LineAmounts {
  const grossCents = roundHalfAwayFromZero(gross * 100);
  const vatCents =
    ratePercent === 0 ? 0 : roundHalfAwayFromZero((grossCents * ratePercent) / (100 + ratePercent));
  return { gross: grossCents / 100, net: (grossCents - vatCents) / 100, vat: vatCents / 100 };
}
