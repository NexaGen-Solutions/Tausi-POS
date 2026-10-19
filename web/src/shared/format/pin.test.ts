import { describe, expect, it } from 'vitest';
import { isTooSimplePin, isWellFormedPin } from './pin';

describe('PIN rules (mirrors the server)', () => {
  it.each(['0000', '1234', '9876', '3456789'.slice(0, 6), '777777'])('%s is too simple', (pin) =>
    expect(isTooSimplePin(pin)).toBe(true),
  );
  it.each(['4821', '1122', '9012', '1235'])('%s is fine', (pin) =>
    expect(isTooSimplePin(pin)).toBe(false),
  );
  it('needs 4 to 6 digits', () => {
    expect(isWellFormedPin('123')).toBe(false);
    expect(isWellFormedPin('1234567')).toBe(false);
    expect(isWellFormedPin('12a4')).toBe(false);
    expect(isWellFormedPin('482193')).toBe(true);
  });
});
