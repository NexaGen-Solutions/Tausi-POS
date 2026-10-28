import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { routes } from '../../app/routes';
import { updateConnection } from '../../shared/connection/status';
import { setDeviceForTests } from '../../shared/device/device';
import { setPinSession } from '../../shared/session/session';
import { deviceInfo, fakeServer, json, renderRoutes, sessionFor } from '../../test/render';
import type { OrderLine, Payment, TicketDetail } from '../tables/types';
import { quickCash } from './quickCash';

vi.mock('../../shared/connection/hub', () => ({
  startHub: vi.fn(),
  stopHub: vi.fn(async () => {}),
  onPosEvent: vi.fn(() => () => {}),
}));

function line(
  id: string,
  name: string,
  quantity: number,
  unitPrice: number,
  extra: Partial<OrderLine> = {},
): OrderLine {
  return {
    id,
    menuItemId: `m-${id}`,
    name,
    shortName: name,
    options: null,
    note: null,
    quantity,
    unitPrice,
    gross: quantity * unitPrice,
    status: 'Printed',
    printRound: 1,
    addedById: 'u-edna',
    addedBy: 'edna',
    addedOn: 'Counter A',
    addedAt: '2026-10-20T19:42:00Z',
    printedAt: '2026-10-20T19:42:00Z',
    voided: false,
    ...extra,
  };
}

