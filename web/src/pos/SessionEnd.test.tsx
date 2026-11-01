import { act, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { routes } from '../app/routes';
import { api } from '../shared/api/client';
import { setDeviceForTests } from '../shared/device/device';
import { pinSession, setPinSession, touchSession } from '../shared/session/session';
import { deviceInfo, fakeServer, json, renderRoutes, sessionFor } from '../test/render';

const hubHandlers = new Map<string, (payload: unknown) => void>();
vi.mock('../shared/connection/hub', () => ({
  startHub: vi.fn(),
  stopHub: vi.fn(async () => {}),
  onPosEvent: vi.fn((name: string, handler: (payload: unknown) => void) => {
    hubHandlers.set(name, handler);
    return () => hubHandlers.delete(name);
  }),
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
        'GET /api/brand': () => json(200, { name: 'Demo Bar' }),
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
    expect(sessionStorage.getItem('tausi.session')).toBeNull();
  });

  it("someone else signing in never sees the previous person's permissions", async () => {
    // As after a reload with CAROL still signed in, and SAM typing his PIN.
    const permissions: Record<string, Record<string, string>> = {
      CAROL: { 'ticket.settle': 'Allowed', 'day.open_close': 'NotAllowed' },
      SAM: { 'ticket.settle': 'Allowed', 'day.open_close': 'Allowed' },
    };
    fetchMock.mockImplementation(
      fakeServer({
        'GET /api/device': () => json(200, deviceInfo({ openedAt: '2026-10-20T08:14:00' })),
        'GET /api/pos/tables': () => json(200, { areas: [], tables: [], takeaway: [], version: 1 }),
        'GET /api/auth/me': () => {
          const name = pinSession()!.user.name;
          return json(200, { user: { id: name, name, role: 'x' }, permissions: permissions[name] });
        },
      }),
    );
    setPinSession(sessionFor('CAROL', 'Cashier'));
    renderRoutes(routes, '/pos/tables');
    expect(await screen.findByRole('button', { name: 'My shift' })).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Close day' })).not.toBeInTheDocument();

    act(() => setPinSession(sessionFor('SAM', 'Supervisor')));

    expect(await screen.findByRole('button', { name: 'Close day' })).toBeInTheDocument();
  });

  it('logs out by itself after the idle time and says why for 5 seconds', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    setPinSession(sessionFor('edna', 'Waiter', { idleLogoutMinutes: 10 }));
    renderRoutes(routes, '/pos/tables');
    await screen.findByText('Counter A · edna (Waiter)');

    // 5 seconds before the deadline: still logged in; past it: logged out.
    act(() => touchSession(Date.now() - 10 * 60_000 + 5000));
    act(() => vi.advanceTimersByTime(1100));
    expect(screen.getByText('Counter A · edna (Waiter)')).toBeInTheDocument();

    act(() => vi.advanceTimersByTime(5000));
    expect(await screen.findByText('Logged out after 10 minutes without use')).toBeInTheDocument();
    expect(screen.queryByText(/edna/)).not.toBeInTheDocument();

    act(() => vi.advanceTimersByTime(6000));
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

  it.each(['user.deactivated', 'user.pin_reset'])(
    '%s for the logged-in user returns this terminal to the PIN pad',
    async (event) => {
      setPinSession(sessionFor('edna', 'Waiter'));
      renderRoutes(routes, '/pos/tables');
      await screen.findByText('Counter A · edna (Waiter)');

      act(() => hubHandlers.get(event)?.({ userId: 'id-someone-else' }));
      expect(screen.getByText('Counter A · edna (Waiter)')).toBeInTheDocument();

      act(() => hubHandlers.get(event)?.({ userId: 'id-edna' }));
      expect(await screen.findByRole('heading', { name: 'Enter your PIN' })).toBeInTheDocument();
      expect(screen.queryByText(/edna/)).not.toBeInTheDocument();
    },
  );
});
