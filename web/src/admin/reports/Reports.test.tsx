import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { routes } from '../../app/routes';
import { fakeServer, json, renderRoutes } from '../../test/render';
import type {
  ReportDefinition,
  ReportOptions,
  ReportResult,
  ReportRow,
  TicketReport,
} from './types';

vi.mock('../../shared/connection/hub', () => ({
  startHub: vi.fn(),
  stopHub: vi.fn(async () => {}),
  onPosEvent: vi.fn(() => () => {}),
}));

const definition = (
  key: string,
  title: string,
  filters: string[] = [],
  extra: Partial<ReportDefinition> = {},
): ReportDefinition => ({
  key,
  title,
  permission: 'reports.all',
  filters,
  usesPeriod: true,
  receipt: false,
  kinds: null,
  ...extra,
});

const list: ReportDefinition[] = [
  definition('sales-by-user', 'Sales by user', ['user', 'terminal', 'creditTo', 'includeOpen'], {
    receipt: true,
  }),
  definition('sales-by-item', 'Sales by item', ['category']),
  definition('payments', 'Payments and cash-ups', ['cashier', 'waiter', 'paymentType']),
  definition('voids', 'Voids, gifts and approvals', ['kind', 'requester', 'approver'], {
    kinds: ['Void', 'Gift'],
  }),
  definition('price-changes', 'Price changes', ['kind', 'user']),
  definition(
    'stock-on-hand',
    'Stock on hand',
    ['asAt', 'location', 'stockCategory', 'onlyBelowReorder'],
    { usesPeriod: false },
  ),
  definition('stock-variance', 'Stock variance', ['count', 'stockItem'], { usesPeriod: false }),
  definition('purchases', 'Purchases', ['supplier', 'location']),
  definition('daily-summary', 'Daily summary'),
  definition('vat', 'VAT summary'),
  definition('tickets', 'Tickets and receipts', ['status', 'flags', 'amount']),
];

const options: ReportOptions = {
  periods: [
    {
      id: 'wp-today',
      date: '2026-10-24',
      openedAt: '2026-10-24T07:00:00Z',
      closedAt: null,
      open: true,
    },
    {
      id: 'wp-yesterday',
      date: '2026-10-23',
      openedAt: '2026-10-23T07:00:00Z',
      closedAt: '2026-10-23T22:00:00Z',
      open: false,
    },
  ],
  users: [
    { id: 'u-edna', name: 'edna', detail: 'Waiter' },
    { id: 'u-sam', name: 'SAM', detail: 'Supervisor' },
  ],
  terminals: [{ id: 't-a', name: 'Counter A', detail: null }],
  locations: [],
  categories: [],
  suppliers: [],
  tables: [],
  roles: [],
  counts: [],
  stockItems: [],
  printers: [{ id: 'p-1', name: 'Ticket Printer 1', detail: null }],
};

const longName =
  'Extraordinarily long cocktail name with mango, passion fruit, lime, mint, ginger beer and a splash of house rum';

function row(ticket: number, item: string, amount: number): ReportRow {
  return {
    cells: [ticket, '2026-10-24T12:00:00Z', item, 1, amount],
    ticketId: `k-${ticket}`,
    muted: false,
  };
}

