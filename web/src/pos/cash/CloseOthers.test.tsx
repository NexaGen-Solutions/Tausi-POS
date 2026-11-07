import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { routes } from '../../app/routes';
import { updateConnection } from '../../shared/connection/status';
import { setDeviceForTests } from '../../shared/device/device';
import { setPinSession } from '../../shared/session/session';
import { deviceInfo, fakeServer, json, renderRoutes, sessionFor } from '../../test/render';

vi.mock('../../shared/connection/hub', () => ({
  startHub: vi.fn(),
  stopHub: vi.fn(async () => {}),
  onPosEvent: vi.fn(() => () => {}),
}));

/** Task 19: SAM closes CAROL's shift from Close day; CAROL sees the notice at her next login. */
describe("closing another cashier's shift", () => {
  const fetchMock = vi.fn<typeof fetch>();
  const calls: { method: string; path: string; body: unknown }[] = [];
  let permissions: Record<string, string>;
  let counted: boolean;
  let notices: unknown[];

  const shift = () => ({
    id: 's1',
    status: 'Open',
    cashierId: 'id-CAROL',
    cashier: 'CAROL',
    terminalId: 't1',
    terminal: 'Counter A',
    openedAt: '2026-10-25T18:04:00Z',
    closedAt: null,
    openingFloat: 2000,
    cashSettled: 370,
    payOuts: 0,
    drops: 0,
    expected: counted ? 2370 : null,
    mpesaCount: 0,
    mpesaTotal: 0,
    cardCount: 0,
    cardTotal: 0,
    blind: true,
    countSubmitted: counted,
    countNotes: {},
    countCoins: counted ? 2270 : 0,
    counted: counted ? 2270 : null,
    difference: counted ? -100 : null,
    note: null,
    signedOffBy: null,
    receipts: 1,
    firstReceipt: 1,
    lastReceipt: 1,
    denominations: [1000, 500],
    waiters: [],
    movements: [],
    categories: [],
    closedBy: counted ? 'SAM' : null,
    closedByOther: counted,
    closeReason: counted ? 'Cashier left without closing' : null,
  });

  beforeEach(() => {
    vi.stubGlobal('fetch', fetchMock);
    setDeviceForTests({ deviceToken: 'device-1' });
    setPinSession(sessionFor('SAM', 'Supervisor'));
    permissions = { 'day.open_close': 'Allowed', 'cash.close_others_shift': 'Allowed' };
    counted = false;
    notices = [];
    calls.length = 0;
    updateConnection({ apiReachable: true, hub: 'connected' });
    fetchMock.mockImplementation(async (input, init) => {
      const url = new URL(String(input), 'http://localhost');
      const method = (init?.method ?? 'GET').toUpperCase();
      const body = typeof init?.body === 'string' ? JSON.parse(init.body) : undefined;
      calls.push({ method, path: url.pathname, body });
      return fakeServer({
        'GET /api/device': () => json(200, deviceInfo({ openedAt: '2026-10-25T08:00:00' })),
        'GET /api/brand': () => json(200, { name: 'Demo Bar', currency: 'KES' }),
        'GET /api/auth/me': () =>
          json(200, { user: { id: 'id-SAM', name: 'SAM', role: 'Supervisor' }, permissions }),
        'GET /api/pos/shift-notices': () => json(200, notices),
        'POST /api/pos/shift-notices/s1/ok': () => {
          notices = [];
          return new Response(null, { status: 204 });
        },
        'GET /api/pos/day': () =>
          json(200, {
            open: true,
            workPeriodId: 'w1',
            openedAt: '2026-10-25T08:00:00Z',
            openedBy: 'SAM',
            summary: null,
            canCloseShifts: permissions['cash.close_others_shift'] === 'Allowed',
            blockers: {
              blocked: true,
              openTickets: [],
              pendingRequests: [],
              openShifts: [
                {
                  id: 's1',
                  cashier: 'CAROL',
                  terminal: 'Counter A',
                  openedAt: '2026-10-25T18:04:00Z',
                },
              ],
            },
          }),
        'GET /api/pos/shifts/open': () =>
          json(200, [
            {
              id: 's1',
              cashierId: 'id-CAROL',
              cashier: 'CAROL',
              terminalId: 't1',
              terminal: 'Counter A',
              openedAt: '2026-10-25T18:04:00Z',
              cashTaken: 370,
              counted,
              closingBy: null,
              pending: [],
            },
          ]),
        'GET /api/pos/shifts/s1/other': () => json(200, shift()),
        'POST /api/pos/shifts/s1/other/count': () => {
          counted = true;
          return json(200, shift());
        },
        'POST /api/pos/shifts/s1/other/close': () =>
          json(200, { ...shift(), status: 'Closed', signedOffBy: 'SAM', countedAlone: true }),
        'GET /api/pos/tables': () =>
          json(200, {
            version: 1,
            serverTime: new Date().toISOString(),
            dayOpen: true,
            tableIdleMinutes: 30,
            areas: [],
            tables: [],
          }),
      })(input, init);
    });
  });

  afterEach(() => {
    fetchMock.mockReset();
    vi.unstubAllGlobals();
    setPinSession(null);
  });

  it('closes a shift left open from Close day: reason, blind count, then alone with the tick box', async () => {
    const user = userEvent.setup();
    renderRoutes(routes, '/pos/day');
    await user.click(await screen.findByRole('button', { name: 'Close this shift' }));

    expect(
      await screen.findByText(/Closing CAROL's shift · Counter A · opened/),
    ).toBeInTheDocument();
    expect(screen.getByRole('note')).toHaveTextContent(
      "You're closing another cashier's shift. The cash must be counted now.",
    );
    const submit = screen.getByRole('button', { name: /Submit the count/ });
    expect(submit).toBeDisabled();
    await user.selectOptions(
      screen.getByLabelText('Why are you closing it?'),
      'Cashier left without closing',
    );
    await user.type(screen.getByLabelText('Number of 1,000 notes'), '2');
    await user.click(screen.getByRole('button', { name: /Submit the count · 2,000/ }));
    await waitFor(() =>
      expect(calls.find((c) => c.path === '/api/pos/shifts/s1/other/count')?.body).toMatchObject({
        reason: 'Cashier left without closing',
        count: { notes: { 1000: 2 } },
      }),
    );

    expect(await screen.findByText('− 100', { exact: false })).toBeInTheDocument();
    await user.click(screen.getByLabelText('I counted this cash alone'));
    await user.click(screen.getByRole('button', { name: 'Close the shift' }));
    await waitFor(() =>
      expect(calls.find((c) => c.path === '/api/pos/shifts/s1/other/close')?.body).toEqual({
        countedAlone: true,
      }),
    );
  });

  it('without the permission the shift says to ask a supervisor', async () => {
    permissions = { 'day.open_close': 'Allowed', 'cash.close_others_shift': 'NotAllowed' };
    renderRoutes(routes, '/pos/day');
    const row = (await screen.findByText(/CAROL · Counter A/)).closest('li')!;
    expect(within(row).getByText('Ask a supervisor to close it')).toBeInTheDocument();
    expect(within(row).queryByRole('button', { name: 'Close this shift' })).not.toBeInTheDocument();
  });

  it('shows the cashier a notice at her next login until she taps OK', async () => {
    setPinSession(sessionFor('CAROL', 'Cashier'));
    permissions = { 'ticket.settle': 'Allowed' };
    notices = [
      {
        shiftId: 's1',
        terminal: 'Counter A',
        closedBy: 'SAM',
        closedAt: '2026-10-25T20:40:00Z',
        difference: -100,
        reason: 'Cashier left without closing',
      },
    ];
    const user = userEvent.setup();
    renderRoutes(routes, '/pos/tables');
    const dialog = await screen.findByRole('dialog', { name: 'Your shift was closed' });
    expect(dialog).toHaveTextContent('Your shift on Counter A was closed by SAM');
    expect(dialog).toHaveTextContent('Difference: − 100.');
    await user.click(within(dialog).getByRole('button', { name: 'OK' }));
    await waitFor(() =>
      expect(
        screen.queryByRole('dialog', { name: 'Your shift was closed' }),
      ).not.toBeInTheDocument(),
    );
    expect(
      calls.some((c) => c.method === 'POST' && c.path === '/api/pos/shift-notices/s1/ok'),
    ).toBe(true);
  });
});
