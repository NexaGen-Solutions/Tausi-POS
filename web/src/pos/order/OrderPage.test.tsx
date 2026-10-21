import { act, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { routes } from '../../app/routes';
import { updateConnection } from '../../shared/connection/status';
import { setDeviceForTests } from '../../shared/device/device';
import { setPinSession } from '../../shared/session/session';
import { deviceInfo, fakeServer, json, renderRoutes, sessionFor } from '../../test/render';
import type { OrderLine, TicketDetail } from '../tables/types';
import type { PosMenu } from './menu';

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

const menu: PosMenu = {
  menuVersion: '1',
  priceListName: 'Standard',
  categories: [
    {
      id: 'c-beers',
      name: 'Beers',
      items: [
        {
          id: 'm-tusker',
          name: 'Tusker Lager',
          shortName: 'Tusker',
          price: 250,
          vatRate: 16,
          groups: [
            {
              id: 'g-temp',
              name: 'Temperature',
              minSelect: 1,
              maxSelect: 1,
              options: [
                { id: 'o-cold', name: 'Cold', priceAddOn: 0, isDefault: true },
                { id: 'o-warm', name: 'Warm', priceAddOn: 0, isDefault: false },
              ],
            },
          ],
        },
      ],
    },
    {
      id: 'c-spirits',
      name: 'Spirits',
      items: [
        {
          id: 'm-whisky',
          name: 'Whisky',
          shortName: 'Whisky',
          price: 300,
          vatRate: 16,
          groups: [
            {
              id: 'g-mixer',
              name: 'Mixer',
              minSelect: 1,
              maxSelect: 1,
              options: [
                { id: 'o-soda', name: 'Soda', priceAddOn: 50, isDefault: false },
                { id: 'o-neat', name: 'Neat', priceAddOn: 0, isDefault: false },
              ],
            },
          ],
        },
      ],
    },
    {
      id: 'c-soft',
      name: 'Soft drinks',
      items: [
        {
          id: 'm-soda',
          name: 'Soda 300ml',
          shortName: 'Soda',
          price: 100,
          vatRate: 16,
          groups: [],
        },
      ],
    },
  ],
};

const names: Record<string, string> = {
  'm-tusker': 'Tusker Lager',
  'm-soda': 'Soda 300ml',
  'm-whisky': 'Whisky',
};
const prices: Record<string, number> = { 'm-tusker': 250, 'm-soda': 100, 'm-whisky': 300 };

function line(id: string, extra: Partial<OrderLine> = {}): OrderLine {
  return {
    id,
    menuItemId: 'm-soda',
    name: 'Soda 300ml',
    shortName: 'Soda',
    options: null,
    note: null,
    quantity: 1,
    unitPrice: 100,
    gross: 100,
    status: 'New',
    printRound: null,
    addedById: 'id-edna',
    addedBy: 'edna',
    addedOn: 'Counter A',
    addedAt: '2026-10-20T19:42:00Z',
    printedAt: null,
    voided: false,
    ...extra,
  };
}

function ticketWith(lines: OrderLine[], extra: Partial<TicketDetail> = {}): TicketDetail {
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
    total: lines.reduce((s, l) => s + l.gross, 0),
    lines,
    lock: null,
    rowVersion: 1,
    billNumber: 0,
    newLines: lines.filter((l) => l.status === 'New').length,
    failedJobs: [],
    ...extra,
  };
}

