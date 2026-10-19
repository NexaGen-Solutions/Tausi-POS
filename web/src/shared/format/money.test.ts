import { describe, expect, it } from 'vitest';
import { formatAmount, formatMoney } from './money';

describe('money formatting', () => {
  it('groups thousands and drops cents on whole amounts', () => {
    expect(formatAmount(1370)).toBe('1,370');
    expect(formatMoney(1370)).toBe('KSh 1,370');
  });

  it('keeps two decimals when there are cents', () => {
    expect(formatAmount(34.5)).toBe('34.50');
    expect(formatAmount(1181.03)).toBe('1,181.03');
  });

  it('uses the currency code when there is no symbol', () => {
    expect(formatMoney(10, 'USD')).toBe('USD 10');
  });
});
