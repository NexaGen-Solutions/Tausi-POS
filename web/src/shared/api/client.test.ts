import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { setDeviceForTests } from '../device/device';
import { setPinSession } from '../session/session';
import {
  ApiError,
  OfflineError,
  RequestSentError,
  api,
  forgetUnknownOutcomes,
  setAlreadyDoneHandler,
  setApprovalHandler,
} from './client';

function json(status: number, body: unknown): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'content-type': 'application/json' },
  });
}

const approvalNeeded = {
  status: 403,
  title: 'Approval required',
  approvalRequired: true,
  permission: 'order.void',
};

describe('api client', () => {
  const fetchMock = vi.fn<typeof fetch>();

  beforeEach(() => {
    vi.stubGlobal('fetch', fetchMock);
    setDeviceForTests({ deviceToken: 'device-123' });
    setPinSession({
      token: 'session-abc',
      expiresAt: new Date(Date.now() + 60_000).toISOString(),
      idleLogoutMinutes: 10,
      mustChangePin: false,
      user: { id: 'u1', name: 'edna', role: 'Waiter', terminalId: 't1', terminalName: 'Counter A' },
    });
  });

  afterEach(() => {
    fetchMock.mockReset();
    setApprovalHandler(null);
    setAlreadyDoneHandler(null);
    forgetUnknownOutcomes();
    setPinSession(null);
    vi.unstubAllGlobals();
  });

  const headersOf = (call: number) => fetchMock.mock.calls[call][1]!.headers as Headers;

  const addLine = () => api('/api/pos/tickets/1/lines', { method: 'POST', body: { item: 'a' } });

  it('uses a new key for each press after a clean answer', async () => {
    fetchMock.mockImplementation(async () => json(200, {}));

    await addLine();
    await addLine();

    expect(headersOf(1).get('Idempotency-Key')).not.toBe(headersOf(0).get('Idempotency-Key'));
  });

  it('sends the same key when the same action is pressed again after a server fault', async () => {
    fetchMock.mockImplementationOnce(async () => json(503, { code: 'server_busy' }));
    fetchMock.mockImplementationOnce(async () => json(200, {}));
    fetchMock.mockImplementation(async () => json(200, {}));

    await expect(addLine()).rejects.toBeInstanceOf(ApiError);
    await addLine();
    await addLine();

    expect(headersOf(1).get('Idempotency-Key')).toBe(headersOf(0).get('Idempotency-Key'));
    expect(headersOf(2).get('Idempotency-Key')).not.toBe(headersOf(1).get('Idempotency-Key'));
  });

  it('sends the same key after a lost connection', async () => {
    fetchMock.mockRejectedValueOnce(new TypeError('failed'));
    fetchMock.mockImplementation(async () => json(200, {}));

    await expect(addLine()).rejects.toBeInstanceOf(OfflineError);
    await addLine();

    expect(headersOf(1).get('Idempotency-Key')).toBe(headersOf(0).get('Idempotency-Key'));
  });

  it('starts afresh for a refusal, for another action, and for sign-in and ticket locks', async () => {
    fetchMock.mockImplementation(async () => json(500, {}));
    const twice = async (send: () => Promise<unknown>) => {
      const from = fetchMock.mock.calls.length;
      await send().catch(() => {});
      await send().catch(() => {});
      return [headersOf(from).get('Idempotency-Key'), headersOf(from + 1).get('Idempotency-Key')];
    };

    const [pinA, pinB] = await twice(() =>
      api('/api/auth/pin', { method: 'POST', body: { pin: '1234' } }),
    );
    const [lockA, lockB] = await twice(() => api('/api/pos/tickets/1/lock', { method: 'POST' }));
    await addLine().catch(() => {});
    const faulted = headersOf(fetchMock.mock.calls.length - 1).get('Idempotency-Key');
    await api('/api/pos/tickets/1/lines', { method: 'POST', body: { item: 'b' } }).catch(() => {});

    expect(pinB).not.toBe(pinA);
    expect(lockB).not.toBe(lockA);
    expect(headersOf(fetchMock.mock.calls.length - 1).get('Idempotency-Key')).not.toBe(faulted);

    fetchMock.mockImplementation(async () => json(400, {}));
    await addLine().catch(() => {});
    await addLine().catch(() => {});
    const calls = fetchMock.mock.calls.length;
    expect(headersOf(calls - 1).get('Idempotency-Key')).not.toBe(
      headersOf(calls - 2).get('Idempotency-Key'),
    );
  });

  it('tells the screens to refresh when the server says it already went through', async () => {
    const refresh = vi.fn();
    setAlreadyDoneHandler(refresh);
    fetchMock.mockImplementation(async () => json(409, { code: 'already_done', detail: 'Done' }));

    await expect(addLine()).rejects.toBeInstanceOf(ApiError);

    expect(refresh).toHaveBeenCalledOnce();
  });

  it('sends the device token and the session on every request', async () => {
    fetchMock.mockResolvedValue(json(200, { ok: true }));

    await api('/api/auth/me');

    expect(headersOf(0).get('X-Device-Token')).toBe('device-123');
    expect(headersOf(0).get('Authorization')).toBe('Bearer session-abc');
    expect(headersOf(0).has('Idempotency-Key')).toBe(false);
  });

  it('adds a UUID Idempotency-Key to writes', async () => {
    fetchMock.mockResolvedValue(json(200, {}));

    await api('/api/things', { method: 'POST', body: { a: 1 } });

    expect(headersOf(0).get('Idempotency-Key')).toMatch(
      /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/,
    );
    expect(headersOf(0).get('Content-Type')).toBe('application/json');
  });

  it('asks for approval and retries once with the token and the same key', async () => {
    fetchMock
      .mockResolvedValueOnce(json(403, approvalNeeded))
      .mockResolvedValueOnce(json(200, { done: true }));
    const handler = vi.fn().mockResolvedValue('approval-xyz');
    setApprovalHandler(handler);

    const result = await api<{ done: boolean }>('/api/pos/tickets/k1/lines/l1/void', {
      method: 'POST',
    });

    expect(result.done).toBe(true);
    expect(handler).toHaveBeenCalledWith('order.void', undefined);
    expect(headersOf(1).get('X-Approval-Token')).toBe('approval-xyz');
    expect(headersOf(1).get('Idempotency-Key')).toBe(headersOf(0).get('Idempotency-Key'));
  });

  it('passes what to ask for to the pad, and a request sent instead is not an error to show', async () => {
    fetchMock.mockResolvedValueOnce(json(403, approvalNeeded));
    const sent = { id: 'r1', kind: 'Reprint', summary: 'Reprint bill', status: 'Pending' };
    const handler = vi.fn().mockResolvedValue({ requested: sent });
    setApprovalHandler(handler);
    const approval = { kind: 'Reprint' as const, ticketId: 'k1', payload: { reason: null } };

    const error = await api('/api/pos/tickets/k1/reprint-bill', { method: 'POST', approval }).catch(
      (e: unknown) => e,
    );

    expect(handler).toHaveBeenCalledWith('order.void', approval);
    expect(error).toBeInstanceOf(RequestSentError);
    expect((error as RequestSentError).request).toEqual(sent);
    expect(fetchMock).toHaveBeenCalledTimes(1);
  });

  it('sends a token already in hand and does not ask again when it is refused', async () => {
    fetchMock.mockResolvedValueOnce(json(403, approvalNeeded));
    const handler = vi.fn();
    setApprovalHandler(handler);

    await expect(
      api('/api/x', { method: 'POST', approvalToken: 'pin-token' }),
    ).rejects.toBeInstanceOf(ApiError);

    expect(headersOf(0).get('X-Approval-Token')).toBe('pin-token');
    expect(handler).not.toHaveBeenCalled();
  });

  it('gives up when no approval is given', async () => {
    fetchMock.mockResolvedValueOnce(json(403, approvalNeeded));
    setApprovalHandler(vi.fn().mockResolvedValue(null));

    await expect(
      api('/api/pos/tickets/k1/lines/l1/void', { method: 'POST' }),
    ).rejects.toBeInstanceOf(ApiError);
    expect(fetchMock).toHaveBeenCalledTimes(1);
  });

  it('does not ask for approval on a plain "not allowed"', async () => {
    fetchMock.mockResolvedValueOnce(
      json(403, { status: 403, approvalRequired: false, permission: 'ticket.settle' }),
    );
    const handler = vi.fn();
    setApprovalHandler(handler);

    const error = await api('/api/x', { method: 'POST' }).catch((e: unknown) => e);

    expect(error).toBeInstanceOf(ApiError);
    expect((error as ApiError).problem.permission).toBe('ticket.settle');
    expect(handler).not.toHaveBeenCalled();
  });
});
