import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { routes } from '../../app/routes';
import { fakeServer, json, renderRoutes } from '../../test/render';

vi.mock('../../shared/connection/hub', () => ({
  startHub: vi.fn(),
  stopHub: vi.fn(async () => {}),
  onPosEvent: vi.fn(() => () => {}),
}));

const tile = (count: number, amount: number) => ({ count, amount });

const row = (code: string, status: string, extra: Record<string, unknown> = {}) => ({
  id: `id-${code}`,
  at: '2026-10-20T18:41:00Z',
  code,
  payerName: 'MARY W.',
  phone: '0722 ••• 101',
  how: 'Paid to till',
  amount: 1370,
  available: status === 'Unapplied' ? 1370 : 0,
  status,
  confirmed: status !== 'NotConfirmed',
  neverConfirmed: false,
  uses: [],
  note: null,
  ticketId: null,
  ...extra,
});

describe('M-Pesa on the admin site (boards 26 and 27)', () => {
  const fetchMock = vi.fn<typeof fetch>();
  const calls: { method: string; path: string; body: unknown }[] = [];

  beforeEach(() => {
    vi.stubGlobal('fetch', fetchMock);
    calls.length = 0;
    fetchMock.mockImplementation(async (input, init) => {
      const url = new URL(String(input), 'http://localhost');
      const method = (init?.method ?? 'GET').toUpperCase();
      calls.push({
        method,
        path: url.pathname,
        body: typeof init?.body === 'string' ? JSON.parse(init.body) : undefined,
      });
      return fakeServer({
        'GET /api/auth/me': () =>
          json(200, { user: { id: 'a', name: 'Administrator', role: 'Admin' }, permissions: {} }),
        'GET /api/brand': () => json(200, { name: 'Demo Bar', logoFileId: null, currency: 'KES' }),
        'POST /api/admin/mpesa/ledger': () =>
          json(200, {
            period: 'Tue 20 Oct 2026',
            tiles: {
              received: tile(3, 3740),
              applied: tile(1, 1370),
              unapplied: tile(1, 1370),
              notFromPos: tile(1, 1000),
              neverConfirmed: tile(1, 500),
            },
            rows: [
              row('SKG7H2LM4P', 'Applied', {
                how: 'QR on bill',
                uses: [
                  {
                    ticketId: 'k1',
                    ticketNumber: 1042,
                    receipt: 2041,
                    cashier: 'CAROL',
                    amount: 1370,
                    settled: true,
                  },
                ],
              }),
              row('SKG7G9QX2D', 'Unapplied'),
              row('SKG7D8RT1Q', 'NotConfirmed', {
                neverConfirmed: true,
                how: 'Code typed',
                amount: 500,
                ticketId: 'k9',
                note: 'typed by CAROL · no matching payment from Safaricom',
              }),
            ],
          }),
        'GET /api/admin/mpesa/ledger/open-tickets': () =>
          json(200, [{ id: 'k2', number: 1043, where: 'Table 8', remaining: 1370 }]),
        'POST /api/admin/mpesa/ledger/id-SKG7G9QX2D/apply': () =>
          json(200, row('SKG7G9QX2D', 'Applied')),
        'GET /api/admin/settings': () =>
          json(200, {
            version: 3,
            logoFileId: null,
            logoPrintUrl: null,
            sections: [
              {
                key: 'mpesa',
                title: 'M-Pesa',
                entries: [
                  ['mpesa.type', 'Enum', 'Type', 'BuyGoods'],
                  ['mpesa.tillNumber', 'String', 'Till number', '123456'],
                  ['mpesa.storeNumber', 'String', 'Store number', '654321'],
                  ['mpesa.paybillNumber', 'String', 'Paybill number', ''],
                  ['mpesa.environment', 'Enum', 'Environment', 'Sandbox'],
                  ['mpesa.relayUrl', 'String', 'Relay address', 'https://pay.nexagensolutions.com'],
                  ['mpesa.qrOnBill', 'Bool', 'Print "Scan to pay" QR on bills', true],
                  ['mpesa.stkEnabled', 'Bool', 'Send prompt to phone (STK push)', true],
                  [
                    'mpesa.waiterPrompts',
                    'Bool',
                    'Waiters can send prompts from the order screen',
                    true,
                  ],
                  ['mpesa.allowTypedCodes', 'Bool', 'Allow typing a code when offline', true],
                ].map(([key, type, label, value]) => ({
                  key,
                  type,
                  label,
                  help: '',
                  value,
                  default: value,
                  min: null,
                  max: null,
                  allowed: null,
                  reloadsTerminals: true,
                  maxLength: 10,
                  required: false,
                  rule: null,
                  editable: true,
                })),
              },
            ],
          }),
        'GET /api/admin/mpesa': () =>
          json(200, {
            secrets: {
              consumerKey: true,
              consumerSecret: true,
              passkey: true,
              relayApiKey: true,
              unreadable: false,
            },
            relay: {
              state: 'Connected',
              reason: null,
              lastMessageAt: '2026-10-20T18:46:00Z',
              lastCollectedAt: null,
            },
            c2BRegisteredAt: null,
            setUp: true,
          }),
        'PUT /api/admin/mpesa/secrets': () => json(200, {}),
        'PUT /api/admin/settings': () => json(200, {}),
        'POST /api/admin/mpesa/test': () =>
          json(200, [
            { name: 'Daraja', ok: true, message: 'Signed in to Safaricom Daraja (sandbox).' },
            { name: 'Relay', ok: false, message: "Can't reach the relay: Connection refused" },
          ]),
      })(input, init);
    });
  });

  afterEach(() => {
    fetchMock.mockReset();
    vi.unstubAllGlobals();
  });

  it('shows the tiles and rows, and applies an unapplied payment to an open ticket', async () => {
    const user = userEvent.setup();
    renderRoutes(routes, '/admin/mpesa');
    const unapplied = await screen.findByRole('region', { name: 'Unapplied' });
    expect(within(unapplied).getByText('1,370')).toBeInTheDocument();
    expect(
      within(screen.getByRole('region', { name: 'Typed, never confirmed' })).getByText('500'),
    ).toBeInTheDocument();
    const applied = screen.getByText('SKG7H2LM4P').closest('tr')!;
    expect(within(applied).getByRole('link', { name: '#1042' })).toHaveAttribute(
      'href',
      '/admin/tickets/k1',
    );
    expect(within(applied).getByText(/receipt 2041/)).toBeInTheDocument();
    const typed = screen.getByText('SKG7D8RT1Q').closest('tr')!;
    expect(within(typed).getByText('Not confirmed')).toBeInTheDocument();
    expect(within(typed).getByRole('link', { name: 'Check' })).toHaveAttribute(
      'href',
      '/admin/tickets/k9',
    );

    await user.click(
      within(screen.getByText('SKG7G9QX2D').closest('tr')!).getByRole('button', {
        name: 'Apply to ticket',
      }),
    );
    const dialog = await screen.findByRole('dialog');
    await user.selectOptions(await within(dialog).findByLabelText('Ticket'), 'k2');
    await user.click(within(dialog).getByRole('button', { name: 'Apply to ticket' }));
    await waitFor(() =>
      expect(
        calls.find((c) => c.path === '/api/admin/mpesa/ledger/id-SKG7G9QX2D/apply')?.body,
      ).toEqual({ ticketId: 'k2', amount: null }),
    );
  });

  it('settings show stored secrets as dots, change them on request, and test the connection', async () => {
    const user = userEvent.setup();
    renderRoutes(routes, '/admin/settings/mpesa');
    expect(
      await screen.findByText('Connected to the Tausi relay', { exact: false }),
    ).toBeInTheDocument();
    expect(screen.getAllByText('••••••••••••')).toHaveLength(4);
    expect(screen.getByLabelText('Till number')).toHaveValue('123456');

    await user.click(screen.getByRole('button', { name: 'Test connection' }));
    expect(
      await screen.findByText(/Can't reach the relay: Connection refused/),
    ).toBeInTheDocument();

    await user.click(screen.getAllByRole('button', { name: 'Change' })[0]);
    await user.type(screen.getByLabelText('Consumer key'), 'new-key');
    await user.click(screen.getByRole('button', { name: 'Paybill' }));
    await user.click(screen.getByRole('button', { name: 'Save changes' }));
    await waitFor(() =>
      expect(
        calls.find((c) => c.method === 'PUT' && c.path === '/api/admin/mpesa/secrets')?.body,
      ).toEqual({ consumerKey: 'new-key' }),
    );
    expect(calls.find((c) => c.method === 'PUT' && c.path === '/api/admin/settings')?.body).toEqual(
      {
        version: 3,
        values: { 'mpesa.type': 'Paybill' },
      },
    );
  });
});
