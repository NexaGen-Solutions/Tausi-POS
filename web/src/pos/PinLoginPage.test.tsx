import { screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { routes } from '../app/routes';
import { setDeviceForTests } from '../shared/device/device';
import { pinSession, setPinSession } from '../shared/session/session';
import { json, renderRoutes } from '../test/render';

vi.mock('../shared/connection/hub', () => ({
  startHub: vi.fn(),
  stopHub: vi.fn(async () => {}),
  onPosEvent: vi.fn(() => () => {}),
}));

describe('PIN login', () => {
  const fetchMock = vi.fn<typeof fetch>();

  beforeEach(() => {
    vi.stubGlobal('fetch', fetchMock);
    setDeviceForTests({ deviceToken: 'device-1' });
  });

  afterEach(() => {
    fetchMock.mockReset();
    vi.unstubAllGlobals();
    setPinSession(null);
  });

  function serve(loginResponse: () => Response) {
    fetchMock.mockImplementation(async (url) => {
      const path = String(url);
      if (path.endsWith('/api/health')) return json(200, { server: 'POSSERVER2' });
      if (path.endsWith('/api/device'))
        return json(200, {
          terminalId: 't1',
          terminalName: 'Counter A',
          businessName: "Mau's Village",
        });
      if (path.endsWith('/api/brand')) return json(200, { name: "Mau's Village", currency: 'KES' });
      if (path.endsWith('/api/auth/pin')) return loginResponse();
      return json(404, {});
    });
  }

  it('shows the terminal and business, and logs in with a PIN', async () => {
    serve(() =>
      json(200, {
        token: 'jwt',
        expiresAt: new Date(Date.now() + 3600_000).toISOString(),
        user: {
          id: 'u1',
          name: 'edna',
          role: 'Waiter',
          terminalId: 't1',
          terminalName: 'Counter A',
        },
      }),
    );
    renderRoutes(routes, '/pos');

    expect(await screen.findByText('Counter A')).toBeInTheDocument();
    expect(screen.getByText("Mau's Village")).toBeInTheDocument();
    expect(await screen.findByText('Connected to POSSERVER2')).toBeInTheDocument();

    for (const d of ['6', '8', '1', '3'])
      await userEvent.click(screen.getByRole('button', { name: d }));
    await userEvent.click(screen.getByRole('button', { name: 'Enter' }));

    expect(await screen.findByText('Logged in as edna on Counter A')).toBeInTheDocument();
    expect(pinSession()?.token).toBe('jwt');
  });

  it('accepts the PIN from a physical keyboard', async () => {
    serve(() => json(401, { status: 401, attemptsLeft: 4 }));
    renderRoutes(routes, '/pos');
    await screen.findByText('Counter A');

    await userEvent.keyboard('0000{Enter}');

    expect(
      await screen.findByText('That PIN is not right. 4 tries left before this terminal locks.'),
    ).toBeInTheDocument();
  });

  it('counts down while the terminal is locked', async () => {
    serve(() => json(429, { status: 429, retryAfterSeconds: 60 }));
    renderRoutes(routes, '/pos');
    await screen.findByText('Counter A');

    await userEvent.keyboard('1111{Enter}');

    expect(await screen.findByText(/Try again in (60|59) seconds/)).toBeInTheDocument();
    await waitFor(() => expect(screen.getByRole('button', { name: 'Enter' })).toBeDisabled());
  });

  it('sends an unpaired device to setup, which shows a pairing code', async () => {
    setDeviceForTests({});
    fetchMock.mockImplementation(async (url) => {
      const path = String(url);
      if (path.endsWith('/api/terminals/pairing'))
        return json(200, {
          pairingId: 'p1',
          code: '277045',
          secret: 's',
          expiresAt: new Date(Date.now() + 600_000).toISOString(),
        });
      if (path.endsWith('/api/terminals/pairing/claim')) return json(200, { status: 'waiting' });
      return json(200, { server: 'POSSERVER2' });
    });
    renderRoutes(routes, '/pos');

    expect(
      await screen.findByRole('heading', { name: 'Set up this terminal' }),
    ).toBeInTheDocument();
    expect(await screen.findByText('277 045')).toBeInTheDocument();
  });
});
