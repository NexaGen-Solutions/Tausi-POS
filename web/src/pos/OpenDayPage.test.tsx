import { screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { routes } from '../app/routes';
import { setDeviceForTests } from '../shared/device/device';
import { pinSession, setPinSession } from '../shared/session/session';
import { deviceInfo, fakeServer, json, renderRoutes, sessionFor } from '../test/render';

vi.mock('../shared/connection/hub', () => ({
  startHub: vi.fn(),
  stopHub: vi.fn(async () => {}),
  onPosEvent: vi.fn(() => () => {}),
}));

describe('opening the day', () => {
  const fetchMock = vi.fn<typeof fetch>();
  let open = false;
  let calls: string[] = [];

  function serve(permission: 'Allowed' | 'NotAllowed') {
    fetchMock.mockImplementation(async (input, init) => {
      calls.push(`${init?.method ?? 'GET'} ${new URL(String(input), 'http://x').pathname}`);
      return fakeServer({
        'GET /api/device': () =>
          json(200, deviceInfo(open ? { openedAt: '2026-10-20T08:14:00' } : null)),
        'GET /api/auth/pin/max-length': () => json(200, 4),
        'GET /api/brand': () => json(200, { name: "Mau's Village" }),
        'GET /api/auth/me': () =>
          json(200, { user: {}, permissions: { 'day.open_close': permission } }),
        'POST /api/day/open': () => {
          open = true;
          return json(200, { id: 'wp1', openedAt: '2026-10-20T08:14:00' });
        },
        'POST /api/auth/logout': () => new Response(null, { status: 204 }),
      })(input, init);
    });
  }

  beforeEach(() => {
    vi.stubGlobal('fetch', fetchMock);
    setDeviceForTests({ deviceToken: 'device-1' });
    open = false;
    calls = [];
  });

  afterEach(() => {
    fetchMock.mockReset();
    vi.unstubAllGlobals();
    setPinSession(null);
  });

  it('a supervisor can open the day and goes on to the tables', async () => {
    serve('Allowed');
    setPinSession(sessionFor('MAU', 'Supervisor'));
    renderRoutes(routes, '/pos/day');

    expect(
      await screen.findByRole('heading', { name: "The day isn't open yet" }),
    ).toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: 'Open the day' }));

    expect(await screen.findByText('Table map: coming in Screen 2')).toBeInTheDocument();
    expect(calls).toContain('POST /api/day/open');
  });

  it('a waiter is told to ask a supervisor and Back logs out to the PIN pad', async () => {
    serve('NotAllowed');
    setPinSession(sessionFor('edna', 'Waiter'));
    renderRoutes(routes, '/pos/day');

    expect(
      await screen.findByRole('heading', {
        name: "The day isn't open yet. Ask a supervisor to open it.",
      }),
    ).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Open the day' })).not.toBeInTheDocument();

    await userEvent.click(screen.getByRole('button', { name: 'Back' }));

    expect(await screen.findByRole('heading', { name: 'Enter your PIN' })).toBeInTheDocument();
    expect(pinSession()).toBeNull();
    expect(calls).toContain('POST /api/auth/logout');
  });

  it('goes straight to the tables when the day is already open', async () => {
    open = true;
    serve('NotAllowed');
    setPinSession(sessionFor('edna', 'Waiter'));
    renderRoutes(routes, '/pos/day');

    expect(await screen.findByText('Table map: coming in Screen 2')).toBeInTheDocument();
  });
});
