import { act, render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { setServerVersion } from '../shared/connection/serverState';
import { fakeServer, json, renderWithProviders, sessionFor } from '../test/render';

const device = vi.hoisted(() => ({
  checkForTerminalUpdate: vi.fn(async (_server: string): Promise<string | null> => '1.1.0'),
  installTerminalUpdate: vi.fn(async () => {}),
  exitKiosk: vi.fn(async (_off: boolean) => {}),
  isTauri: vi.fn(() => true),
  kioskMode: vi.fn(() => true),
}));
vi.mock('../shared/device/device', async (actual) => ({ ...(await actual()), ...device }));

const { KioskExit } = await import('./KioskExit');
const { TerminalUpdater, IDLE_BEFORE_INSTALL } = await import('./TerminalUpdater');
const session = await import('../shared/session/session');

describe('Kiosk mode', () => {
  const fetchMock = vi.fn<typeof fetch>();
  beforeEach(() => vi.stubGlobal('fetch', fetchMock));
  afterEach(() => {
    vi.unstubAllGlobals();
    vi.clearAllMocks();
  });

  it('leaves only after five taps in the corner and an admin-site login', async () => {
    fetchMock.mockImplementation(
      fakeServer({
        'POST /api/terminals/kiosk-exit': ({ body }) =>
          (body as { password: string }).password === 'right'
            ? new Response(null, { status: 204 })
            : json(401, { status: 401, detail: 'Wrong user name or password.' }),
      }),
    );
    renderWithProviders(<KioskExit />);
    const corner = screen.getByRole('button', { name: 'Leave the POS' });
    for (let i = 0; i < 4; i++) await userEvent.click(corner);
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
    await userEvent.click(corner);
    expect(screen.getByRole('dialog', { name: 'Leave the POS' })).toBeInTheDocument();

    await userEvent.type(screen.getByLabelText('User name'), 'admin');
    await userEvent.type(screen.getByLabelText('Password'), 'wrong');
    await userEvent.click(screen.getByRole('button', { name: 'Leave' }));
    expect(await screen.findByText('Wrong user name or password.')).toBeInTheDocument();
    expect(device.exitKiosk).not.toHaveBeenCalled();

    await userEvent.clear(screen.getByLabelText('Password'));
    await userEvent.type(screen.getByLabelText('Password'), 'right');
    await userEvent.click(screen.getByLabelText('Also switch kiosk mode off on this PC'));
    await userEvent.click(screen.getByRole('button', { name: 'Leave' }));
    await vi.waitFor(() => expect(device.exitKiosk).toHaveBeenCalledWith(true));
  });

  it('shows nothing outside the terminal app or with kiosk mode off', () => {
    device.kioskMode.mockReturnValueOnce(false);
    renderWithProviders(<KioskExit />);
    expect(screen.queryByRole('button', { name: 'Leave the POS' })).not.toBeInTheDocument();
  });
});

describe('Terminal updates', () => {
  beforeEach(() => {
    vi.useFakeTimers();
    setServerVersion('1.1.0');
  });
  afterEach(() => {
    vi.useRealTimers();
    vi.clearAllMocks();
    session.endPinSession('logout');
  });

  it('installs only on the PIN screen after five minutes with nobody logged in, never mid-order', async () => {
    let now = 0;
    render(<TerminalUpdater now={() => now} />);
    await act(async () => {
      await vi.advanceTimersByTimeAsync(30_000);
    });
    expect(device.checkForTerminalUpdate).toHaveBeenCalledWith('1.1.0');

    // Someone logs in: no install while they work, however long.
    act(() => session.setPinSession(sessionFor('edna', 'Waiter')));
    now = IDLE_BEFORE_INSTALL * 3;
    await act(async () => {
      await vi.advanceTimersByTimeAsync(IDLE_BEFORE_INSTALL * 3);
    });
    expect(device.installTerminalUpdate).not.toHaveBeenCalled();

    // They log out: still waits five minutes on the PIN screen.
    act(() => session.endPinSession('logout'));
    now += IDLE_BEFORE_INSTALL - 60_000;
    await act(async () => {
      await vi.advanceTimersByTimeAsync(60_000);
    });
    expect(device.installTerminalUpdate).not.toHaveBeenCalled();
    now += 120_000;
    await act(async () => {
      await vi.advanceTimersByTimeAsync(60_000);
    });
    expect(device.installTerminalUpdate).toHaveBeenCalledTimes(1);
  });

  it('does nothing when the server offers no newer terminal', async () => {
    device.checkForTerminalUpdate.mockResolvedValueOnce(null);
    let now = 0;
    render(<TerminalUpdater now={() => now} />);
    now = IDLE_BEFORE_INSTALL * 2;
    await act(async () => {
      await vi.advanceTimersByTimeAsync(IDLE_BEFORE_INSTALL * 2);
    });
    expect(device.installTerminalUpdate).not.toHaveBeenCalled();
  });
});