describe('settle screen', () => {
  const fetchMock = vi.fn<typeof fetch>();
  const calls: { method: string; path: string; body: unknown }[] = [];
  let payments: Payment[];
  let lines: OrderLine[];
  let newLines: number;
  let extra: Parameters<typeof fakeServer>[0];

  const total = () => lines.reduce((s, l) => s + l.gross, 0);
  const ticket = (): TicketDetail => {
    const paid = payments.reduce((s, p) => s + p.amount, 0);
    return {
      id: 'k1',
      number: 1042,
      status: 'Open',
      tableId: 't-7',
      tableName: '7',
      customerName: null,
      openedBy: 'edna',
      openedAt: '2026-10-20T19:00:00Z',
      lastActivityAt: '2026-10-20T19:42:00Z',
      note: '',
      total: total(),
      lines,
      lock: null,
      rowVersion: 1,
      billNumber: 1,
      newLines,
      failedJobs: [],
      subtotal: total(),
      discountTotal: 0,
      payments,
      amountDue: total(),
      rounding: 0,
      remaining: total() - paid,
      defaultBroughtById: 'u-edna',
      vatLines: [{ label: 'Total includes VAT 16%', amount: 175.18 }],
    };
  };

  beforeEach(() => {
    vi.stubGlobal('fetch', fetchMock);
    setDeviceForTests({ deviceToken: 'device-1' });
    setPinSession(sessionFor('CAROL', 'Cashier'));
    payments = [];
    newLines = 0;
    lines = [
      line('l1', 'Tusker Lager', 2, 250, { options: 'Cold' }),
      line('l2', 'Guiness', 1, 300),
      line('l3', 'House Water 1L', 1, 100),
      line('l4', 'Sodas 300ml', 2, 100, { options: 'Cold' }),
      line('l5', 'Pineapple Punch', 1, 270),
    ];
    extra = {};
    calls.length = 0;
    updateConnection({ apiReachable: true, hub: 'connected' });
    fetchMock.mockImplementation(async (input, init) => {
      const url = new URL(String(input), 'http://localhost');
      const method = (init?.method ?? 'GET').toUpperCase();
      const body = typeof init?.body === 'string' ? JSON.parse(init.body) : undefined;
      calls.push({ method, path: url.pathname, body });
      return fakeServer({
        'GET /api/device': () => json(200, deviceInfo({ openedAt: '2026-10-20T08:14:00' })),
        'GET /api/brand': () => json(200, { name: 'Demo Bar', currency: 'KES' }),
        'GET /api/auth/me': () =>
          json(200, {
            user: { id: 'id-CAROL', name: 'CAROL', role: 'Cashier' },
            permissions: { 'ticket.settle': 'Allowed' },
          }),
        'GET /api/pos/tickets/k1': () => json(200, ticket()),
        'POST /api/pos/tickets/k1/lock': () =>
          json(200, { editable: true, heldBy: null, message: null }),
        'DELETE /api/pos/tickets/k1/lock': () => new Response(null, { status: 204 }),
        'GET /api/pos/tickets/k1/brought-by': () =>
          json(200, [
            { userId: 'u-edna', name: 'edna', role: 'Waiter', isDefault: true },
            { userId: 'u-carol', name: 'CAROL', role: 'Cashier', isDefault: false },
          ]),
        'POST /api/pos/tickets/k1/payments': ({ body: b }) => {
          const r = b as {
            type: Payment['type'];
            tendered?: number;
            amount?: number;
            mpesaCode?: string;
            broughtByUserId: string;
          };
          const left = ticket().remaining!;
          const amount = r.type === 'Cash' ? Math.min(r.tendered!, left) : r.amount!;
          payments = [
            ...payments,
            {
              id: `p${payments.length + 1}`,
              type: r.type,
              amount,
              tendered: r.type === 'Cash' ? r.tendered! : null,
              change: r.type === 'Cash' ? r.tendered! - amount : null,
              mpesaCode: r.mpesaCode ?? null,
              cardReference: null,
              broughtById: r.broughtByUserId,
              broughtBy: r.broughtByUserId === 'u-carol' ? 'CAROL' : 'edna',
              takenBy: 'CAROL',
              status: 'Pending',
              takenAt: new Date().toISOString(),
            },
          ];
          return json(200, ticket());
        },
        'DELETE /api/pos/tickets/k1/payments/p1': () => {
          payments = payments.filter((p) => p.id !== 'p1');
          return json(200, ticket());
        },
        'POST /api/pos/tickets/k1/settle': () =>
          json(200, {
            ticketId: 'k1',
            receiptNumber: 2041,
            total: 1370,
            change: 130,
            logOut: false,
          }),
        'POST /api/pos/tickets/k1/print': () => {
          newLines = 0;
          return json(200, ticket());
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
    setPinSession(null);
  });

  async function open() {
    const router = renderRoutes(routes, '/pos/ticket/k1/settle');
    await screen.findByText('Settle · Table 7 · #1042');
    await screen.findByText('2 × Tusker Lager (Cold)');
    return router;
  }

  it('offers the exact remaining amount and the next round amounts', () => {
    expect(quickCash(370)).toEqual([370, 400, 500, 1000]);
    expect(quickCash(1370)).toEqual([1370, 1400, 1500, 2000]);
    expect(quickCash(500)).toEqual([500, 600, 1000]);
    expect(quickCash(0)).toEqual([]);
  });

  it('M-Pesa then cash: remaining goes down, change shows large, then complete gives the change and goes back to the map', async () => {
    const user = userEvent.setup();
    const router = await open();
    expect(screen.getByText('KSh 1,370', { selector: '.settle-total' })).toBeInTheDocument();
    expect(screen.getByText('Total includes VAT 16%')).toBeInTheDocument();
    const complete = screen.getByRole('button', { name: 'Complete and print receipt' });
    expect(complete).toBeDisabled();

    await user.click(screen.getByRole('button', { name: 'M-Pesa' }));
    const code = screen.getByLabelText('M-Pesa code from the SMS');
    await user.type(code, 'sjk3abc123');
    expect(code).toHaveValue('SJK3ABC123');
    await user.type(screen.getByLabelText('Amount'), '1000');
    await user.click(screen.getByRole('button', { name: 'Add M-Pesa payment' }));
    await waitFor(() => expect(screen.getByText('M-Pesa SJK3ABC123')).toBeInTheDocument());
    expect(
      calls.find((c) => c.method === 'POST' && c.path === '/api/pos/tickets/k1/payments')!.body,
    ).toEqual({
      type: 'Mpesa',
      mpesaCode: 'SJK3ABC123',
      amount: 1000,
      broughtByUserId: 'u-edna',
    });
    expect(
      screen.getByText('KSh 370', { selector: '.settle-remaining .settle-amount' }),
    ).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Cash' }));
    expect(screen.getByText('Cash for the remaining KSh 370')).toBeInTheDocument();
    expect(
      within(document.querySelector<HTMLElement>('.settle-quick')!)
        .getAllByRole('button')
        .map((b) => b.textContent),
    ).toEqual(['370', '400', '500', '1,000']);
    expect(screen.getByLabelText('Cash brought by')).toHaveDisplayValue('edna (waiter on Table 7)');
    await user.click(screen.getByRole('button', { name: '500' }));
    expect(screen.getByLabelText('Cash received')).toHaveValue('500');
    expect(screen.getByText('130', { selector: '.settle-change__amount' })).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Add cash payment' }));
    await waitFor(() => expect(screen.getByText('Paid')).toBeInTheDocument());
    expect(screen.getByText('Cash (edna)')).toBeInTheDocument();

    await user.click(complete);
    const dialog = await screen.findByRole('dialog', { name: 'Give change' });
    expect(within(dialog).getByText('KSh 130')).toBeInTheDocument();
    await user.click(within(dialog).getByRole('button', { name: 'Complete and print receipt' }));

    await waitFor(() => expect(router.state.location.pathname).toBe('/pos/tables'));
    expect(calls.some((c) => c.method === 'POST' && c.path === '/api/pos/tickets/k1/settle')).toBe(
      true,
    );
    expect(await screen.findByText('Receipt 2041 printed.')).toBeInTheDocument();
  });

  it('removing a pending payment brings the remaining back', async () => {
    const user = userEvent.setup();
    await open();
    await user.click(screen.getByRole('button', { name: 'Card' }));
    await user.click(screen.getByRole('button', { name: 'Add card payment' }));
    await waitFor(() => expect(screen.getByText('Paid')).toBeInTheDocument());
    expect(screen.getByRole('button', { name: 'Complete and print receipt' })).toBeEnabled();

    await user.click(screen.getByRole('button', { name: 'Remove Card 1,370' }));
    await waitFor(() => expect(screen.getByText('Remaining')).toBeInTheDocument());
    expect(screen.getByRole('button', { name: 'Complete and print receipt' })).toBeDisabled();
  });

  it('with brought-by off and the M-Pesa code optional: no Brought by field, and M-Pesa without a code shows No code', async () => {
    const user = userEvent.setup();
    extra = {
      'GET /api/device': () =>
        json(200, {
          ...deviceInfo({ openedAt: '2026-10-20T08:14:00' }),
          rules: {
            approvalsFromOwnCounter: true,
            recordBroughtBy: false,
            mpesaCodeRequired: false,
            closeVoidedWithoutReceipt: true,
            returnOnVoidIfNotServed: true,
            approvalChime: true,
          },
        }),
    };
    await open();
    await waitFor(() => expect(screen.queryByLabelText(/brought/i)).not.toBeInTheDocument());

    await user.click(screen.getByRole('button', { name: 'M-Pesa' }));
    expect(screen.queryByLabelText(/brought/i)).not.toBeInTheDocument();
    await user.type(screen.getByLabelText('M-Pesa code (optional)'), 'SJK');
    expect(screen.getByRole('button', { name: 'Add M-Pesa payment' })).toBeDisabled();
    await user.clear(screen.getByLabelText('M-Pesa code (optional)'));
    await user.click(screen.getByRole('button', { name: 'Add M-Pesa payment' }));

    await waitFor(() =>
      expect(calls.filter((c) => c.method === 'POST' && c.path.endsWith('/payments'))).toHaveLength(
        1,
      ),
    );
    expect(calls.find((c) => c.path.endsWith('/payments'))!.body).toMatchObject({
      type: 'Mpesa',
      mpesaCode: null,
      broughtByUserId: null,
    });
    expect(await screen.findByText('M-Pesa No code')).toBeInTheDocument();
  });

  it('a duplicate M-Pesa code shows the server message', async () => {
    const user = userEvent.setup();
    extra = {
      'POST /api/pos/tickets/k1/payments': () =>
        json(409, {
          status: 409,
          code: 'mpesa_code_used',
          detail: 'Code SJK3ABC123 was already used on #1031 (Table 3, 20:14).',
        }),
    };
    await open();
    await user.click(screen.getByRole('button', { name: 'M-Pesa' }));
    await user.type(screen.getByLabelText('M-Pesa code from the SMS'), 'SJK3ABC123');
    await user.click(screen.getByRole('button', { name: 'Add M-Pesa payment' }));
    expect(
      await screen.findByText('Code SJK3ABC123 was already used on #1031 (Table 3, 20:14).'),
    ).toBeInTheDocument();
  });

  it('with unprinted items, "Print, then settle" prints first', async () => {
    const user = userEvent.setup();
    newLines = 2;
    await open();
    expect(screen.getByText("2 items haven't been printed.")).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Print, then settle' }));
    await waitFor(() =>
      expect(calls.some((c) => c.method === 'POST' && c.path === '/api/pos/tickets/k1/print')).toBe(
        true,
      ),
    );
    await waitFor(() =>
      expect(screen.queryByText("2 items haven't been printed.")).not.toBeInTheDocument(),
    );
  });
});
