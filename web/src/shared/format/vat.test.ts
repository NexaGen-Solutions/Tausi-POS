import { describe, expect, it } from 'vitest';
import { formatMoneyCents } from './money';
import { vatIncluded } from './vat';

describe('VAT preview (same rules as the server)', () => {
  it('250 at 16% is net 215.52 and VAT 34.48', () => {
    expect(vatIncluded(250, 16)).toEqual({ gross: 250, net: 215.52, vat: 34.48 });
  });

  it('rounds half away from zero', () => {
    expect(vatIncluded(0.12, 60).vat).toBe(0.05);
    expect(vatIncluded(1370, 16).vat).toBe(188.97);
  });

  it('exempt items have no VAT', () => {
    expect(vatIncluded(100, 0)).toEqual({ gross: 100, net: 100, vat: 0 });
  });

  it('formats with cents', () => {
    expect(formatMoneyCents(215.52)).toBe('KSh 215.52');
    expect(formatMoneyCents(34)).toBe('KSh 34.00');
  });
});
