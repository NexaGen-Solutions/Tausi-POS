import { afterEach, describe, expect, it } from 'vitest';
import { loadDevice, oldStorageKey } from './device';

describe('device storage', () => {
  afterEach(() => localStorage.clear());

  it('keeps a browser paired before the rename to Tausi POS', async () => {
    localStorage.setItem(oldStorageKey, JSON.stringify({ serverUrl: null, deviceToken: 'tok-1' }));

    expect((await loadDevice()).deviceToken).toBe('tok-1');
    expect(localStorage.getItem('tausi.device')).toContain('tok-1');
    expect(localStorage.getItem(oldStorageKey)).toBeNull();
  });

  it('prefers the new key when both exist', async () => {
    localStorage.setItem(oldStorageKey, JSON.stringify({ deviceToken: 'old' }));
    localStorage.setItem('tausi.device', JSON.stringify({ deviceToken: 'new' }));

    expect((await loadDevice()).deviceToken).toBe('new');
    expect(localStorage.getItem(oldStorageKey)).toBeNull();
  });
});
