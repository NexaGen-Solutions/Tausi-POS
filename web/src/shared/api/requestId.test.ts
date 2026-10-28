import { afterEach, describe, expect, it, vi } from 'vitest';
import { newRequestId } from './requestId';

const uuidV4 = /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/;

describe('newRequestId', () => {
  afterEach(() => vi.restoreAllMocks());

  it('is a v4 UUID on a secure page', () => {
    expect(newRequestId()).toMatch(uuidV4);
  });

  it('still works on plain http://<server> on the LAN, where randomUUID is missing', () => {
    const original = crypto.randomUUID;
    Object.defineProperty(crypto, 'randomUUID', { value: undefined, configurable: true });
    try {
      const ids = new Set(Array.from({ length: 50 }, () => newRequestId()));
      expect(ids.size).toBe(50);
      for (const id of ids) expect(id).toMatch(uuidV4);
    } finally {
      Object.defineProperty(crypto, 'randomUUID', { value: original, configurable: true });
    }
  });
});
