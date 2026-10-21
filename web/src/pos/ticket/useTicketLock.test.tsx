import { render, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { setDeviceForTests } from '../../shared/device/device';
import { setPinSession } from '../../shared/session/session';
import { json, sessionFor } from '../../test/render';
import { useTicketLock } from './useTicketLock';

vi.mock('../../shared/connection/hub', () => ({
  startHub: vi.fn(),
  stopHub: vi.fn(async () => {}),
  onPosEvent: vi.fn(() => () => {}),
}));

function Holder({ id }: { id: string }) {
  const lock = useTicketLock(id);
  return <p>{lock?.editable ? 'editable' : 'waiting'}</p>;
}

describe('ticket edit lock', () => {
  const fetchMock = vi.fn<typeof fetch>();

  beforeEach(() => {
    vi.stubGlobal('fetch', fetchMock);
    setDeviceForTests({ deviceToken: 'device-1' });
    setPinSession(sessionFor('edna', 'Waiter'));
    fetchMock.mockImplementation(async (_input, init) =>
      init?.method === 'POST'
        ? json(200, { editable: true, heldBy: null, message: null })
        : new Response(null, { status: 204 }),
    );
  });

  afterEach(() => {
    fetchMock.mockReset();
    vi.unstubAllGlobals();
    setPinSession(null);
  });

  it('gives the lock back with a keepalive request when the tab closes or reloads', async () => {
    const view = render(<Holder id="k1" />);
    await waitFor(() => expect(view.container).toHaveTextContent('editable'));

    window.dispatchEvent(new Event('pagehide'));

    const release = fetchMock.mock.calls.find(([, init]) => init?.method === 'DELETE');
    expect(String(release![0])).toContain('/api/pos/tickets/k1/lock');
    expect(release![1]!.keepalive).toBe(true);
    expect(new Headers(release![1]!.headers).get('Authorization')).toBe('Bearer token-edna');

    // Leaving afterwards does not release it twice.
    view.unmount();
    expect(fetchMock.mock.calls.filter(([, init]) => init?.method === 'DELETE')).toHaveLength(1);
  });
});
