import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { routes } from '../../app/routes';
import { updateConnection } from '../../shared/connection/status';
import { setDeviceForTests } from '../../shared/device/device';
import { setPinSession } from '../../shared/session/session';
import { deviceInfo, fakeServer, json, renderRoutes, sessionFor } from '../../test/render';
import type { Payment, TicketDetail } from '../tables/types';
import type { PosMpesa, Prompt, ReceivedPayment } from './types';

vi.mock('../../shared/connection/hub', () => ({
  startHub: vi.fn(),
  stopHub: vi.fn(async () => {}),
  onPosEvent: vi.fn(() => () => {}),
}));

describe('M-Pesa on the terminal (boards 22–25)', () => {
  const fetchMock = vi.fn<typeof fetch>();
  const calls: { method: string; path: string; body: unknown; headers: Headers }[] = [];
  let mpesa: PosMpesa;
  let payments: Payment[];
  let prompt: Prompt | null;
  let received: ReceivedPayment[];
  let permissions: Record<string, string>;

  const ticket = (): TicketDetail =>
    ({
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
      total: 1370,
      lines: [],
      lock: null,
      rowVersion: 1,
      billNumber: 1,
      newLines: 0,
      failedJobs: [],
      subtotal: 1370,
      discountTotal: 0,
      payments,
      amountDue: 1370,
      rounding: 0,
      remaining: 1370 - payments.reduce((s, p) => s + p.amount, 0),
      defaultBroughtById: 'u-edna',
      vatLines: [],
    }) as unknown as TicketDetail;

  const waiting = (): Prompt => ({
    id: 'pr1',
    ticketId: 'k1',
    phone: '0712 ••• 678',
    amount: 1370,
    status: 'Waiting',
    sentAt: new Date().toISOString(),
    expiresAt: new Date(Date.now() + 120_000).toISOString(),
    message: null,
    code: null,
    payerName: null,
    payerPhone: null,
    paidAt: null,
    paymentId: null,
    broughtByUserId: null,
    broughtBy: null,
  });

  beforeEach(() => {
    vi.stubGlobal('fetch', fetchMock);
    setDeviceForTests({ deviceToken: 'device-1' });
    setPinSession(sessionFor('CAROL', 'Cashier'));
    mpesa = {
      setUp: true,
      stkEnabled: true,
      waiterPrompts: true,
      allowTypedCodes: true,
      received: 2,
    };
    payments = [];
    prompt = null;
    permissions = { 'ticket.settle': 'Allowed', 'order.add': 'Allowed' };
    received = [
      {
        id: 'tx1',
        code: 'SKG7H2LM4P',
        at: '2026-10-20T18:46:00Z',
        payerName: 'JOHN K.',
        phone: '0712 ••• 678',
        amount: 1370,
        available: 1370,
        how: 'QR on bill',
        match: 'QrOnThisBill',
      },
      {
        id: 'tx2',
        code: 'SKG7F1MN8T',
        at: '2026-10-20T18:30:00Z',
        payerName: 'PETER O.',
        phone: '0733 ••• 552',
        amount: 500,
        available: 500,
        how: 'Paid to till',
        match: 'None',
      },
    ];
    calls.length = 0;
    updateConnection({ apiReachable: true, hub: 'connected' });
    fetchMock.mockImplementation(async (input, init) => {
      const url = new URL(String(input), 'http://localhost');
      const method = (init?.method ?? 'GET').toUpperCase();
      const body = typeof init?.body === 'string' ? JSON.parse(init.body) : undefined;
      calls.push({ method, path: url.pathname, body, headers: new Headers(init?.headers) });
      return fakeServer({
        'GET /api/device': () => json(200, deviceInfo({ openedAt: '2026-10-20T08:14:00' })),
        'GET /api/brand': () => json(200, { name: 'Demo Bar', currency: 'KES' }),
        'GET /api/auth/me': () =>
          json(200, { user: { id: 'id-CAROL', name: 'CAROL', role: 'Cashier' }, permissions }),
        'GET /api/pos/mpesa': () => json(200, mpesa),
        'GET /api/pos/tickets/k1': () => json(200, ticket()),
        'GET /api/pos/tickets/k1/brought-by': () =>
          json(200, [{ userId: 'u-edna', name: 'edna', role: 'Waiter', isDefault: true }]),
        'GET /api/pos/mpesa/tickets/k1/prompts/current': () =>
          prompt ? json(200, prompt) : new Response(null, { status: 204 }),
        'POST /api/pos/mpesa/tickets/k1/prompts': () => {
          prompt = waiting();
          return json(200, prompt);
        },
        'POST /api/pos/mpesa/prompts/pr1/status': () => json(200, prompt),
        'POST /api/pos/mpesa/prompts/pr1/cancel': () =>
          json(200, { ...prompt, status: 'Cancelled', message: 'Cancelled on the POS.' }),
        'GET /api/pos/mpesa/tickets/k1/received': () => json(200, received),
        'POST /api/pos/tickets/k1/payments': ({ body: b }) => {
          const r = b as { mpesaTransactionId?: string };
          payments = [
            ...payments,
            {
              id: 'p1',
              type: 'Mpesa',
              amount: 1370,
              tendered: null,
              change: null,
              mpesaCode: r.mpesaTransactionId ? 'SKG7H2LM4P' : null,
              cardReference: null,
              broughtById: 'u-edna',
              broughtBy: 'edna',
              takenBy: 'CAROL',
              status: 'Pending',
              takenAt: new Date().toISOString(),
            },
          ];
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
      })(input, init);
    });
  });

  afterEach(() => {
    fetchMock.mockReset();
    vi.unstubAllGlobals();
    setPinSession(null);
  });

  const openMpesa = async () => {
    const user = userEvent.setup();
    renderRoutes(routes, '/pos/ticket/k1/settle');
    await user.click(await screen.findByRole('button', { name: 'M-Pesa' }));
    return user;
  };

  it('offers a prompt, payments received and a typed code, and sends a prompt for the remaining amount', async () => {
    const user = await openMpesa();
    const choices = await screen.findByRole('group', { name: 'How the customer pays' });
    expect(
      within(choices)
        .getAllByRole('button')
        .map((b) => b.textContent),
    ).toEqual(['Send prompt to phone', 'Payments received (2)', 'Type a code']);
    const send = screen.getByRole('button', { name: /Send prompt · KSh 1,370/ });
    expect(send).toBeDisabled();
    await user.type(screen.getByLabelText("Customer's phone number"), '0712345678');
    expect(screen.getByLabelText("Customer's phone number")).toHaveValue('0712 345 678');
    await user.click(send);

    await screen.findByText('Waiting for the customer to enter their PIN…');
    const sent = calls.find((c) => c.path === '/api/pos/mpesa/tickets/k1/prompts')!;
    expect(sent.body).toEqual({
      phone: '0712345678',
      amount: 1370,
      replace: false,
      fromOrderScreen: false,
    });
    expect(sent.headers.has('Idempotency-Key')).toBe(true);

    // The answer arrives: paid.
    prompt = {
      ...waiting(),
      status: 'Paid',
      code: 'SKG7G2AB7K',
      payerName: 'JOHN K.',
      paidAt: new Date().toISOString(),
      paymentId: 'p1',
      broughtByUserId: 'u-edna',
      broughtBy: 'edna',
    };
    expect(
      await screen.findByText('KSh 1,370 received', {}, { timeout: 4000 }),
    ).toBeInTheDocument();
    expect(screen.getByText('SKG7G2AB7K')).toBeInTheDocument();
    const status = calls.find((c) => c.path === '/api/pos/mpesa/prompts/pr1/status')!;
    expect(status.headers.has('Idempotency-Key')).toBe(false);
  });

  it('lets the amount be lowered but never above what is left', async () => {
    const user = await openMpesa();
    const amount = await screen.findByLabelText('Amount');
    await user.clear(amount);
    await user.type(amount, '1400');
    expect(screen.getByRole('alert')).toHaveTextContent(
      "It can't be more than the 1,370 remaining.",
    );
    await user.clear(amount);
    await user.type(amount, '500');
    await user.type(screen.getByLabelText("Customer's phone number"), '0712345678');
    expect(screen.getByRole('button', { name: /Send prompt · KSh 500/ })).toBeEnabled();
  });

  it('adds a received payment to this bill, the QR match highlighted', async () => {
    const user = await openMpesa();
    await user.click(await screen.findByRole('button', { name: 'Payments received (2)' }));
    const row = (await screen.findByText('SKG7H2LM4P')).closest('tr')!;
    expect(within(row).getByText('QR on this bill')).toBeInTheDocument();
    expect(
      within((await screen.findByText('SKG7F1MN8T')).closest('tr')!).getByText(
        'Paid to till directly',
      ),
    ).toBeInTheDocument();
    await user.click(within(row).getByRole('button', { name: 'Add to this bill' }));
    await waitFor(() =>
      expect(
        calls.find((c) => c.method === 'POST' && c.path === '/api/pos/tickets/k1/payments')?.body,
      ).toMatchObject({
        type: 'Mpesa',
        mpesaTransactionId: 'tx1',
        amount: null,
      }),
    );
  });

  it('shows only the typed code when M-Pesa is not set up', async () => {
    mpesa = {
      setUp: false,
      stkEnabled: false,
      waiterPrompts: false,
      allowTypedCodes: true,
      received: 0,
    };
    await openMpesa();
    expect(await screen.findByText(/M-Pesa code/)).toBeInTheDocument();
    expect(screen.queryByRole('group', { name: 'How the customer pays' })).not.toBeInTheDocument();
    expect(screen.queryByLabelText("Customer's phone number")).not.toBeInTheDocument();
  });

  it('comes back to a prompt still waiting and can cancel it', async () => {
    prompt = waiting();
    const user = userEvent.setup();
    renderRoutes(routes, '/pos/ticket/k1/settle');
    await user.click(await screen.findByRole('button', { name: 'M-Pesa' }));
    await screen.findByText('Waiting for the customer to enter their PIN…');
    await user.click(screen.getByRole('button', { name: 'Cancel' }));
    expect(await screen.findByText('Prompt cancelled')).toBeInTheDocument();
  });
});
