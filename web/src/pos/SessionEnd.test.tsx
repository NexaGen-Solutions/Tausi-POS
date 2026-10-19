import { act, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { routes } from '../app/routes';
import { api } from '../shared/api/client';
import { setDeviceForTests } from '../shared/device/device';
import { pinSession, setPinSession, touchSession } from '../shared/session/session';
import { deviceInfo, fakeServer, json, renderRoutes, sessionFor } from '../test/render';

vi.mock('../shared/connection/hub', () => ({
  startHub: vi.fn(),
  stopHub: vi.fn(async () => {}),
  onPosEvent: vi.fn(() => () => {}),
}));

describe('end of a session', () => {
  const fetchMock = vi.fn<typeof fetch>();
  let logouts = 0;

  beforeEach(() => {
    vi.stubGlobal('fetch', fetchMock);
    setDeviceForTests({ deviceToken: 'device-1' });
    logouts = 0;
    fetchMock.mockImplementation(
      fakeServer({
        'GET /api/device': () => json(200, deviceInfo({ openedAt: '2026-10-20T08:14:00' })),
        'GET /api/auth/pin/max-length': () => json(200, 4),
        'GET /api/brand': () => json(200, { name: "Mau's Village" }),
        'GET /api/auth/session': () =>
          json(401, { status: 401, code: 'session_expired', title: 'Session expired' }),
        'POST /api/auth/logout': () => {
          logouts++;
          return new Response(null, { status: 204 });
        },
      }),
    );
  });

  afterEach(() => {
    fetchMock.mockReset();
    vi.unstubAllGlobals();
    vi.useRealTimers();
    setPinSession(null);
  });

  it('session_expired returns to the PIN pad with nothing of the previous user on screen', async () => {
    setPinSession(sessionFor('CAROL', 'Cashier'));
    renderRoutes(routes, '/pos/tables');
    expect(await screen.findByText('Counter A · CAROL (Cashier)')).toBeInTheDocument();

    await act(async () => {
      await api('/api/auth/session').catch(() => {});
    });

    expect(await screen.findByRole('heading', { name: 'Enter your PIN' })).toBeInTheDocument();
    expect(screen.queryByText(/CAROL/)).not.toBeInTheDocument();
    expect(pinSession()).toBeNull();
    expect(sessionStorage.getItem('nexagen.session')).toBeNull();
  });

  it('logs out by itself after the idle time and says why for 5 seconds', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    setPinSession(sessionFor('edna', 'Waiter', { idleLogoutMinutes: 10 }));
    renderRoutes(routes, '/pos/tables');
    await screen.findByText('Counter A · edna (Waiter)');

    act(() => touchSession(Date.now() - 10 * 60_000 + 2000));
    act(() => vi.advanceTimersByTime(1100));
    expect(screen.getByText('Counter A · edna (Waiter)')).toBeInTheDocument();

    act(() => vi.advanceTimersByTime(1500));
    expect(await screen.findByText('Logged out after 10 minutes without use')).toBeInTheDocument();
    expect(screen.queryByText(/edna/)).not.toBeInTheDocument();

    act(() => vi.advanceTimersByTime(5100));
    await waitFor(() =>
      expect(screen.queryByText('Logged out after 10 minutes without use')).not.toBeInTheDocument(),
    );
  });

  it('Log out ends the session on the server and returns to the PIN pad', async () => {
    setPinSession(sessionFor('edna', 'Waiter'));
    renderRoutes(routes, '/pos/tables');

    await userEvent.click(await screen.findByRole('button', { name: 'Log out' }));

    expect(await screen.findByRole('heading', { name: 'Enter your PIN' })).toBeInTheDocument();
    expect(logouts).toBe(1);
    expect(screen.queryByText(/edna/)).not.toBeInTheDocument();
  });
});
