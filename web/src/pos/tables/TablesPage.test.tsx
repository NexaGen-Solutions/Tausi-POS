import { act, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { routes } from '../../app/routes';
import { updateConnection } from '../../shared/connection/status';
import { setDeviceForTests } from '../../shared/device/device';
import { setPinSession } from '../../shared/session/session';
import { deviceInfo, fakeServer, json, renderRoutes, sessionFor } from '../../test/render';
import type { TableMap, TableTile, TicketSummary } from './types';

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

const serverTime = new Date().toISOString();
const ago = (minutes: number) => new Date(Date.parse(serverTime) - minutes * 60_000).toISOString();

function ticket(id: string, number: number, extra: Partial<TicketSummary> = {}): TicketSummary {
  return {
    id,
    number,
    tableId: 't-7',
    customerName: null,
    openedById: 'u-edna',
    openedBy: 'edna',
    total: 1370,
    openedAt: ago(40),
    lastActivityAt: ago(5),
    mine: false,
    lock: null,
    newLines: 0,
    ...extra,
  };
}

function tile(name: string, areaId: string, tickets: TicketSummary[] = []): TableTile {
  return { id: `t-${name}`, name, areaId, sortOrder: 0, tickets, idleMinutes: null };
}

function mapWith(tables: TableTile[], extra: Partial<TableMap> = {}): TableMap {
  return {
    version: 10,
    serverTime,
    dayOpen: true,
    tableIdleMinutes: 30,
    areas: [
      { id: 'main', name: 'Main', sortOrder: 10 },
      { id: 'pool', name: 'Pool', sortOrder: 20 },
    ],
    tables,
    ...extra,
  };
}

const ruthsLock = { terminalId: 'term-b', terminal: 'Pillar 1', userId: 'u-ruth', user: 'Ruth' };

function standardTables(): TableTile[] {
  return [
    tile('4', 'main'),
    tile('5', 'main', [
      ticket('k5', 1030, {
        tableId: 't-5',
        openedBy: 'Ruth',
        lastActivityAt: ago(42),
        total: 1230,
      }),
    ]),
    tile('7', 'main', [
      ticket('k1', 1042, { mine: true }),
      ticket('k2', 1047, { openedAt: ago(10), total: 540 }),
    ]),
    tile('8', 'main', [
      ticket('k8', 1050, { tableId: 't-8', openedBy: 'WAMBOO', total: 600, lock: ruthsLock }),
    ]),
    tile('Pool 1', 'pool'),
  ];
}

describe('table map', () => {
  const fetchMock = vi.fn<typeof fetch>();
  const calls: { method: string; path: string; body: unknown; headers: Headers }[] = [];
  let map: TableMap;
  let permissions: Record<string, string>;
  let extra: Parameters<typeof fakeServer>[0];

  const serve = () =>
    fetchMock.mockImplementation(async (input, init) => {
      const url = new URL(String(input), 'http://localhost');
      calls.push({
        method: (init?.method ?? 'GET').toUpperCase(),
        path: url.pathname + url.search,
        body: typeof init?.body === 'string' ? JSON.parse(init.body) : undefined,
        headers: new Headers(init?.headers),
      });
      if (url.pathname === '/api/pos/tables' && url.searchParams.get('tableIds')) {
        const ids = url.searchParams.get('tableIds')!.split(',');
        return json(200, { ...map, tables: map.tables.filter((t) => ids.includes(t.id)) });
      }
      return fakeServer({
        'GET /api/device': () => json(200, deviceInfo({ openedAt: '2026-10-20T08:14:00' })),
        'GET /api/brand': () => json(200, { name: "Demo Bar", currency: 'KES' }),
        'GET /api/auth/me': () =>
          json(200, { user: { id: 'u-edna', name: 'edna', role: 'Waiter' }, permissions }),
        'GET /api/pos/tables': () => json(200, map),
        'GET /api/pos/tickets/takeaway': () => json(200, []),
        ...extra,
      })(input, init);
    });

  beforeEach(() => {
    vi.stubGlobal('fetch', fetchMock);
    setDeviceForTests({ deviceToken: 'device-1' });
    setPinSession(sessionFor('edna', 'Waiter'));
    map = mapWith(standardTables());
    permissions = { 'order.add': 'Allowed', 'ticket.move': 'Allowed', 'ticket.merge': 'Allowed' };
    extra = {};
    calls.length = 0;
    hub.clear();
    updateConnection({ apiReachable: true, hub: 'connected' });
    serve();
  });

  afterEach(() => {
    fetchMock.mockReset();
    vi.unstubAllGlobals();
    setPinSession(null);
  });

  const tileFor = (name: string) => screen.getByRole('button', { name: new RegExp(`^${name}: `) });

  async function open() {
    const router = renderRoutes(routes, '/pos/tables');
    await screen.findByRole('button', { name: /^7: / });
    return router;
  }

  it('groups tables by area and draws free, occupied, idle and locked tiles', async () => {
    await open();

    expect(screen.getByRole('heading', { name: 'Main' })).toBeInTheDocument();
    expect(screen.getByRole('heading', { name: 'Pool' })).toBeInTheDocument();
    expect(tileFor('4')).toHaveClass('map-tile--free');
    expect(tileFor('4')).toHaveTextContent('Free');
    expect(tileFor('7')).toHaveClass('map-tile--occupied');
    expect(tileFor('7')).toHaveTextContent('1,910');
    expect(tileFor('7')).toHaveTextContent('edna · 2 tickets');
    expect(tileFor('5')).toHaveClass('map-tile--idle');
    expect(tileFor('5')).toHaveTextContent('Idle 42 min');
    expect(tileFor('8')).toHaveAccessibleName('8: WAMBOO, 600, Open on another terminal');
    expect(tileFor('8').querySelector('.map-lock')).not.toBeNull();
    expect(screen.getByText('Unpaid, idle 30 min')).toBeInTheDocument();
  });

  it('My tables shows only tables with my tickets or lines', async () => {
    await open();
    await userEvent.click(screen.getByRole('button', { name: 'My tables' }));

    expect(
      screen
        .getAllByRole('button', { name: /^\S+( \d)?: / })
        .map((b) => b.getAttribute('aria-label')),
    ).toEqual(['7: edna · 2 tickets, 1,910']);
  });

  it('one tap selects a table; two taps open a draft on a free table or the only ticket', async () => {
    const router = await open();

    await userEvent.click(tileFor('4'));
    const panel = screen.getByRole('complementary');
    expect(within(panel).getByText('Table 4 · Free')).toBeInTheDocument();
    expect(router.state.location.pathname).toBe('/pos/tables');

    await userEvent.click(tileFor('4'));
    expect(router.state.location.pathname + router.state.location.search).toBe(
      '/pos/ticket/new?table=t-4',
    );
  });

  it('two taps on a table with one ticket open it; with several they only select', async () => {
    map = mapWith([
      tile('9', 'main', [ticket('k9', 1060, { tableId: 't-9' })]),
      ...standardTables(),
    ]);
    const router = await open();

    await userEvent.dblClick(tileFor('7'));
    expect(router.state.location.pathname).toBe('/pos/tables');
    expect(
      within(screen.getByRole('complementary')).getByText('2 open tickets'),
    ).toBeInTheDocument();

    await userEvent.dblClick(tileFor('9'));
    expect(router.state.location.pathname).toBe('/pos/ticket/k9');
  });

  it('merges the picked tickets into the oldest, asking a supervisor when it needs approval', async () => {
    permissions['ticket.merge'] = 'NeedsApproval';
    let asked = false;
    extra = {
      'POST /api/pos/tickets/merge': ({ headers }) => {
        if (!headers.get('X-Approval-Token')) {
          asked = true;
          return json(403, {
            status: 403,
            title: 'Approval required',
            approvalRequired: true,
            permission: 'ticket.merge',
          });
        }
        return json(200, { ...ticket('k1', 1042), total: 1910 });
      },
      'POST /api/auth/approve': () =>
        json(200, { approvalToken: 'token-1', approvedByName: 'SAM' }),
    };
    await open();

    await userEvent.click(tileFor('7'));
    await userEvent.click(screen.getByRole('button', { name: 'Merge tickets' }));
    const dialog = screen.getByRole('dialog');
    expect(dialog).toHaveTextContent('Merge #1047 into #1042?');
    await userEvent.click(within(dialog).getByRole('button', { name: 'Merge' }));

    const pad = await screen.findByRole('dialog', { name: 'Supervisor approval' });
    expect(asked).toBe(true);
    await userEvent.keyboard('8014{Enter}');

    await screen.findByText('Merged into #1042.');
    expect(pad).not.toBeInTheDocument();
    const merges = calls.filter((c) => c.path === '/api/pos/tickets/merge');
    expect(merges.at(-1)!.body).toEqual({ ticketIds: ['k2'], intoTicketId: 'k1' });
    expect(merges.at(-1)!.headers.get('X-Approval-Token')).toBe('token-1');
  });

  it('hides merge and move when they are not allowed', async () => {
    permissions['ticket.merge'] = 'NotAllowed';
    permissions['ticket.move'] = 'NotAllowed';
    await open();
    await userEvent.click(tileFor('7'));

    expect(screen.queryByRole('button', { name: 'Merge tickets' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Move to another table' })).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Open ticket' })).toBeEnabled();
  });

  it('moves the chosen ticket to the table tapped next', async () => {
    extra = {
      'POST /api/pos/tickets/k2/move': () => json(200, ticket('k2', 1047, { tableId: 't-4' })),
    };
    await open();

    await userEvent.click(tileFor('7'));
    await userEvent.click(screen.getByRole('button', { name: /#1047/ }));
    await userEvent.click(screen.getByRole('button', { name: 'Move to another table' }));
    expect(screen.getByText('Choose a table for #1047')).toBeInTheDocument();
    expect(tileFor('7')).toBeDisabled();

    await userEvent.click(tileFor('4'));

    await screen.findByText('#1047 moved to Table 4.');
    expect(calls.find((c) => c.path === '/api/pos/tickets/k2/move')!.body).toEqual({
      toTableId: 't-4',
    });
    expect(screen.queryByText('Choose a table for #1047')).not.toBeInTheDocument();
  });

  it('with a supervisor elsewhere, a merge is sent as a request instead', async () => {
    permissions['ticket.merge'] = 'NeedsApproval';
    extra = {
      'POST /api/pos/tickets/merge': () =>
        json(403, { status: 403, approvalRequired: true, permission: 'ticket.merge' }),
      'GET /api/pos/approvals/approvers': () => json(200, []),
      'POST /api/pos/approvals': () =>
        json(200, {
          id: 'r2',
          kind: 'Merge',
          summary: 'Merge #1047 into #1042',
          status: 'Pending',
        }),
    };
    await open();

    await userEvent.click(tileFor('7'));
    await userEvent.click(screen.getByRole('button', { name: 'Merge tickets' }));
    await userEvent.click(
      within(screen.getByRole('dialog')).getByRole('button', { name: 'Merge' }),
    );
    const pad = await screen.findByRole('dialog', { name: 'Supervisor approval' });
    expect(within(pad).getByRole('combobox', { name: 'Send request to' })).toHaveDisplayValue(
      'Anyone who can approve',
    );
    await userEvent.click(within(pad).getByRole('button', { name: 'Send request' }));

    expect(await screen.findByText('Request sent: Merge #1047 into #1042.')).toBeInTheDocument();
    expect(calls.find((c) => c.method === 'POST' && c.path === '/api/pos/approvals')!.body).toEqual(
      {
        kind: 'Merge',
        ticketId: 'k1',
        lineId: null,
        payload: { ticketIds: ['k2'], intoTicketId: 'k1' },
        preferredApproverId: null,
      },
    );
  });

  it('with a supervisor elsewhere, a move is sent as a request instead', async () => {
    permissions['ticket.move'] = 'NeedsApproval';
    extra = {
      'POST /api/pos/tickets/k2/move': () =>
        json(403, { status: 403, approvalRequired: true, permission: 'ticket.move' }),
      'GET /api/pos/approvals/approvers': () =>
        json(200, [{ userId: 'u-sam', name: 'SAM', role: 'Supervisor', terminal: 'Counter B' }]),
      'POST /api/pos/approvals': () =>
        json(200, { id: 'r1', kind: 'Move', summary: 'Move to Table 4', status: 'Pending' }),
    };
    await open();

    await userEvent.click(tileFor('7'));
    await userEvent.click(screen.getByRole('button', { name: /#1047/ }));
    await userEvent.click(screen.getByRole('button', { name: 'Move to another table' }));
    await userEvent.click(tileFor('4'));
    const pad = await screen.findByRole('dialog', { name: 'Supervisor approval' });
    await userEvent.click(within(pad).getByRole('button', { name: 'Send request' }));

    expect(await screen.findByText('Request sent: Move to Table 4.')).toBeInTheDocument();
    expect(calls.find((c) => c.method === 'POST' && c.path === '/api/pos/approvals')!.body).toEqual(
      {
        kind: 'Move',
        ticketId: 'k2',
        lineId: null,
        payload: { toTableId: 't-4' },
        preferredApproverId: 'u-sam',
      },
    );
    expect(screen.queryByText('Choose a table for #1047')).not.toBeInTheDocument();
  });

  it('an incoming ticket.changed refetches and redraws just that table', async () => {
    await open();
    map = mapWith(
      standardTables().map((t) =>
        t.name === '4'
          ? tile('4', 'main', [
              ticket('k4', 1070, { tableId: 't-4', openedBy: 'CYNTHIA', total: 250 }),
            ])
          : t,
      ),
      { version: 11 },
    );
    calls.length = 0;

    emit('ticket.changed', { ticketId: 'k4', tableId: 't-4', status: 'Open', version: 11 });

    await waitFor(() => expect(tileFor('4')).toHaveClass('map-tile--occupied'));
    expect(tileFor('4')).toHaveTextContent('CYNTHIA');
    expect(calls.map((c) => c.path)).toEqual(['/api/pos/tables?tableIds=t-4']);
  });

  it('a gap in versions refetches the whole map', async () => {
    await open();
    calls.length = 0;

    emit('ticket.changed', { ticketId: 'k4', tableId: 't-4', status: 'Open', version: 13 });

    await waitFor(() => expect(calls.map((c) => c.path)).toContain('/api/pos/tables'));
  });

  it('while offline the banner shows and every action is disabled', async () => {
    await open();
    await userEvent.click(tileFor('7'));

    act(() => updateConnection({ hub: 'reconnecting' }));

    expect(screen.getByText('Not connected to the server. Reconnecting…')).toBeInTheDocument();
    expect(tileFor('4')).toBeDisabled();
    expect(screen.getByRole('button', { name: 'Open ticket' })).toBeDisabled();
    expect(screen.getByRole('button', { name: 'Move to another table' })).toBeDisabled();
  });

  it('with no open day the map says so, blocks new tickets and lets a manager open it', async () => {
    map = mapWith(standardTables(), { dayOpen: false });
    permissions['day.open_close'] = 'Allowed';
    extra = { 'POST /api/day/open': () => json(200, { id: 'wp', openedAt: serverTime }) };
    await open();

    expect(screen.getByRole('alert')).toHaveTextContent("The day isn't open yet");
    await userEvent.click(tileFor('4'));
    expect(screen.queryByRole('button', { name: 'New ticket' })).not.toBeInTheDocument();

    map = mapWith(standardTables());
    await userEvent.click(screen.getByRole('button', { name: 'Open day' }));
    await waitFor(() =>
      expect(screen.queryByText("The day isn't open yet")).not.toBeInTheDocument(),
    );
    expect(calls.some((c) => c.method === 'POST' && c.path === '/api/day/open')).toBe(true);
    expect(screen.getByRole('button', { name: 'New ticket' })).toBeEnabled();
  });

  it('a table with lines left unprinted says so on its tile', async () => {
    map = mapWith([tile('7', 'main', [ticket('k1', 1042, { newLines: 2 })])]);
    await open();

    expect(tileFor('7')).toHaveTextContent('2 not printed');
    expect(tileFor('7')).toHaveAccessibleName('7: edna, 1,370, 2 not printed');
  });

  it('comes from Move table on the order screen ready to choose the new table', async () => {
    const user = userEvent.setup();
    const router = renderRoutes(routes, '/pos/tables?move=k5');
    expect(await screen.findByText('Choose a table for #1030')).toBeInTheDocument();
    expect(router.state.location.search).toBe('?move=k5');

    await user.click(screen.getByRole('button', { name: 'Cancel' }));
    expect(screen.queryByText('Choose a table for #1030')).not.toBeInTheDocument();
    expect(router.state.location.search).toBe('');
  });

  it('a part-paid table says how much is left on its tile', async () => {
    map = mapWith([tile('7', 'main', [ticket('k1', 1042, { paid: 1000, left: 370 })])]);
    await open();

    expect(tileFor('7')).toHaveTextContent('Part paid · KSh 370 left');
    expect(tileFor('7')).toHaveAccessibleName('7: edna, 1,370, Part paid · KSh 370 left');
  });

  it("cashiers see today's settled tickets and reprint a receipt with a supervisor's approval", async () => {
    permissions['ticket.settle'] = 'Allowed';
    let token: string | null = null;
    extra = {
      'GET /api/pos/tickets/settled': () =>
        json(200, [
          {
            id: 'k9',
            receiptNumber: 2041,
            number: 1042,
            where: 'Table 7',
            total: 1370,
            paymentTypes: ['Mpesa', 'Cash'],
            cashier: 'CAROL',
            settledAt: ago(5),
          },
        ]),
      'POST /api/pos/tickets/k9/receipt/reprint': ({ headers }) => {
        token = headers.get('X-Approval-Token');
        return token
          ? new Response(null, { status: 204 })
          : json(403, { status: 403, approvalRequired: true, permission: 'ticket.reprint' });
      },
      'POST /api/auth/approve': () => json(200, { approvalToken: 'tok-1', approvedByName: 'SAM' }),
    };
    await open();

    await userEvent.click(screen.getByRole('button', { name: 'Settled' }));
    const row = (await screen.findByText('Receipt 2041')).closest('li')!;
    expect(row).toHaveTextContent('#1042 · Table 7');
    expect(row).toHaveTextContent('KSh 1,370');
    expect(row).toHaveTextContent('M-Pesa + Cash');
    expect(row).toHaveTextContent('CAROL');
    await userEvent.click(within(row).getByRole('button', { name: 'Reprint receipt' }));
    await screen.findByRole('dialog', { name: 'Supervisor approval' });
    await userEvent.keyboard('8014{Enter}');

    expect(await screen.findByText('Receipt 2041 copy printed.')).toBeInTheDocument();
    expect(token).toBe('tok-1');
  });

  it('waiters have no Settled tab', async () => {
    await open();
    expect(screen.queryByRole('button', { name: 'Settled' })).not.toBeInTheDocument();
  });

  it('a waiter without day.open_close sees the banner without the button', async () => {
    map = mapWith(standardTables(), { dayOpen: false });
    await open();

    expect(screen.getByRole('alert')).toHaveTextContent("The day isn't open yet");
    expect(screen.queryByRole('button', { name: 'Open day' })).not.toBeInTheDocument();
  });

  it('the order screen shows a ticket open elsewhere read-only, and takes it over when it is released', async () => {
    let lockedBy: string | null = 'Pillar 1';
    extra = {
      'GET /api/pos/tickets/k8': () =>
        json(200, {
          id: 'k8',
          number: 1050,
          status: 'Open',
          tableId: 't-8',
          tableName: '8',
          customerName: null,
          openedBy: 'WAMBOO',
          openedAt: ago(30),
          lastActivityAt: ago(3),
          note: '',
          total: 600,
          rowVersion: 1,
          lock: null,
          billNumber: 1,
          newLines: 0,
          failedJobs: [],
          lines: [
            {
              id: 'l1',
              menuItemId: 'm-guiness',
              name: 'Guiness',
              shortName: 'Guiness',
              options: null,
              note: null,
              quantity: 2,
              unitPrice: 300,
              gross: 600,
              status: 'Printed',
              printRound: 1,
              addedById: 'u-wamboo',
              addedBy: 'WAMBOO',
              addedOn: 'Counter A',
              addedAt: ago(30),
              printedAt: ago(29),
              voided: false,
            },
          ],
        }),
      'POST /api/pos/tickets/k8/lock': () =>
        json(
          200,
          lockedBy
            ? {
                editable: false,
                heldBy: ruthsLock,
                message: 'Open on Pillar 1 by Ruth. You can view it but not change it.',
              }
            : { editable: true, heldBy: null, message: null },
        ),
      'GET /api/pos/menu': () =>
        json(200, { menuVersion: '1', priceListName: 'Standard', categories: [] }),
    };
    renderRoutes(routes, '/pos/ticket/k8?table=t-8');

    expect(
      await screen.findByText('Open on Pillar 1 by Ruth. You can view it but not change it.'),
    ).toBeInTheDocument();
    expect(screen.getByText('Table 8 · Ticket #1050')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /Guiness/ })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Ticket note' })).toBeDisabled();

    lockedBy = null;
    emit('ticket.lock', { ticketId: 'k8', tableId: 't-8', terminalId: null, version: 12 });

    await waitFor(() =>
      expect(screen.queryByText(/You can view it but not change it/)).not.toBeInTheDocument(),
    );
  });
});