function salesByUser(): ReportResult {
  const ednaRows = Array.from({ length: 54 }, (_, i) =>
    row(1001 + i, i === 0 ? longName : `Tusker Lager ${i}`, 250),
  );
  return {
    key: 'sales-by-user',
    title: 'Sales by user',
    period: 'Sat 24 Oct 2026 (open)',
    filterText: ['Sales credited to who added the item', 'Open tickets included'],
    runAt: '2026-10-24T18:00:00Z',
    notes: ['Includes 2 open tickets'],
    isEmpty: false,
    tables: [
      {
        title: null,
        columns: [
          { key: 'ticket', label: 'Ticket', type: 'Ticket', rightAligned: false },
          { key: 'time', label: 'Time', type: 'Time', rightAligned: false },
          { key: 'item', label: 'Item', type: 'Text', rightAligned: false },
          { key: 'qty', label: 'Qty', type: 'Quantity', rightAligned: true },
          { key: 'amount', label: 'Amount', type: 'Money', rightAligned: true },
        ],
        groups: [
          {
            title: 'edna',
            subtitle: 'Waitress',
            total: 13500,
            rows: ednaRows,
            subtotal: null,
            footer: ['Tickets 54 · Gifts 0 (KSh 0)'],
          },
          {
            title: 'SAM',
            subtitle: 'Supervisor',
            total: 550,
            rows: [row(2001, 'Heineken', 300), row(2002, 'GK Smooth', 250)],
            subtotal: null,
            footer: null,
          },
        ],
        totals: { cells: ['Total', null, null, 56, 14050], ticketId: null, muted: false },
        footer: null,
      },
    ],
  };
}

const emptyResult = (key: string, title: string): ReportResult => ({
  key,
  title,
  period: 'Sat 24 Oct 2026 (open)',
  filterText: [],
  runAt: '2026-10-24T18:00:00Z',
  notes: [],
  isEmpty: true,
  tables: [
    {
      title: null,
      columns: [{ key: 'item', label: 'Item', type: 'Text', rightAligned: false }],
      groups: [
        { title: null, rows: [], subtotal: null, footer: null, subtitle: null, total: null },
      ],
      totals: null,
      footer: null,
    },
  ],
});

const ticket: TicketReport = {
  ticket: {
    id: 'k-1001',
    number: 1001,
    receiptNumber: 5001,
    table: '7',
    status: 'Settled',
    openedBy: 'edna',
    openedAt: '2026-10-24T19:00:00Z',
    openedOn: 'Counter A',
    settledBy: 'CAROL',
    settledAt: '2026-10-24T21:00:00Z',
    settledOn: 'Counter A',
    businessDate: '2026-10-24',
    periodOpenedAt: '2026-10-24T07:00:00Z',
    total: 520,
    discount: 0,
    discountLabel: null,
    rounding: 0,
    mergedIntoNumber: null,
    mergedIntoId: null,
    mergedInNumbers: [],
    note: '',
  },
  lines: [
    {
      id: 'l1',
      name: 'Guiness',
      options: null,
      note: null,
      quantity: 1,
      unitPrice: 270,
      originalUnitPrice: null,
      gross: 270,
      discount: 0,
      amount: 0,
      status: 'Voided',
      gift: false,
      addedBy: 'edna',
      terminal: 'Counter A',
      addedAt: '2026-10-24T19:00:00Z',
      printRound: 1,
      printedAt: '2026-10-24T19:01:00Z',
      voidedBy: 'SAM',
      voidedAt: '2026-10-24T19:30:00Z',
      voidReason: 'Wrong item',
      cancelledBy: null,
      cancelledAt: null,
      priceChanges: [],
    },
  ],
  prints: [
    {
      id: 'j1',
      kind: 'Void',
      label: 'VOID · Guiness',
      printer: 'Bar printer',
      at: '2026-10-24T19:30:00Z',
      sentAt: '2026-10-24T19:30:01Z',
      by: 'SAM',
      status: 'Sent',
      reprint: false,
      text: '*** VOID ***\n1 x Guiness\nWrong item',
      error: null,
    },
    {
      id: 'j2',
      kind: 'Bill',
      label: 'Corrected bill 2',
      printer: 'Counter printer',
      at: '2026-10-24T19:31:00Z',
      sentAt: null,
      by: 'SAM',
      status: 'Sent',
      reprint: false,
      text: null,
      error: null,
    },
  ],
  payments: [],
  approvals: [],
  movements: [],
  journal: [
    {
      id: 'e1',
      businessDate: '2026-10-24',
      at: '2026-10-24T21:00:00Z',
      memo: 'Receipt 5001 · Ticket #1001',
      lines: [
        { code: 'CASH', account: 'Cash', debit: 520, credit: 0 },
        { code: 'SALES', account: 'Sales', debit: 0, credit: 448.28 },
      ],
    },
  ],
  timeline: [
    {
      at: '2026-10-24T19:00:00Z',
      kind: 'Opened',
      text: 'Opened on Counter A',
      by: 'edna',
      ref: null,
    },
    {
      at: '2026-10-24T19:30:00Z',
      kind: 'Void',
      text: '1 × Guiness voided, KSh 270 · Wrong item · poured',
      by: 'edna, approved by SAM',
      ref: 'l1',
    },
    {
      at: '2026-10-24T21:00:00Z',
      kind: 'Settled',
      text: 'Settled on Counter A, receipt 5001',
      by: 'CAROL',
      ref: null,
    },
  ],
};

