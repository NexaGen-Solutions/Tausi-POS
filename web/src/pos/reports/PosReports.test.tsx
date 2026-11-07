import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { routes } from '../../app/routes';
import type { ReportColumn, ReportResult } from '../../admin/reports/types';
import { updateConnection } from '../../shared/connection/status';
import { setDeviceForTests } from '../../shared/device/device';
import { setPinSession } from '../../shared/session/session';
import { deviceInfo, fakeServer, json, renderRoutes, sessionFor } from '../../test/render';

vi.mock('../../shared/connection/hub', () => ({
  startHub: vi.fn(),
  stopHub: vi.fn(async () => {}),
  onPosEvent: vi.fn(() => () => {}),
}));

const money = (key: string, label: string): ReportColumn => ({
  key,
  label,
  type: 'Money',
  rightAligned: true,
});

function byUser(names: string[]): ReportResult {
  return {
    key: 'sales-by-user',
    title: 'Sales by user',
    period: 'Tue 20 Oct 2026',
    filterText: names.length === 1 ? [`User: ${names[0]}`] : [],
    runAt: '2026-10-20T18:00:00Z',
    notes: [],
    isEmpty: false,
    tables: [
      {
        title: null,
        columns: [
          { key: 'ticket', label: 'Ticket', type: 'Ticket', rightAligned: false },
          { key: 'time', label: 'Time', type: 'Time', rightAligned: false },
          { key: 'item', label: 'Item', type: 'Text', rightAligned: false },
          { key: 'qty', label: 'Qty', type: 'Quantity', rightAligned: true },
          money('amount', 'Amount'),
        ],
        groups: names.map((name) => ({
          title: name,
          subtitle: 'Waiter',
          total: 500,
          footer: ['Tickets 1 · Gifts 0 (KSh 0) · Voids 0 (KSh 0) · Discounts KSh 0'],
          subtotal: null,
          rows: [
            {
              cells: [1001, '2026-10-20T17:00:00Z', `Tusker Lager sold by ${name}`, 2, 500],
              ticketId: 'k1',
              muted: false,
            },
          ],
        })),
        totals: {
          cells: ['Total', null, null, 2 * names.length, 500 * names.length],
          ticketId: null,
          muted: false,
        },
        footer: null,
      },
    ],
  };
}

const daily: ReportResult = {
  key: 'daily-sales',
  title: 'Daily sales',
  period: 'Tue 20 Oct 2026',
  filterText: [],
  runAt: '2026-10-20T18:00:00Z',
  notes: [],
  isEmpty: false,
  tables: [
    {
      title: null,
      columns: [
        { key: 'line', label: 'Line', type: 'Text', rightAligned: false },
        { key: 'count', label: 'Count', type: 'Quantity', rightAligned: true },
        money('amount', 'Amount'),
      ],
      groups: [
        {
          title: 'Sales',
          subtitle: null,
          total: 2201.5,
          footer: null,
          subtotal: null,
          rows: [{ cells: ['Gross sales', 10, 2285], ticketId: null, muted: false }],
        },
        {
          title: 'Payments',
          subtitle: null,
          total: 1952,
          footer: null,
          subtotal: null,
          rows: [{ cells: ['Cash', 2, 950], ticketId: null, muted: false }],
        },
        {
          title: 'Tickets',
          subtitle: null,
          total: null,
          footer: null,
          subtotal: null,
          rows: [{ cells: ['Settled tickets', 3, 1951.5], ticketId: null, muted: false }],
        },
      ],
      totals: null,
      footer: null,
    },
  ],
};

