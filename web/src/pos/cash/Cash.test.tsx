import { act, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { routes } from '../../app/routes';
import { updateConnection } from '../../shared/connection/status';
import { setDeviceForTests } from '../../shared/device/device';
import { pinSession, setPinSession } from '../../shared/session/session';
import { deviceInfo, fakeServer, json, renderRoutes, sessionFor } from '../../test/render';
import type { Shift } from './types';

const hub = vi.hoisted(() => new Map<string, Set<(payload: unknown) => void>>());
vi.mock('../../shared/connection/hub', () => ({
  startHub: vi.fn(),
  stopHub: vi.fn(async () => {}),
  onPosEvent: vi.fn((name: string, handler: (payload: unknown) => void) => {
    if (!hub.has(name)) hub.set(name, new Set());
    hub.get(name)!.add(handler);
    return () => hub.get(name)!.delete(handler);
  }),
}));
const emit = (name: string, payload: unknown) =>
  act(() => hub.get(name)?.forEach((h) => h(payload)));

function shift(extra: Partial<Shift> = {}): Shift {
  return {
    id: 's1',
    status: 'Open',
    cashierId: 'id-CAROL',
    cashier: 'CAROL',
    terminalId: 't1',
    terminal: 'Counter A',
    openedAt: '2026-10-22T16:00:00Z',
    closedAt: null,
    openingFloat: 2000,
    cashSettled: 370,
    payOuts: 200,
    drops: 1000,
    expected: null,
    mpesaCount: 2,
    mpesaTotal: 1300,
    cardCount: 1,
    cardTotal: 270,
    blind: true,
    countSubmitted: false,
    countNotes: {},
    countCoins: 0,
    counted: null,
    difference: null,
    note: null,
    signedOffBy: null,
    receipts: 3,
    firstReceipt: 1,
    lastReceipt: 3,
    denominations: [1000, 500, 200, 100, 50],
    waiters: [
      { userId: 'u-edna', name: 'edna', cash: 370, openBills: 0 },
      { userId: 'u-cyn', name: 'CYNTHIA', cash: 0, openBills: 1 },
    ],
    movements: [],
    categories: [{ id: 'c-transport', name: 'Transport' }],
    ...extra,
  };
}

describe('cash screens', () => {
  const fetchMock = vi.fn<typeof fetch>();
  const calls: { method: string; path: string; body: unknown; headers: Headers }[] = [];
  let current: Shift | null;
  let permissions: Record<string, string>;
  let extra: Parameters<typeof fakeServer>[0];

  beforeEach(() => {
    vi.stubGlobal('fetch', fetchMock);
    setDeviceForTests({ deviceToken: 'device-1' });
    setPinSession(sessionFor('CAROL', 'Cashier'));
    current = null;
    permissions = { 'ticket.settle': 'Allowed', 'reports.own': 'Allowed' };
    extra = {};
    calls.length = 0;
    hub.clear();
    updateConnection({ apiReachable: true, hub: 'connected' });
    fetchMock.mockImplementation(async (input, init) => {
      const url = new URL(String(input), 'http://localhost');
      const method = (init?.method ?? 'GET').toUpperCase();
      const body = typeof init?.body === 'string' ? JSON.parse(init.body) : undefined;
      calls.push({ method, path: url.pathname, body, headers: new Headers(init?.headers) });
      return fakeServer({
        'GET /api/device': () => json(200, deviceInfo({ openedAt: '2026-10-22T08:14:00' })),
        'GET /api/brand': () => json(200, { name: "Demo Bar", currency: 'KES' }),
        'GET /api/auth/me': () =>
          json(200, { user: { id: 'id-CAROL', name: 'CAROL', role: 'Cashier' }, permissions }),
        'GET /api/pos/shifts/current': () =>
          json(200, {
            shift: current,
            start: { defaultFloat: 0, denominations: [1000, 500, 200, 100, 50] },
          }),
        'POST /api/pos/shifts': () => {
          current = shift({
            cashSettled: 0,
            payOuts: 0,
            drops: 0,
            openingFloat: 2000,
            waiters: [],
          });
          return json(200, current);
        },
        'GET /api/pos/tables': () =>
          json(200, {
            version: 1,
            serverTime: new Date().toISOString(),
            dayOpen: true,
            tableIdleMinutes: 30,
            areas: [],
            tables: [],
          }),
        ...extra,
      })(input, init);
    });
  });

  afterEach(() => {
    fetchMock.mockReset();
    vi.unstubAllGlobals();
    vi.useRealTimers();
    setPinSession(null);
  });

  it('starts a shift with a float counted by note', async () => {
    const user = userEvent.setup();
    renderRoutes(routes, '/pos/shift');
    await screen.findByRole('heading', { name: 'Start shift' });
    await user.type(screen.getByLabelText('Number of 1,000 notes'), '2');
    expect(
      screen.getByText('2,000', { selector: '.cash-count__total .cash-amount' }),
    ).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Start shift' }));

    await waitFor(() =>
      expect(calls.find((c) => c.method === 'POST' && c.path === '/api/pos/shifts')?.body).toEqual({
        float: { notes: { 1000: 2 }, coins: 0 },
      }),
    );
    expect(await screen.findByText('Close my shift · cash-up')).toBeInTheDocument();
  });

  it('a blind count hides the expected cash until the count is in, then sign-off goes through the approval pad', async () => {
    const user = userEvent.setup();
    current = shift();
    let token: string | null = null;
    extra = {
      'POST /api/pos/shifts/s1/count': ({ body }) => {
        current = shift({
          countSubmitted: true,
          counted: 1170,
          expected: 1170,
          difference: 0,
          countNotes: (body as { count: { notes: Record<string, number> } }).count.notes,
          countCoins: 20,
        });
        return json(200, current);
      },
      'POST /api/pos/shifts/s1/close': ({ headers }) => {
        token = headers.get('X-Approval-Token');
        return token
          ? json(200, { ...current, status: 'Closed', signedOffBy: 'SAM' })
          : json(403, { status: 403, approvalRequired: true, permission: 'day.open_close' });
      },
      'POST /api/auth/approve': () => json(200, { approvalToken: 'sign-1', approvedByName: 'SAM' }),
    };
    const router = renderRoutes(routes, '/pos/shift');

    expect(await screen.findByText('Shown after you count.')).toBeInTheDocument();
    expect(screen.queryByText('Expected cash')).not.toBeInTheDocument();
    const waiters = screen.getByRole('region', { name: 'Cash brought by waiters' });
    expect(within(waiters).getByText('1')).toHaveClass('cash-warn');
    expect(
      screen.getByRole('button', { name: 'Close shift · supervisor PIN to sign off' }),
    ).toBeDisabled();

    await user.type(screen.getByLabelText('Number of 1,000 notes'), '1');
    await user.type(screen.getByLabelText('Number of 100 notes'), '1');
    await user.type(screen.getByLabelText('Number of 50 notes'), '1');
    await user.type(screen.getByLabelText('Coins'), '20');
    const row = screen.getByLabelText('Number of 50 notes').closest('label')!;
    expect(within(row).getByText('50', { selector: '.cash-count__subtotal' })).toBeInTheDocument();
    expect(
      screen.getByText('1,170', { selector: '.cash-count__total .cash-amount' }),
    ).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Submit count' }));

    expect(await screen.findByText('Expected cash')).toBeInTheDocument();
    expect(screen.getByText('Difference').parentElement).toHaveTextContent('± 0');
    expect(screen.getByRole('button', { name: 'Submit count' })).toBeDisabled();
    expect(calls.find((c) => c.path === '/api/pos/shifts/s1/count')!.body).toEqual({
      count: { notes: { 1000: 1, 100: 1, 50: 1 }, coins: 20 },
    });

    await user.click(
      screen.getByRole('button', { name: 'Close shift · supervisor PIN to sign off' }),
    );
    await screen.findByRole('dialog', { name: 'Supervisor approval' });
    await user.keyboard('8014{Enter}');
    await waitFor(() => expect(router.state.location.pathname).toBe('/pos/tables'));
    expect(token).toBe('sign-1');
  });

  it('without a blind count the expected cash shows from the start', async () => {
    current = shift({ blind: false, expected: 1170 });
    renderRoutes(routes, '/pos/shift');
    expect(await screen.findByText('Expected cash')).toBeInTheDocument();
    expect(
      screen.getByText('1,170', { selector: '.cash-row--total .cash-amount' }),
    ).toBeInTheDocument();
  });

  it('pay out asks what for and a note', async () => {
    const user = userEvent.setup();
    current = shift({ blind: false, expected: 1170 });
    extra = {
      'POST /api/pos/shifts/s1/pay-out': () =>
        json(200, shift({ blind: false, expected: 970, payOuts: 400 })),
    };
    renderRoutes(routes, '/pos/shift');
    await user.click(await screen.findByRole('button', { name: 'Pay out' }));
    const dialog = await screen.findByRole('dialog', { name: 'Pay out' });
    await user.type(within(dialog).getByLabelText('Amount'), '200');
    const save = within(dialog).getAllByRole('button', { name: 'Pay out' }).at(-1)!;
    expect(save).toBeDisabled();
    await user.type(
      within(dialog).getByLabelText('Note (what was bought or paid)'),
      'Taxi for ice',
    );
    await user.click(save);
    await waitFor(() =>
      expect(calls.find((c) => c.path === '/api/pos/shifts/s1/pay-out')?.body).toEqual({
        amount: 200,
        categoryId: 'c-transport',
        note: 'Taxi for ice',
      }),
    );
  });

  it('settling without a shift starts one inline, then carries on', async () => {
    const user = userEvent.setup();
    extra = {
      'GET /api/pos/tickets/k1': () =>
        json(200, {
          id: 'k1',
          number: 1042,
          status: 'Open',
          tableId: 't-7',
          tableName: '7',
          customerName: null,
          openedBy: 'edna',
          openedAt: '2026-10-22T19:00:00Z',
          lastActivityAt: '2026-10-22T19:00:00Z',
          note: '',
          total: 300,
          lines: [],
          lock: null,
          rowVersion: 1,
          billNumber: 1,
          newLines: 0,
          failedJobs: [],
          payments: [],
          amountDue: 300,
          rounding: 0,
          remaining: 300,
          defaultBroughtById: 'u-edna',
          vatLines: [],
        }),
      'POST /api/pos/tickets/k1/lock': () =>
        json(200, { editable: true, heldBy: null, message: null }),
      'DELETE /api/pos/tickets/k1/lock': () => new Response(null, { status: 204 }),
      'GET /api/pos/tickets/k1/brought-by': () => json(200, []),
    };
    renderRoutes(routes, '/pos/ticket/k1/settle');
    expect(await screen.findByRole('heading', { name: 'Start your shift' })).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Cash' })).not.toBeInTheDocument();
    await user.type(screen.getByLabelText('Number of 1,000 notes'), '2');
    await user.click(screen.getByRole('button', { name: 'Start shift' }));
    expect(await screen.findByRole('button', { name: 'Cash' })).toBeInTheDocument();
    expect(screen.getByText('Cash for the remaining KSh 300')).toBeInTheDocument();
  });

  it('close day lists what blocks it with links, then shows the summary', async () => {
    const user = userEvent.setup();
    setPinSession(sessionFor('SAM', 'Supervisor'));
    permissions = {
      'day.open_close': 'Allowed',
      'ticket.settle': 'Allowed',
      'reports.all': 'Allowed',
    };
    let blocked = true;
    extra = {
      'GET /api/pos/day': () =>
        json(200, {
          open: true,
          workPeriodId: 'w1',
          openedAt: '2026-10-22T08:00:00Z',
          openedBy: 'SAM',
          blockers: blocked
            ? {
                openTickets: [
                  {
                    id: 'k9',
                    number: 1050,
                    where: 'Table 9',
                    waiter: 'edna',
                    total: 300,
                    paid: 100,
                  },
                ],
                openShifts: [
                  {
                    id: 's1',
                    cashier: 'CAROL',
                    terminal: 'Counter A',
                    openedAt: '2026-10-22T16:00:00Z',
                  },
                ],
                pendingRequests: [],
                blocked: true,
              }
            : { openTickets: [], openShifts: [], pendingRequests: [], blocked: false },
          summary: blocked
            ? null
            : {
                tickets: 4,
                gross: 2240,
                discounts: 0,
                gifts: 300,
                voids: 250,
                net: 1931.04,
                vat: 308.96,
                vatRates: [{ label: 'VAT 16%', net: 1931.04, vat: 308.96 }],
                payments: [
                  { type: 'Cash', count: 1, amount: 370 },
                  { type: 'M-Pesa', count: 2, amount: 1300 },
                  { type: 'Card', count: 2, amount: 570 },
                ],
                paymentsTotal: 2240,
                rounding: 0,
                shiftDifferences: [{ cashier: 'CAROL', terminal: 'Counter A', difference: 0 }],
                payOuts: 200,
                profit: { net: -150 },
              },
        }),
      'POST /api/pos/day/close': () => json(200, {}),
      'POST /api/pos/reports/sales-by-user/print': () => new Response(null, { status: 204 }),
    };
    renderRoutes(routes, '/pos/tables');
    await user.click(await screen.findByRole('button', { name: 'Close day' }));

    expect(await screen.findByText("The day can't close yet")).toBeInTheDocument();
    expect(screen.getByRole('link', { name: /#1050 · Table 9 · edna · 300/ })).toHaveAttribute(
      'href',
      '/pos/ticket/k9',
    );
    expect(screen.getByText('· Part paid')).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'CAROL · Counter A' })).toBeInTheDocument();
    expect(screen.getAllByRole('button', { name: 'Close day' }).at(-1)).toBeDisabled();

    blocked = false;
    await user.click(screen.getByRole('button', { name: 'Tables' }));
    await user.click(await screen.findByRole('button', { name: 'Close day' }));
    expect(await screen.findByText('Nothing blocks the close')).toBeInTheDocument();
    expect(screen.getByText('KSh 2,240')).toBeInTheDocument();
    expect(screen.getByText('Net for the day').nextSibling).toHaveTextContent('− 150');
    await user.click(screen.getByRole('button', { name: 'Print sales by user' }));
    expect(await screen.findByText('Sales by user is printing.')).toBeInTheDocument();
    expect(
      calls.some((c) => c.method === 'POST' && c.path === '/api/pos/reports/sales-by-user/print'),
    ).toBe(true);
    await user.click(screen.getAllByRole('button', { name: 'Close day' }).at(-1)!);
    await user.click(
      within(await screen.findByRole('dialog')).getByRole('button', { name: 'Close day' }),
    );
    await waitFor(() =>
      expect(calls.some((c) => c.method === 'POST' && c.path === '/api/pos/day/close')).toBe(true),
    );
  });

  it('every terminal gets the 60-second warning and logs out when the day closes', async () => {
    renderRoutes(routes, '/pos/tables');
    await screen.findByRole('button', { name: 'Log out' });
    vi.useFakeTimers({ shouldAdvanceTime: true });
    emit('day.closed', {
      workPeriodId: 'w1',
      closedAt: new Date().toISOString(),
      logoutAt: new Date(Date.now() + 60_000).toISOString(),
    });
    expect(
      await screen.findByText("The day is closing. Finish what you're doing."),
    ).toBeInTheDocument();
    expect(pinSession()).not.toBeNull();
    act(() => vi.advanceTimersByTime(61_000));
    await waitFor(() => expect(pinSession()).toBeNull());
  });

  it('my sales shows only my figures, the open bills, and void and gift markers', async () => {
    const user = userEvent.setup();
    setPinSession(sessionFor('edna', 'Waiter'));
    permissions = { 'reports.own': 'Allowed' };
    extra = {
      'GET /api/pos/my-sales': () =>
        json(200, {
          name: 'edna',
          periodOpenedAt: '2026-10-22T08:00:00Z',
          total: 3400,
          tickets: 6,
          cash: 1370,
          mpesa: 1000,
          card: 270,
          openBills: [{ id: 'k7', number: 1047, where: 'Table 7', total: 540 }],
          lines: [
            {
              ticketId: 'k1',
              ticketNumber: 1042,
              at: '2026-10-22T19:42:00Z',
              name: 'Johnnie Walker Black Label Blended Scotch Whisky 750ml',
              options: null,
              quantity: 1,
              amount: 4500,
              voided: false,
              gift: false,
            },
            {
              ticketId: 'k1',
              ticketNumber: 1042,
              at: '2026-10-22T19:40:00Z',
              name: 'Tusker Lager',
              options: 'Cold',
              quantity: 1,
              amount: 250,
              voided: true,
              gift: false,
            },
            {
              ticketId: 'k1',
              ticketNumber: 1042,
              at: '2026-10-22T19:30:00Z',
              name: 'Guiness',
              options: null,
              quantity: 1,
              amount: 0,
              voided: false,
              gift: true,
            },
          ],
        }),
      'POST /api/pos/my-sales/print': () => new Response(null, { status: 204 }),
    };
    renderRoutes(routes, '/pos/my-sales');

    expect(await screen.findByText('My sales · edna · today')).toBeInTheDocument();
    expect(screen.getByText('Only your own figures are shown')).toBeInTheDocument();
    expect(await screen.findByText('KSh 3,400')).toBeInTheDocument();
    const brought = screen.getByRole('region', { name: 'Brought to the cashier' });
    expect(brought).toHaveTextContent('1,370');
    const voided = screen.getByText('Tusker Lager (Cold)').closest('tr')!;
    expect(voided).toHaveClass('sales-line--voided');
    expect(within(voided).getByText('Void')).toBeInTheDocument();
    expect(
      within(screen.getByText('Guiness').closest('tr')!).getByText('Gift'),
    ).toBeInTheDocument();
    expect(screen.getByText('Johnnie Walker Black Label Blended Scotch Whisky 750ml')).toHaveClass(
      'sales-item__name',
    );

    await user.click(screen.getByRole('button', { name: /Bills still open on your name/ }));
    expect(
      await screen.findByRole('button', { name: /#1047 · Table 7 · KSh 540/ }),
    ).toBeInTheDocument();
    await user.keyboard('{Escape}');
    await user.click(screen.getByRole('button', { name: 'Print my cash-up slip' }));
    expect(await screen.findByText('Your slip is printing.')).toBeInTheDocument();
  });
});