describe('Reports', () => {
  const fetchMock = vi.fn<typeof fetch>();
  const calls: { method: string; path: string; search: string; body: unknown; headers: Headers }[] =
    [];
  let reports: ReportDefinition[];
  let extra: Parameters<typeof fakeServer>[0];

  beforeEach(() => {
    vi.stubGlobal('fetch', fetchMock);
    calls.length = 0;
    reports = list;
    extra = {};
    fetchMock.mockImplementation(async (input, init) => {
      const url = new URL(String(input), 'http://localhost');
      const method = (init?.method ?? 'GET').toUpperCase();
      calls.push({
        method,
        path: url.pathname,
        search: url.search,
        body: typeof init?.body === 'string' ? JSON.parse(init.body) : undefined,
        headers: new Headers(init?.headers),
      });
      return fakeServer({
        'GET /api/auth/me': () =>
          json(200, { user: { id: 'a', name: 'Administrator', role: 'Admin' }, permissions: {} }),
        'GET /api/brand': () => json(200, { name: 'Demo Bar', logoFileId: null, currency: 'KES' }),
        'GET /api/admin/reports': () => json(200, reports),
        'GET /api/admin/reports/options': () => json(200, options),
        'POST /api/admin/reports/sales-by-user/run': () => json(200, salesByUser()),
        'POST /api/admin/reports/stock-on-hand/run': () =>
          json(200, emptyResult('stock-on-hand', 'Stock on hand')),
        'POST /api/admin/reports/vat/run': () => json(200, emptyResult('vat', 'VAT summary')),
        'POST /api/admin/reports/tickets/run': () =>
          json(200, emptyResult('tickets', 'Tickets and receipts')),
        'POST /api/admin/reports/sales-by-user/export': () =>
          new Response(new Blob(['xlsx']), {
            status: 200,
            headers: { 'content-disposition': 'attachment; filename=sales-by-user.xlsx' },
          }),
        'POST /api/admin/reports/sales-by-user/print': () => new Response(null, { status: 204 }),
        'GET /api/admin/tickets/k-1001': () => json(200, ticket),
        ...extra,
      })(input, init);
    });
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  const runs = (key: string) =>
    calls.filter((c) => c.method === 'POST' && c.path === `/api/admin/reports/${key}/run`);

  it('shows the first seven reports as tabs and the rest under More reports', async () => {
    renderRoutes(routes, '/admin/reports');
    await screen.findByRole('button', { name: 'Sales by user' });
    const tabs = screen.getByRole('navigation', { name: 'Reports' });
    const buttons = within(tabs)
      .getAllByRole('button')
      .map((b) => b.textContent);
    expect(buttons).toEqual([
      'Sales by user',
      'Sales by item',
      'Payments and cash-ups',
      'Voids, gifts and approvals',
      'Price changes',
      'Stock on hand',
      'Stock variance',
      'More reports',
    ]);
    expect(within(tabs).getByRole('button', { name: 'Sales by user' })).toHaveAttribute(
      'aria-pressed',
      'true',
    );

    await userEvent.click(within(tabs).getByRole('button', { name: 'More reports' }));
    await userEvent.click(screen.getByRole('button', { name: 'VAT summary' }));
    expect(
      await within(tabs).findByRole('button', { name: 'More: VAT summary' }),
    ).toBeInTheDocument();
    await waitFor(() => expect(runs('vat')).toHaveLength(1));
  });

  it('looks like the wireframe: users grouped with their role and total, ticket links, and "n more" that expands', async () => {
    renderRoutes(routes, '/admin/reports');
    const table = await screen.findByRole('table', { name: 'Sales by user' });
    const edna = within(table).getByRole('rowheader', { name: 'edna' }).closest('tr')!;
    expect(edna).toHaveTextContent('Waitress');
    expect(edna).toHaveTextContent('13,500');
    expect(within(table).getByText(longName)).toBeInTheDocument();
    expect(within(table).getByRole('link', { name: '#1001' })).toHaveAttribute(
      'href',
      '/admin/tickets/k-1001',
    );
    expect(within(table).queryByText('Tusker Lager 53')).not.toBeInTheDocument();
    expect(screen.getByText('Includes 2 open tickets')).toBeInTheDocument();
    expect(
      screen.getByText(/Sat 24 Oct 2026 \(open\) · Sales credited to who added the item/),
    ).toBeInTheDocument();

    await userEvent.click(within(table).getByRole('button', { name: '… 4 more items' }));
    expect(within(table).getByText('Tusker Lager 53')).toBeInTheDocument();
    expect(within(table).getByText('Tickets 54 · Gifts 0 (KSh 0)')).toBeInTheDocument();
  });

  it('shows the filters each report takes and reruns with them', async () => {
    renderRoutes(routes, '/admin/reports');
    await screen.findByRole('table', { name: 'Sales by user' });
    expect(screen.getByLabelText('Work period')).toBeInTheDocument();
    expect(screen.getByLabelText('Credit sales to')).toHaveValue('AddedBy');
    expect(screen.getByLabelText('Include open tickets')).toBeChecked();

    await userEvent.selectOptions(screen.getByLabelText('Credit sales to'), 'OpenedBy');
    await userEvent.selectOptions(screen.getByLabelText('User'), 'u-edna');
    await waitFor(() =>
      expect(runs('sales-by-user').at(-1)?.body).toEqual({
        creditTo: 'OpenedBy',
        userId: 'u-edna',
      }),
    );
    expect(runs('sales-by-user').at(-1)?.headers.get('Idempotency-Key')).toBeNull();

    await userEvent.selectOptions(screen.getByLabelText('Work period'), 'range');
    await userEvent.selectOptions(screen.getByLabelText('Dates'), 'LastWeek');
    await waitFor(() =>
      expect(runs('sales-by-user').at(-1)?.body).toMatchObject({ quick: 'LastWeek' }),
    );

    await userEvent.click(screen.getByRole('button', { name: 'Stock on hand' }));
    expect(await screen.findByLabelText('As at')).toBeInTheDocument();
    expect(screen.queryByLabelText('Work period')).not.toBeInTheDocument();
    expect(screen.getByLabelText('Only below the reorder level')).toBeInTheDocument();
    expect(screen.queryByLabelText('Credit sales to')).not.toBeInTheDocument();
  });

  it('applies text and amount filters on Enter, not on every key', async () => {
    renderRoutes(routes, '/admin/reports?report=tickets');
    await waitFor(() => expect(runs('tickets')).toHaveLength(1));
    await userEvent.type(screen.getByLabelText('Amount from'), '500');
    expect(runs('tickets')).toHaveLength(1);
    await userEvent.type(screen.getByLabelText('Amount from'), '{Enter}');
    await waitFor(() => expect(runs('tickets').at(-1)?.body).toEqual({ minAmount: 500 }));
    await userEvent.click(screen.getByLabelText('Has voids'));
    await waitFor(() =>
      expect(runs('tickets').at(-1)?.body).toEqual({ minAmount: 500, hasVoids: true }),
    );
  });

  it('shows the empty state', async () => {
    renderRoutes(routes, '/admin/reports?report=stock-on-hand');
    expect(await screen.findByText('Nothing to show for these filters.')).toBeInTheDocument();
  });

  it('exports to Excel and prints on a receipt printer', async () => {
    renderRoutes(routes, '/admin/reports');
    await screen.findByRole('table', { name: 'Sales by user' });
    await userEvent.click(screen.getByRole('button', { name: 'Export to Excel' }));
    await waitFor(() =>
      expect(calls.some((c) => c.path === '/api/admin/reports/sales-by-user/export')).toBe(true),
    );
    const exported = calls.find((c) => c.path === '/api/admin/reports/sales-by-user/export')!;
    expect(exported.search).toBe('?format=xlsx');
    expect(exported.headers.get('Idempotency-Key')).toBeNull();

    await userEvent.click(screen.getByRole('button', { name: 'Print' }));
    expect(screen.getByRole('button', { name: 'A4 page (PDF)' })).toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: 'Receipt printer' }));
    const dialog = await screen.findByRole('dialog', { name: 'Receipt printer' });
    await userEvent.click(within(dialog).getByRole('button', { name: 'Print' }));
    await waitFor(() =>
      expect(calls.some((c) => c.path === '/api/admin/reports/sales-by-user/print')).toBe(true),
    );
    const printed = calls.find((c) => c.path === '/api/admin/reports/sales-by-user/print')!;
    expect(printed.search).toBe('?target=printer&printerId=p-1');
    expect(printed.headers.get('Idempotency-Key')).not.toBeNull();
    expect(await screen.findByText('Sales by user is printing.')).toBeInTheDocument();
  });

  it('says so when the user may run no reports', async () => {
    reports = [];
    renderRoutes(routes, '/admin/reports');
    expect(await screen.findByRole('alert')).toHaveTextContent(
      'You need reports.all to see reports.',
    );
  });

  it('opens the ticket detail from a ticket number with its timeline and slips', async () => {
    renderRoutes(routes, '/admin/reports');
    const table = await screen.findByRole('table', { name: 'Sales by user' });
    await userEvent.click(within(table).getByRole('link', { name: '#1001' }));

    expect(await screen.findByRole('heading', { name: 'Ticket #1001' })).toBeInTheDocument();
    const timeline = screen.getByRole('table', { name: 'Timeline' });
    expect(
      within(timeline)
        .getAllByRole('row')
        .map((r) => r.textContent),
    ).toEqual([
      expect.stringContaining('Time'),
      expect.stringContaining('Opened on Counter A'),
      expect.stringContaining('1 × Guiness voided'),
      expect.stringContaining('Settled on Counter A'),
    ]);
    const lines = screen.getByRole('table', { name: 'Lines' });
    expect(within(lines).getByText('Voided')).toBeInTheDocument();
    expect(within(lines).getByText('Voided: Wrong item (SAM)')).toBeInTheDocument();

    const prints = screen.getByRole('table', { name: 'Prints' });
    expect(within(prints).getByText('Slip text no longer kept')).toBeInTheDocument();
    await userEvent.click(within(prints).getByRole('button', { name: 'View slip' }));
    const slip = await screen.findByRole('dialog', { name: 'VOID · Guiness' });
    expect(slip.querySelector('pre')).toHaveTextContent('*** VOID *** 1 x Guiness Wrong item');

    await userEvent.click(within(slip).getByRole('button', { name: 'Close' }));
    expect(screen.getByRole('link', { name: 'Cash' })).toHaveAttribute(
      'href',
      '/admin/accounts?account=CASH&date=2026-10-24',
    );
  });
});
