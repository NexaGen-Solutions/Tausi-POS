import { describe, expect, it } from 'vitest';
import { permissionLabel, t } from './i18n';

describe('t', () => {
  it('finds nested texts and fills placeholders', () => {
    expect(t('pos.loggedInAs', { name: 'edna', terminal: 'Counter A' })).toBe(
      'Logged in as edna on Counter A',
    );
  });

  it('returns the key when a text is missing', () => {
    expect(t('nope.missing')).toBe('nope.missing');
  });

  it('labels permission keys', () => {
    expect(permissionLabel('order.void')).toBe('Void a printed item');
  });
});