describe('reports on the terminal', () => {
  const fetchMock = vi.fn<typeof fetch>();
  const calls: { method: string; path: string; body: unknown; headers: Headers }[] = [];
  let permissions: Record<string, string>;

  beforeEach(() => {
    vi.stubGlobal('fetch', fetchMock);
    setDeviceForTests({ deviceToken: 'device-1' });
    setPinSession(sessionFor('edna', 'Waiter'));
    permissions = { 'reports.own': 'Allowed', 'order.add': 'Allowed' };
    calls.length = 0;
    updateConnection({ apiReachable: true, hub: 'connected' });
    fetchMock.mockImplementation(async (input, init) => {
      const url = new URL(String(input), 'http://localhost');
      const method = (init?.method ?? 'GET').toUpperCase();
      const body = typeof init?.body === 'string' ? JSON.parse(init.body) : undefined;
      calls.push({ method, path: url.pathname, body, headers: new Headers(init?.headers) });
      const own = permissions['reports.all'] !== 'Allowed';
      return fakeServer({
        'GET /api/device': () => json(200, deviceInfo({ openedAt: '2026-10-20T08:00:00' })),
        'GET /api/brand': () => json(200, { name: 'Demo Bar', currency: 'KES' }),
        'GET /api/auth/me': () =>
          json(200, { user: { id: 'id-edna', name: 'edna', role: 'Waiter' }, permissions }),
        'GET /api/pos/tables': () =>
          json(200, {
            version: 1,
            serverTime: new Date().toISOString(),
            dayOpen: true,
            tableIdleMinutes: 30,
            areas: [],
            tables: [],
          }),
        'GET /api/admin/reports/options': () =>
          json(200, {
            periods: [],
            users: [
              { id: 'id-edna', name: 'edna', detail: 'Waiter' },
              { id: 'id-ruth', name: 'Ruth', detail: 'Waiter' },
            ],
            terminals: [{ id: 't1', name: 'Counter A', detail: null }],
            locations: [],
            categories: [],
            suppliers: [],
            tables: [],
            roles: [],
            counts: [],
            stockItems: [],
            printers: [],
          }),
        'POST /api/pos/reports/sales-by-user/run': () =>
          json(200, { ownOnly: own, result: byUser(own ? ['edna'] : ['edna', 'Ruth']) }),
        'POST /api/pos/reports/daily-sales/run': () => json(200, { ownOnly: own, result: daily }),
        'POST /api/pos/reports/daily-sales/slip': () => new Response(null, { status: 204 }),
      })(input, init);
    });
  });

  afterEach(() => {
    fetchMock.mockReset();
    vi.unstubAllGlobals();
    setPinSession(null);
  });

  it('opens from the table map for reports.own and shows only my figures', async () => {
    const user = userEvent.setup();
    renderRoutes(routes, '/pos/tables');
    await user.click(await screen.findByRole('button', { name: 'Reports' }));

    expect(await screen.findByText('Only your own figures')).toBeInTheDocument();
    expect(
      screen.getByRole('button', { name: 'Sales by user', pressed: true }),
    ).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Today', pressed: true })).toBeInTheDocument();
    expect(screen.queryByLabelText('User')).not.toBeInTheDocument();
    const sales = screen.getByText('Sales', { selector: '.pos-reports__figure-label' });
    expect(within(sales.closest('section')!).getByText('500')).toBeInTheDocument();
    const run = calls.find((c) => c.path === '/api/pos/reports/sales-by-user/run')!;
    expect(run.body).toMatchObject({ quick: 'Today', userId: null });
    expect(run.headers.has('Idempotency-Key')).toBe(false);

    // Tap a user to see their items.
    expect(screen.queryByText('Tusker Lager sold by edna')).not.toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: /edna \(Waiter\)/ }));
    expect(screen.getByText('Tusker Lager sold by edna')).toBeInTheDocument();
  });

  it('hides the Reports button without reports.own or reports.all', async () => {
    permissions = { 'order.add': 'Allowed' };
    renderRoutes(routes, '/pos/tables');
    await screen.findByRole('button', { name: 'My sales' });
    await waitFor(() => expect(calls.some((c) => c.path === '/api/auth/me')).toBe(true));
    expect(screen.queryByRole('button', { name: 'Reports' })).not.toBeInTheDocument();
  });

  it('lets reports.all pick a user, and changes dates and reports', async () => {
    permissions = { 'reports.all': 'Allowed', 'order.add': 'Allowed' };
    const user = userEvent.setup();
    renderRoutes(routes, '/pos/reports');
    await screen.findByRole('button', { name: /Ruth \(Waiter\)/ });
    expect(screen.queryByText('Only your own figures')).not.toBeInTheDocument();

    await user.selectOptions(await screen.findByLabelText('User'), 'Ruth');
    await waitFor(() =>
      expect(calls.at(-1)?.body).toMatchObject({ userId: 'id-ruth', quick: 'Today' }),
    );
    await user.click(screen.getByRole('button', { name: 'Yesterday' }));
    await waitFor(() => expect(calls.at(-1)?.body).toMatchObject({ quick: 'Yesterday' }));

    await user.click(screen.getByRole('button', { name: 'Daily sales' }));
    const figures = await screen.findByText('Paid');
    expect(within(figures.closest('section')!).getByText('1,952')).toBeInTheDocument();
  });

  it('picks dates on the touch calendar', async () => {
    const user = userEvent.setup();
    renderRoutes(routes, '/pos/reports');
    await screen.findByText('Only your own figures');
    await user.click(screen.getByRole('button', { name: 'Pick dates' }));
    const dialog = await screen.findByRole('dialog');
    const days = within(dialog).getAllByRole('button', { name: /\d{4}$/ });
    const enabled = days.filter((d) => !(d as HTMLButtonElement).disabled);
    await user.click(enabled[0]);
    await user.click(enabled[1] ?? enabled[0]);
    await user.click(within(dialog).getByRole('button', { name: 'Show these dates' }));
    await waitFor(() => {
      const body = calls.at(-1)?.body as { quick: string | null; from: string; to: string };
      expect(body.quick).toBeNull();
      expect(body.from <= body.to).toBe(true);
    });
  });

  it('prints the report on screen with an Idempotency-Key', async () => {
    const user = userEvent.setup();
    renderRoutes(routes, '/pos/reports');
    await screen.findByText('Only your own figures');
    await user.click(screen.getByRole('button', { name: 'Daily sales' }));
    await screen.findByText('Paid');
    await user.click(screen.getByRole('button', { name: 'Print' }));
    await screen.findByText('The report is printing.');
    const slip = calls.find((c) => c.path === '/api/pos/reports/daily-sales/slip')!;
    expect(slip.method).toBe('POST');
    expect(slip.headers.has('Idempotency-Key')).toBe(true);
    expect(slip.body).toMatchObject({ quick: 'Today' });
  });
});
