const symbols: Record<string, string> = { KES: 'KSh' };

/** "1,370" or "1,370.50": whole amounts drop the cents, as on the wireframes. */
export function formatAmount(amount: number): string {
  const whole = Number.isInteger(amount);
  return amount.toLocaleString('en-KE', {
    minimumFractionDigits: whole ? 0 : 2,
    maximumFractionDigits: 2,
  });
}

/** "1,370" or "− 1,370" (a true minus sign): figures that can go below 0, like a day's net. */
export function formatSigned(amount: number): string {
  return amount < 0 ? `− ${formatAmount(-amount)}` : formatAmount(amount);
}

/** "KSh 1,370". The currency comes from the business settings (KES by default). */
export function formatMoney(amount: number, currency = 'KES'): string {
  return `${symbols[currency] ?? currency} ${formatAmount(amount)}`;
}

/** "KSh 215.52": always with cents (net and VAT figures). */
export function formatMoneyCents(amount: number, currency = 'KES'): string {
  return `${symbols[currency] ?? currency} ${amount.toLocaleString('en-KE', {
    minimumFractionDigits: 2,
    maximumFractionDigits: 2,
  })}`;
}
