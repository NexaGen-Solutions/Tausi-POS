const symbols: Record<string, string> = { KES: 'KSh' };

/** "1,370" or "1,370.50": whole amounts drop the cents, as on the wireframes. */
export function formatAmount(amount: number): string {
  const whole = Number.isInteger(amount);
  return amount.toLocaleString('en-KE', {
    minimumFractionDigits: whole ? 0 : 2,
    maximumFractionDigits: 2,
  });
}

/** "KSh 1,370". The currency comes from the business settings (KES by default). */
export function formatMoney(amount: number, currency = 'KES'): string {
  return `${symbols[currency] ?? currency} ${formatAmount(amount)}`;
}