describe('order screen', () => {
  const fetchMock = vi.fn<typeof fetch>();
  const calls: { method: string; path: string; body: unknown }[] = [];
  let ticket: TicketDetail;
  let permissions: Record<string, string>;
  let printMode: 'OneButton' | 'SendAndBill';
  let editable: boolean;
  let extra: Parameters<typeof fakeServer>[0];
  /** Lets a test hold the next line add until it says so. */
  let gate: Promise<void> | null;
  let inFlight: number;
  let maxInFlight: number;

  const detail = () => {
    ticket = {
      ...ticket,
      total: ticket.lines.reduce((s, l) => s + l.gross, 0),
      newLines: ticket.lines.filter((l) => l.status === 'New').length,
    };
    return json(200, ticket);
  };

  const addLine = (body: { menuItemId: string; optionIds: string[]; quantity: number }) => {
    const options =
      body.optionIds
        .map(
          (id) => ({ 'o-cold': 'Cold', 'o-warm': 'Warm', 'o-soda': 'Soda', 'o-neat': 'Neat' })[id],
        )
        .join(', ') || null;
    const unitPrice = prices[body.menuItemId] + (body.optionIds.includes('o-soda') ? 50 : 0);
    const same = ticket.lines.find(
      (l) => l.status === 'New' && l.menuItemId === body.menuItemId && l.options === options,
    );
    if (same) {
      ticket = {
        ...ticket,
        lines: ticket.lines.map((l) =>
          l === same
            ? {
                ...l,
                quantity: l.quantity + body.quantity,
                gross: (l.quantity + body.quantity) * unitPrice,
              }
            : l,
        ),
      };
    } else {
      ticket = {
        ...ticket,
        lines: [
          ...ticket.lines,
          line(`l${ticket.lines.length + 1}`, {
            menuItemId: body.menuItemId,
            name: names[body.menuItemId],
            options,
            quantity: body.quantity,
            unitPrice,
            gross: unitPrice * body.quantity,
          }),
        ],
      };
    }
  };

  const serve = () =>
    fetchMock.mockImplementation(async (input, init) => {
      const url = new URL(String(input), 'http://localhost');
      const method = (init?.method ?? 'GET').toUpperCase();
      const body = typeof init?.body === 'string' ? JSON.parse(init.body) : undefined;
      calls.push({ method, path: url.pathname, body });
      return fakeServer({
        'GET /api/device': () =>
          json(200, {
            ...deviceInfo({ openedAt: '2026-10-20T08:14:00' }),
            printing: {
              mode: printMode,
              receiptPrinter: 'Ticket Printer 1',
              dispensePrinter: 'Bar Printer',
              dispenseName: 'Dispense A',
            },
          }),
        'GET /api/brand': () => json(200, { name: "Mau's Village", currency: 'KES' }),
        'GET /api/auth/me': () =>
          json(200, { user: { id: 'id-edna', name: 'edna', role: 'Waiter' }, permissions }),
        'GET /api/pos/menu': () => json(200, menu),
        'GET /api/pos/tables': () =>
          json(200, {
            version: 1,
            serverTime: new Date().toISOString(),
            dayOpen: true,
            tableIdleMinutes: 30,
            areas: [],
            tables: [],
          }),
        'GET /api/pos/tickets/takeaway': () => json(200, []),
        'GET /api/pos/tickets/k1': () => detail(),
        'POST /api/pos/tickets/k1/lock': () =>
          json(
            200,
            editable
              ? { editable: true, heldBy: null, message: null }
              : {
                  editable: false,
                  heldBy: {
                    terminalId: 't2',
                    terminal: 'Pillar 1',
                    userId: 'u-ruth',
                    user: 'Ruth',
                  },
                  message: 'Open on Pillar 1 by Ruth. You can view it but not change it.',
                },
          ),
        'DELETE /api/pos/tickets/k1/lock': () => new Response(null, { status: 204 }),
        'POST /api/pos/tickets': async ({ body: b }) => {
          const request = b as {
            lines: { menuItemId: string; optionIds: string[]; quantity: number }[];
          };
          ticket = ticketWith([]);
          request.lines.forEach(addLine);
          return detail();
        },
        'POST /api/pos/tickets/k1/lines': async ({ body: b }) => {
          inFlight++;
          maxInFlight = Math.max(maxInFlight, inFlight);
          if (gate) await gate;
          await new Promise((r) => setTimeout(r, 1));
          inFlight--;
          addLine(b as { menuItemId: string; optionIds: string[]; quantity: number });
          return detail();
        },
        'POST /api/pos/tickets/k1/print': () => {
          ticket = {
            ...ticket,
            billNumber: ticket.billNumber + 1,
            lines: ticket.lines.map((l) => ({ ...l, status: 'Printed' as const })),
          };
          return detail();
        },
        'POST /api/pos/tickets/k1/send': () => {
          ticket = {
            ...ticket,
            lines: ticket.lines.map((l) => ({ ...l, status: 'Printed' as const })),
          };
          return detail();
        },
        'POST /api/pos/tickets/k1/reprint-bill': () => {
          ticket = { ...ticket, billNumber: ticket.billNumber + 1 };
          return detail();
        },
        'POST /api/pos/tickets/k1/print-jobs/j1/retry': () => {
          ticket = { ...ticket, failedJobs: [] };
          return json(200, {});
        },
        ...extra,
      })(input, init);
    });

  beforeEach(() => {
    vi.stubGlobal('fetch', fetchMock);
    setDeviceForTests({ deviceToken: 'device-1' });
    setPinSession(sessionFor('edna', 'Waiter'));
    ticket = ticketWith([line('l1', { quantity: 2, gross: 200 })]);
    permissions = {
      'order.add': 'Allowed',
      'order.cancel_unprinted': 'Allowed',
      'ticket.print': 'Allowed',
      'ticket.reprint': 'NeedsApproval',
      'ticket.move': 'Allowed',
    };
    printMode = 'OneButton';
    editable = true;
    extra = {};
    gate = null;
    inFlight = 0;
    maxInFlight = 0;
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

  const writes = (suffix: string) =>
    calls.filter((c) => c.method !== 'GET' && c.path.endsWith(suffix));
  const lineButton = (name: RegExp) =>
    within(screen.getByRole('region', { name: 'Ticket' })).getByRole('button', { name });

  async function open(url = '/pos/ticket/k1?table=t-7') {
    const router = renderRoutes(routes, url);
    await screen.findByRole('button', { name: /Soda 300ml/ });
    return router;
  }

  async function openSoftDrinks(user: ReturnType<typeof userEvent.setup>) {
    await user.click(await screen.findByRole('button', { name: 'Soft drinks' }));
  }

  it('shows the header, the lines with who added them, the total and Print (n new)', async () => {
    await open();

    expect(screen.getByText('Table 7 · Ticket #1042')).toBeInTheDocument();
    expect(screen.getByText('Counter A · edna (Waiter)')).toBeInTheDocument();
    const soda = lineButton(/Soda 300ml/);
    expect(soda).toHaveTextContent('edna');
    expect(soda).toHaveTextContent('New');
    expect(screen.getByText('KSh 200')).toBeInTheDocument();
    expect(await screen.findByRole('button', { name: 'Print (1 new)' })).toBeEnabled();
    expect(
      screen.getByText(/then a slip with only the new items to Dispense A/),
    ).toBeInTheDocument();
  });

  it('adds an item straight away and turns it solid when the server confirms', async () => {
    const user = userEvent.setup();
    let release!: () => void;
    gate = new Promise((r) => (release = r));
    ticket = ticketWith([]);
    const router = renderRoutes(routes, '/pos/ticket/k1?table=t-7');
    await openSoftDrinks(user);
    await user.click(await screen.findByRole('button', { name: /^Soda 300ml\s*KSh 100/ }));

    const pending = lineButton(/Soda 300ml/);
    expect(pending).toHaveTextContent('Saving');
    expect(pending).toBeDisabled();

    release();
    await waitFor(() => expect(lineButton(/Soda 300ml/)).toHaveTextContent('New'));
    expect(lineButton(/Soda 300ml/)).toBeEnabled();
    expect(writes('/lines')[0].body).toEqual({ menuItemId: 'm-soda', optionIds: [], quantity: 1 });
    expect(router.state.location.pathname).toBe('/pos/ticket/k1');
  });

  it('removes a refused add and says why', async () => {
    const user = userEvent.setup();
    ticket = ticketWith([]);
    extra = {
      'POST /api/pos/tickets/k1/lines': () =>
        json(409, { status: 409, detail: 'Soda 300ml is sold out.' }),
    };
    renderRoutes(routes, '/pos/ticket/k1?table=t-7');
    await openSoftDrinks(user);
    await user.click(await screen.findByRole('button', { name: /^Soda 300ml\s*KSh 100/ }));

    expect(await screen.findByText('Soda 300ml is sold out.')).toBeInTheDocument();
    await waitFor(() =>
      expect(
        within(screen.getByRole('region', { name: 'Ticket' })).queryByRole('button', {
          name: /Soda 300ml/,
        }),
      ).not.toBeInTheDocument(),
    );
  });

  it('10 fast taps are sent one after the other and make a quantity of 10', async () => {
    const user = userEvent.setup();
    ticket = ticketWith([]);
    renderRoutes(routes, '/pos/ticket/k1?table=t-7');
    await openSoftDrinks(user);
    const soda = await screen.findByRole('button', { name: /^Soda 300ml\s*KSh 100/ });
    for (let i = 0; i < 10; i++) await user.click(soda);

    await waitFor(() => expect(writes('/lines')).toHaveLength(10));
    await waitFor(() => expect(lineButton(/Soda 300ml/)).not.toHaveTextContent('Saving'));
    expect(maxInFlight).toBe(1);
    expect(ticket.lines).toHaveLength(1);
    expect(ticket.lines[0].quantity).toBe(10);
    expect(lineButton(/Soda 300ml/)).toHaveTextContent(/^10/);
  });

  it('the first add on a new ticket saves it and moves to its address', async () => {
    const user = userEvent.setup();
    const router = renderRoutes(routes, '/pos/ticket/new?table=t-7');
    expect(await screen.findByText(/New ticket/)).toBeInTheDocument();
    await openSoftDrinks(user);
    await user.click(await screen.findByRole('button', { name: /^Soda 300ml\s*KSh 100/ }));

    await waitFor(() => expect(router.state.location.pathname).toBe('/pos/ticket/k1'));
    expect(writes('/api/pos/tickets')[0].body).toEqual({
      tableId: 't-7',
      customerName: null,
      lines: [{ menuItemId: 'm-soda', optionIds: [], quantity: 1 }],
    });
    expect(await screen.findByText('Table 7 · Ticket #1042')).toBeInTheDocument();
  });

  it('a beer opens the Cold / Warm pop-up with Cold chosen, and adds the chosen quantity', async () => {
    const user = userEvent.setup();
    await open();
    await user.click(screen.getByRole('button', { name: 'Beers' }));
    await user.click(screen.getByRole('button', { name: /Tusker Lager/ }));

    const dialog = await screen.findByRole('dialog');
    expect(within(dialog).getByText(/Temperature · choose 1 \(required\)/)).toBeInTheDocument();
    expect(within(dialog).getByRole('button', { name: 'Cold' })).toHaveAttribute(
      'aria-pressed',
      'true',
    );
    await user.click(within(dialog).getByRole('button', { name: 'One more' }));
    expect(
      within(dialog).getByRole('button', { name: 'Add 2 × Tusker (Cold) · KSh 500' }),
    ).toBeEnabled();

    await user.click(within(dialog).getByRole('button', { name: 'Warm' }));
    await user.click(
      within(dialog).getByRole('button', { name: 'Add 2 × Tusker (Warm) · KSh 500' }),
    );

    await waitFor(() => expect(writes('/lines')).toHaveLength(1));
    expect(writes('/lines')[0].body).toEqual({
      menuItemId: 'm-tusker',
      optionIds: ['o-warm'],
      quantity: 2,
    });
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
  });

  it('the pop-up keeps Add disabled until a required choice without a default is made', async () => {
    const user = userEvent.setup();
    await open();
    await user.click(screen.getByRole('button', { name: 'Spirits' }));
    await user.click(screen.getByRole('button', { name: /Whisky/ }));

    const dialog = await screen.findByRole('dialog');
    expect(within(dialog).getByRole('button', { name: /^Add 1 × Whisky/ })).toBeDisabled();
    await user.click(within(dialog).getByRole('button', { name: /Soda/ }));
    expect(
      within(dialog).getByRole('button', { name: 'Add 1 × Whisky (Soda) · KSh 350' }),
    ).toBeEnabled();

    await user.click(within(dialog).getByRole('button', { name: 'Cancel' }));
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
    expect(writes('/lines')).toHaveLength(0);
  });

  it('a New line has −, + and Remove; a Printed line has Void and Gift, disabled for now', async () => {
    const user = userEvent.setup();
    ticket = ticketWith([
      line('l1', { quantity: 2, gross: 200 }),
      line('l2', { name: 'Guiness', menuItemId: 'm-g', status: 'Printed', printRound: 1 }),
    ]);
    extra = {
      'PATCH /api/pos/tickets/k1/lines/l1': ({ body }) => {
        const quantity = (body as { quantity: number }).quantity;
        ticket = {
          ...ticket,
          lines: ticket.lines.map((l) =>
            l.id === 'l1' ? { ...l, quantity, gross: quantity * 100 } : l,
          ),
        };
        return detail();
      },
      'DELETE /api/pos/tickets/k1/lines/l1': () => {
        ticket = { ...ticket, lines: ticket.lines.filter((l) => l.id !== 'l1') };
        return detail();
      },
    };
    await open();

    await user.click(lineButton(/Soda 300ml/));
    expect(lineButton(/Soda 300ml/)).toHaveAttribute('aria-pressed', 'true');
    expect(screen.getByText('Selected: Soda 300ml (new)')).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'One more' }));
    await waitFor(() => expect(writes('/lines/l1')[0]?.body).toEqual({ quantity: 3 }));
    await waitFor(() => expect(lineButton(/Soda 300ml/)).toHaveTextContent(/^3/));

    await user.click(screen.getByRole('button', { name: 'Remove' }));
    await waitFor(() => expect(writes('/lines/l1').at(-1)?.method).toBe('DELETE'));
    await waitFor(() => expect(screen.queryByText(/Selected: Soda/)).not.toBeInTheDocument());

    await user.click(lineButton(/Guiness/));
    expect(screen.getByRole('button', { name: 'Void' })).toBeDisabled();
    expect(screen.getByRole('button', { name: 'Void' })).toHaveAttribute(
      'title',
      'Comes with voids and approvals',
    );
    expect(screen.getByRole('button', { name: 'Gift' })).toBeDisabled();
  });

  it('Print sends the new lines; with nothing new it reads Print and reprints the bill', async () => {
    const user = userEvent.setup();
    await open();

    await user.click(await screen.findByRole('button', { name: 'Print (1 new)' }));
    await waitFor(() => expect(writes('/print')).toHaveLength(1));
    expect(await screen.findByRole('button', { name: 'Print' })).toBeEnabled();
    expect(lineButton(/Soda 300ml/)).toHaveTextContent('Printed');

    await user.click(screen.getByRole('button', { name: 'Print' }));
    await waitFor(() => expect(writes('/reprint-bill')).toHaveLength(1));
    expect(writes('/print')).toHaveLength(1);
  });

  it('without ticket.reprint, Reprint says it needs a supervisor and is disabled', async () => {
    permissions['ticket.reprint'] = 'NotAllowed';
    ticket = ticketWith([line('l1', { status: 'Printed' })], { billNumber: 1 });
    await open();

    const buttons = await screen.findAllByRole('button', { name: 'Reprint needs a supervisor' });
    expect(buttons.length).toBeGreaterThan(0);
    buttons.forEach((b) => expect(b).toBeDisabled());
  });

  it('in Send and bill mode the buttons are Send (n new) and Print bill', async () => {
    const user = userEvent.setup();
    printMode = 'SendAndBill';
    extra = {
      'POST /api/pos/tickets/k1/print-bill': () => {
        ticket = { ...ticket, billNumber: ticket.billNumber + 1 };
        return detail();
      },
    };
    await open();

    await user.click(await screen.findByRole('button', { name: 'Send (1 new)' }));
    await waitFor(() => expect(writes('/send')).toHaveLength(1));
    await user.click(screen.getByRole('button', { name: 'Print bill' }));
    await waitFor(() => expect(writes('/print-bill')).toHaveLength(1));
    expect(writes('/print')).toHaveLength(0);
  });

  it('leaving with unprinted lines asks: Stay, Leave them unprinted, or Print now', async () => {
    const user = userEvent.setup();
    const router = await open();

    await user.click(screen.getByRole('button', { name: 'Tables' }));
    let dialog = await screen.findByRole('dialog', { name: "1 item hasn't been printed." });
    await user.click(within(dialog).getByRole('button', { name: 'Stay' }));
    expect(router.state.location.pathname).toBe('/pos/ticket/k1');

    await user.click(screen.getByRole('button', { name: 'Tables' }));
    dialog = await screen.findByRole('dialog');
    await user.click(within(dialog).getByRole('button', { name: 'Print now' }));
    await waitFor(() => expect(router.state.location.pathname).toBe('/pos/tables'));
    expect(writes('/print')).toHaveLength(1);
  });

  it('Leave them unprinted goes to the tables without printing', async () => {
    const user = userEvent.setup();
    const router = await open();

    await user.click(screen.getByRole('button', { name: 'Tables' }));
    await user.click(
      within(await screen.findByRole('dialog')).getByRole('button', {
        name: 'Leave them unprinted',
      }),
    );
    await waitFor(() => expect(router.state.location.pathname).toBe('/pos/tables'));
    expect(writes('/print')).toHaveLength(0);
  });

  it('Move table goes to the map to choose the new table', async () => {
    const user = userEvent.setup();
    ticket = ticketWith([line('l1', { status: 'Printed' })]);
    const router = await open();

    await user.click(screen.getByRole('button', { name: 'Move table' }));
    await waitFor(() => expect(router.state.location.pathname).toBe('/pos/tables'));
    expect(router.state.location.search).toBe('?move=k1');
  });

  it('shows a failed bill with Retry, live and when reopened', async () => {
    const user = userEvent.setup();
    ticket = ticketWith([line('l1', { status: 'Printed' })], {
      billNumber: 1,
      failedJobs: [{ id: 'j1', kind: 'Bill', printerName: 'Ticket Printer 1', error: 'No answer' }],
    });
    await open();

    expect(await screen.findByText('Bill not printed on Ticket Printer 1')).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Retry' }));
    await waitFor(() => expect(writes('/print-jobs/j1/retry')).toHaveLength(1));
    await waitFor(() =>
      expect(screen.queryByText('Bill not printed on Ticket Printer 1')).not.toBeInTheDocument(),
    );

    ticket = {
      ...ticket,
      failedJobs: [{ id: 'j2', kind: 'Dispense', printerName: 'Bar Printer', error: 'Paper out' }],
    };
    emit('print.failed', {
      printJobId: 'j2',
      printerName: 'Bar Printer',
      kind: 'Dispense',
      ticketId: 'k1',
    });
    expect(await screen.findByText('Dispense slip not printed on Bar Printer')).toBeInTheDocument();
  });

  it('a ticket open on another terminal is read-only', async () => {
    const user = userEvent.setup();
    editable = false;
    await open();

    expect(
      await screen.findByText('Open on Pillar 1 by Ruth. You can view it but not change it.'),
    ).toBeInTheDocument();
    await waitFor(() =>
      expect(screen.getByRole('button', { name: 'Print (1 new)' })).toBeDisabled(),
    );
    await user.click(screen.getByRole('button', { name: 'Soft drinks' }));
    expect(screen.getByRole('button', { name: /^Soda 300ml\s*KSh 100/ })).toBeDisabled();
    await user.click(screen.getByRole('button', { name: 'Tables' }));
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
  });

  it('Settle is for cashiers only', async () => {
    await open();
    expect(await screen.findByRole('button', { name: 'Settle: cashier only' })).toBeDisabled();
  });

  it('a cashier can go to Settle', async () => {
    const user = userEvent.setup();
    permissions['ticket.settle'] = 'Allowed';
    const router = await open();

    await user.click(await screen.findByRole('button', { name: 'Settle' }));
    expect(router.state.location.pathname).toBe('/pos/settle/k1');
  });
});
