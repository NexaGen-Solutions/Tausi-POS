import { act, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { routes } from '../app/routes';
import { updateConnection } from '../shared/connection/status';
import { setDeviceForTests } from '../shared/device/device';
import { pinSession, setPinSession } from '../shared/session/session';
import { deviceInfo, fakeServer, json, renderRoutes, sessionFor } from '../test/render';

vi.mock('../shared/connection/hub', () => ({
  startHub: vi.fn(),
  stopHub: vi.fn(async () => {}),
  onPosEvent: vi.fn(() => () => {}),
}));

const fetchMock = vi.fn<typeof fetch>();

function serve(
  login: () => Response,
  maxLength = 4,
  workPeriod: { openedAt: string } | null = null,
) {
  fetchMock.mockImplementation(
    fakeServer({
      'GET /api/device': () => json(200, deviceInfo(workPeriod)),
      'GET /api/auth/pin/max-length': () => json(200, maxLength),
      'GET /api/brand': () => json(200, { name: 'Demo Bar', currency: 'KES' }),
      'POST /api/auth/pin': login,
      'GET /api/auth/me': () =>
        json(200, { user: {}, permissions: { 'day.open_close': 'Allowed' } }),
    }),
  );
}

const dots = (count: number, max = 4) => screen.getByLabelText(`${count} of ${max} digits entered`);
const key = (name: string) => screen.getByRole('button', { name });

describe('PIN login screen', () => {
  beforeEach(() => {
    vi.stubGlobal('fetch', fetchMock);
    setDeviceForTests({ deviceToken: 'device-1' });
  });

  afterEach(() => {
    fetchMock.mockReset();
    vi.unstubAllGlobals();
    vi.useRealTimers();
    setPinSession(null);
    updateConnection({ apiReachable: true, hub: 'off' });
  });

  it('shows the terminal, business, day and connection', async () => {
    serve(() => json(401, {}));
    renderRoutes(routes, '/pos');

    expect(await screen.findByText('Counter A')).toBeInTheDocument();
    expect(screen.getByText('Demo Bar')).toBeInTheDocument();
    expect(screen.getByText('Day not open')).toBeInTheDocument();
    expect(await screen.findByText('Connected to POS-SERVER')).toBeInTheDocument();
    expect(screen.getByRole('heading', { name: 'Enter your PIN' })).toBeInTheDocument();
    expect(
      screen.getByText(
        'Each person has their own PIN, so every order and void is recorded under their name.',
      ),
    ).toBeInTheDocument();
  });

  it('shows when the work period opened', async () => {
    serve(() => json(401, {}), 4, { openedAt: '2026-10-20T08:14:00' });
    renderRoutes(routes, '/pos');

    expect(await screen.findByText('Work period open since 08:14')).toBeInTheDocument();
  });

  it('fills the dots from the keypad and the keyboard; Backspace and Escape work', async () => {
    serve(() => json(401, {}), 6);
    renderRoutes(routes, '/pos');
    await screen.findByText('Counter A');
    await screen.findByLabelText('0 of 6 digits entered');

    await userEvent.click(key('4'));
    await userEvent.click(key('8'));
    expect(dots(2, 6)).toBeInTheDocument();

    await userEvent.keyboard('21');
    expect(dots(4, 6)).toBeInTheDocument();
    await userEvent.keyboard('{Backspace}');
    expect(dots(3, 6)).toBeInTheDocument();
    await userEvent.keyboard('{Escape}');
    expect(dots(0, 6)).toBeInTheDocument();
    expect(screen.queryByText(/[0-9]{3,}/)).not.toBeInTheDocument();
  });

  it('has the keyboard as soon as it opens, and keeps it after a tap on a blank area', async () => {
    serve(() => json(401, {}), 6);
    renderRoutes(routes, '/pos');
    await screen.findByLabelText('0 of 6 digits entered');

    // No click first: a cashier with a keyboard can type straight away.
    await userEvent.keyboard('48');
    expect(dots(2, 6)).toBeInTheDocument();

    // A tap on the left panel would otherwise leave nothing focused.
    await userEvent.click(screen.getByText('Counter A'));
    await waitFor(() => expect(document.activeElement).toHaveClass('nx-pinpad'));
    await userEvent.keyboard('2');
    expect(dots(3, 6)).toBeInTheDocument();

    // The terminal window being activated again.
    act(() => {
      (document.activeElement as HTMLElement).blur();
      window.dispatchEvent(new Event('focus'));
    });
    await waitFor(() => expect(document.activeElement).toHaveClass('nx-pinpad'));
    await userEvent.keyboard('1');
    expect(dots(4, 6)).toBeInTheDocument();
  });

  it('submits by itself at the longest PIN length and goes in', async () => {
    serve(() => json(200, sessionFor('edna', 'Waiter')), 4, { openedAt: '2026-10-20T08:14:00' });
    renderRoutes(routes, '/pos');
    await screen.findByLabelText('0 of 4 digits entered');

    for (const d of ['6', '8', '1', '3']) await userEvent.click(key(d));

    expect(await screen.findByRole('button', { name: 'All tables' })).toBeInTheDocument();
    expect(screen.getByText('Counter A · edna (Waiter)')).toBeInTheDocument();
    expect(pinSession()?.user.name).toBe('edna');
    const login = fetchMock.mock.calls.find(([url]) => String(url).endsWith('/api/auth/pin'))!;
    expect(JSON.parse(login[1]!.body as string)).toEqual({ pin: '6813' });
  });

  it('Enter submits a PIN shorter than the longest', async () => {
    serve(() => json(200, sessionFor('edna', 'Waiter')), 6, { openedAt: '2026-10-20T08:14:00' });
    renderRoutes(routes, '/pos');
    await screen.findByLabelText('0 of 6 digits entered');

    await userEvent.keyboard('6813{Enter}');

    expect(await screen.findByRole('button', { name: 'All tables' })).toBeInTheDocument();
  });

  it('a wrong PIN clears the dots and says so for 3 seconds', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    const user = userEvent.setup({ advanceTimers: vi.advanceTimersByTime });
    serve(() => json(401, { status: 401, attemptsLeft: 4 }));
    renderRoutes(routes, '/pos');
    await screen.findByLabelText('0 of 4 digits entered');

    await user.keyboard('0000');

    expect(await screen.findByText('PIN not recognised')).toBeInTheDocument();
    expect(dots(0)).toBeInTheDocument();
    expect(screen.queryByText(/tries left/)).not.toBeInTheDocument();

    act(() => vi.advanceTimersByTime(3100));
    await waitFor(() => expect(screen.queryByText('PIN not recognised')).not.toBeInTheDocument());
  });

  it('a lockout counts down, disables the keypad and re-enables it at zero', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    const user = userEvent.setup({ advanceTimers: vi.advanceTimersByTime });
    serve(() => json(429, { status: 429, retryAfterSeconds: 47 }));
    renderRoutes(routes, '/pos');
    await screen.findByLabelText('0 of 4 digits entered');

    await user.keyboard('1111');

    expect(await screen.findByText('Too many attempts. Try again in 0:47')).toBeInTheDocument();
    expect(key('5')).toBeDisabled();
    expect(key('Enter')).toBeDisabled();

    act(() => vi.advanceTimersByTime(10_000));
    expect(await screen.findByText('Too many attempts. Try again in 0:37')).toBeInTheDocument();

    act(() => vi.advanceTimersByTime(37_500));
    await waitFor(() => expect(key('5')).toBeEnabled());
    expect(screen.queryByText(/Too many attempts/)).not.toBeInTheDocument();
  });

  it('disables the keypad while the server cannot be reached and recovers', async () => {
    serve(() => json(401, {}));
    renderRoutes(routes, '/pos');
    await screen.findByText('Counter A');

    act(() => updateConnection({ apiReachable: false }));
    expect(screen.getByText("Can't reach the server. Retrying…")).toBeInTheDocument();
    expect(screen.getByText('Reconnecting…')).toBeInTheDocument();
    expect(key('1')).toBeDisabled();

    act(() => updateConnection({ apiReachable: true }));
    expect(key('1')).toBeEnabled();
    expect(screen.queryByText("Can't reach the server. Retrying…")).not.toBeInTheDocument();
  });

  it('sends an unpaired device to setup, which shows a pairing code', async () => {
    setDeviceForTests({});
    fetchMock.mockImplementation(
      fakeServer({
        'POST /api/terminals/pairing': () =>
          json(200, {
            pairingId: 'p1',
            code: '277045',
            secret: 's',
            expiresAt: new Date(Date.now() + 600_000).toISOString(),
          }),
        'POST /api/terminals/pairing/claim': () => json(200, { status: 'waiting' }),
      }),
    );
    renderRoutes(routes, '/pos');

    expect(
      await screen.findByRole('heading', { name: 'Set up this terminal' }),
    ).toBeInTheDocument();
    expect(await screen.findByText('277 045')).toBeInTheDocument();
  });
});
