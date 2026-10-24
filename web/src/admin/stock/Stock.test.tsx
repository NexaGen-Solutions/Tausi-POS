import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { routes } from '../../app/routes';
import { fakeServer, json, renderRoutes } from '../../test/render';
import type {
  CountLine,
  Delivery,
  StockActivity,
  StockCount,
  StockItemRow,
  StockOverview,
} from './types';

vi.mock('../../shared/connection/hub', () => ({
  startHub: vi.fn(),
  stopHub: vi.fn(async () => {}),
  onPosEvent: vi.fn(() => () => {}),
}));

const store = { id: 'l-store', name: 'Store', active: true };
const bar = { id: 'l-bar', name: 'Bar', active: true };

function item(name: string, extra: Partial<StockItemRow> = {}): StockItemRow {
  return {
    id: `i-${name}`,
    name,
    category: 'Beer',
    baseUnit: 'bottle',
    packs: [{ name: 'crate', factor: 25 }],
    active: true,
    onHand: 50,
    onHandText: '2 crates',
    reorderLevel: 24,
    averageCost: 100,
    value: 5000,
    belowReorder: false,
    negative: false,
    hasMovements: true,
    levels: [],
    rowVersion: 1,
    ...extra,
  };
}

const tusker = item('Tusker Lager');
const whiteCap = item('White Cap Lager', {
  onHand: 18,
  onHandText: '18 bottles',
  belowReorder: true,
});
const guiness = item('Guiness', {
  onHand: -3,
  onHandText: '-3 bottles',
  negative: true,
  averageCost: 120,
});
const viceroy = item('Viceroy 375ml', {
  category: 'Spirits',
  baseUnit: 'ml',
  packs: [
    { name: 'bottle', factor: 375 },
    { name: 'carton', factor: 9000 },
  ],
  averageCost: 1.6,
});

function overview(extra: Partial<StockOverview> = {}): StockOverview {
  return {
    locations: [store, bar],
    items: [tusker, whiteCap, guiness, viceroy],
    noRecipe: [{ menuItemId: 'm-pool', name: 'Pool Table' }],
    totalValue: 12345,
    canEdit: true,
    ...extra,
  };
}

const recipe = {
  menuItemId: 'm-tot',
  menuItem: 'Viceroy Tot',
  lines: [
    {
      stockItemId: viceroy.id,
      stockItem: viceroy.name,
      quantity: 25,
      unit: 'ml',
      units: ['ml', 'bottle', 'carton'],
      cost: 40,
    },
  ],
  costPerSale: 40,
  price: 150,
  marginPercent: 69,
  priceWithoutVat: 129.31,
};

describe('Stock', () => {
  const fetchMock = vi.fn<typeof fetch>();
  const calls: { method: string; path: string; search: string; body: unknown }[] = [];
  let extra: Parameters<typeof fakeServer>[0];
  let awaiting: StockActivity[];

  beforeEach(() => {
    vi.stubGlobal('fetch', fetchMock);
    calls.length = 0;
    extra = {};
    awaiting = [];
    fetchMock.mockImplementation(async (input, init) => {
      const url = new URL(String(input), 'http://localhost');
      const method = (init?.method ?? 'GET').toUpperCase();
      calls.push({
        method,
        path: url.pathname,
        search: url.search,
        body: typeof init?.body === 'string' ? JSON.parse(init.body) : undefined,
      });
      return fakeServer({
        'GET /api/auth/me': () =>
          json(200, { user: { id: 'a', name: 'Administrator', role: 'Admin' }, permissions: {} }),
        'GET /api/brand': () =>
          json(200, { name: "Mau's Village", logoFileId: null, currency: 'KES' }),
        'GET /api/admin/stock': () =>
          json(
            200,
            url.searchParams.get('locationId') === bar.id
              ? overview({ items: [whiteCap, guiness] })
              : overview(),
          ),
        'GET /api/admin/stock/awaiting': () => json(200, awaiting),
        'GET /api/admin/stock/recipes': () =>
          json(200, [
            { id: 'm-tot', name: 'Viceroy Tot', category: 'Spirits', hasRecipe: true },
            { id: 'm-pool', name: 'Pool Table', category: 'Pool', hasRecipe: false },
          ]),
        'GET /api/admin/stock/recipes/m-tot': () => json(200, recipe),
        'GET /api/admin/stock/recipes/m-pool': () =>
          json(200, {
            ...recipe,
            menuItemId: 'm-pool',
            menuItem: 'Pool Table',
            lines: [],
            costPerSale: 0,
            price: 40,
            priceWithoutVat: 34.48,
          }),
        [`GET /api/admin/stock/items/${guiness.id}/movements`]: () =>
          json(200, [
            {
              at: '2026-10-24T10:00:00Z',
              kind: 'Sale',
              quantity: -2,
              quantityText: '-2 bottles',
              location: 'Bar',
              user: 'edna',
              reference: '#1042 · Guiness',
              unitCost: 120,
            },
            {
              at: '2026-10-23T10:00:00Z',
              kind: 'Opening',
              quantity: 25,
              quantityText: '1 crate',
              location: 'Bar',
              user: 'Administrator',
              reference: 'Count',
              unitCost: 120,
            },
          ]),
        ...extra,
      })(input, init);
    });
  });

  afterEach(() => {
    fetchMock.mockReset();
    vi.unstubAllGlobals();
  });

  const writes = (path: string) => calls.filter((c) => c.method !== 'GET' && c.path === path);
  const row = (name: string) =>
    screen.getByRole('button', { name: `Movements of ${name}` }).closest('tr')!;

  it('lists stock with orange low and negative rows, filters by location, and shows the no-recipe warning', async () => {
    const user = userEvent.setup();
    renderRoutes(routes, '/admin/stock');
    await screen.findByRole('button', { name: 'Movements of Tusker Lager' });

    expect(row('White Cap Lager')).toHaveClass('stock-row--low');
    expect(row('Guiness')).toHaveClass('stock-row--low');
    expect(row('Tusker Lager')).not.toHaveClass('stock-row--low');
    expect(screen.getByText(/Below zero: check deliveries or the last count/)).toBeInTheDocument();
    expect(
      screen.getByText("1 menu items have no recipe, so they don't reduce stock."),
    ).toBeInTheDocument();
    expect(
      screen.getByText(
        'Orange = below reorder level. Stock goes down automatically with every sale, through the recipe.',
      ),
    ).toBeInTheDocument();

    await user.selectOptions(screen.getByLabelText('Location'), bar.id);
    await waitFor(() =>
      expect(
        screen.queryByRole('button', { name: 'Movements of Tusker Lager' }),
      ).not.toBeInTheDocument(),
    );
    expect(calls.some((c) => c.search === `?locationId=${bar.id}`)).toBe(true);

    await user.click(screen.getByRole('button', { name: 'Show the list' }));
    expect(
      within(screen.getByRole('dialog', { name: 'Menu items without a recipe' })).getByRole(
        'button',
        { name: 'Pool Table' },
      ),
    ).toBeInTheDocument();
  });

  it('opens an item’s movement history', async () => {
    const user = userEvent.setup();
    renderRoutes(routes, '/admin/stock');
    await user.click(await screen.findByRole('button', { name: 'Movements of Guiness' }));

    const dialog = screen.getByRole('dialog', { name: 'Movements · Guiness' });
    expect(await within(dialog).findByText('#1042 · Guiness')).toBeInTheDocument();
    expect(within(dialog).getByText('Opening stock')).toBeInTheDocument();
    expect(within(dialog).getByText('-2 bottles')).toBeInTheDocument();
    await user.type(within(dialog).getByLabelText('From'), '2026-10-24');
    await waitFor(() =>
      expect(
        calls.some((c) => c.path.endsWith('/movements') && c.search === '?from=2026-10-24'),
      ).toBe(true),
    );
  });

  it('the recipe editor offers the item’s units and works out cost, price and margin as it changes', async () => {
    const user = userEvent.setup();
    extra = {
      'PUT /api/admin/stock/recipes/m-tot': ({ body }) =>
        json(200, {
          ...recipe,
          lines: (body as { lines: unknown[] }).lines.map(() => recipe.lines[0]),
        }),
    };
    renderRoutes(routes, '/admin/stock');
    const panel = await screen.findByRole('complementary', { name: 'Recipe' });
    await waitFor(() =>
      expect(within(panel).getByTestId('recipe-sums')).toHaveTextContent(
        'Cost per sale: 40 · Price: 150 · Margin: 69.1%',
      ),
    );
    expect(
      within(panel)
        .getAllByRole('option')
        .map((o) => o.textContent),
    ).toEqual(expect.arrayContaining(['ml of', 'bottle of', 'carton of']));

    await user.clear(within(panel).getByLabelText('Quantity of ingredient 1'));
    await user.type(within(panel).getByLabelText('Quantity of ingredient 1'), '50');
    expect(within(panel).getByTestId('recipe-sums')).toHaveTextContent(
      'Cost per sale: 80 · Price: 150 · Margin: 38.1%',
    );
    await user.click(within(panel).getByRole('button', { name: 'Save recipe' }));
    await waitFor(() => expect(writes('/api/admin/stock/recipes/m-tot')).toHaveLength(1));
    expect(writes('/api/admin/stock/recipes/m-tot')[0].body).toEqual({
      lines: [{ stockItemId: viceroy.id, quantity: 50, unit: 'ml' }],
    });
  });

  it('records waste and a transfer', async () => {
    const user = userEvent.setup();
    const posted = (id: string): StockActivity => ({
      id,
      kind: 'Waste',
      at: '',
      summary: '',
      location: '',
      value: 120,
      status: 'Posted',
      by: 'Administrator',
      approvedBy: null,
      note: null,
      approveKey: '',
    });
    extra = {
      'POST /api/admin/stock/waste': () => json(200, posted('w1')),
      'POST /api/admin/stock/transfers': () => json(200, { ...posted('t1'), status: 'Pending' }),
      'GET /api/admin/printers': () => json(200, []),
    };
    renderRoutes(routes, '/admin/stock');
    await user.click(await screen.findByRole('button', { name: 'Record waste' }));
    let dialog = screen.getByRole('dialog', { name: 'Record waste' });
    await user.selectOptions(within(dialog).getByLabelText('Stock item'), guiness.id);
    await user.type(within(dialog).getByLabelText('Quantity'), '1');
    const save = within(dialog).getByRole('button', { name: 'Record waste' });
    expect(save).toBeDisabled();
    await user.click(within(dialog).getByRole('button', { name: 'Other (add note)' }));
    expect(save).toBeDisabled();
    await user.click(within(dialog).getByRole('button', { name: 'Broken' }));
    await user.click(save);
    await waitFor(() => expect(writes('/api/admin/stock/waste')).toHaveLength(1));
    expect(writes('/api/admin/stock/waste')[0].body).toMatchObject({
      locationId: bar.id,
      stockItemId: guiness.id,
      quantity: 1,
      unit: 'bottle',
      reason: 'Broken',
    });
    expect(await screen.findByText('Saved. Stock updated.')).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Transfer' }));
    dialog = screen.getByRole('dialog', { name: 'Transfer' });
    await user.type(within(dialog).getByLabelText('Quantity'), '2');
    await user.click(within(dialog).getByRole('button', { name: 'Save transfer' }));
    await waitFor(() => expect(writes('/api/admin/stock/transfers')).toHaveLength(1));
    expect(writes('/api/admin/stock/transfers')[0].body).toMatchObject({
      fromLocationId: store.id,
      toLocationId: bar.id,
      lines: [{ stockItemId: tusker.id, quantity: 2, unit: 'crate' }],
    });
    expect(
      await screen.findByText('Saved. It waits for approval before stock moves.'),
    ).toBeInTheDocument();
  });

  it('lists what awaits approval and approves it', async () => {
    const user = userEvent.setup();
    awaiting = [
      {
        id: 'd1',
        kind: 'Reversal',
        at: '2026-10-24T10:00:00Z',
        summary: 'KBL · invoice 4471',
        location: 'Store',
        value: 10000,
        status: 'Pending',
        by: 'MAU',
        approvedBy: null,
        note: 'Wrong invoice',
        approveKey: 'stock.adjust',
      },
    ];
    extra = { 'POST /api/admin/deliveries/d1/reverse/approve': () => json(200, {}) };
    renderRoutes(routes, '/admin/stock');
    const list = await screen.findByRole('region', { name: 'Awaiting approval (1)' });
    expect(within(list).getByText(/KBL · invoice 4471/)).toBeInTheDocument();
    await user.click(within(list).getByRole('button', { name: 'Approve' }));
    await waitFor(() => expect(writes('/api/admin/deliveries/d1/reverse/approve')).toHaveLength(1));
  });
});

describe('Deliveries', () => {
  const fetchMock = vi.fn<typeof fetch>();
  const calls: { method: string; path: string; body: unknown }[] = [];
  let extra: Parameters<typeof fakeServer>[0];

  beforeEach(() => {
    vi.stubGlobal('fetch', fetchMock);
    calls.length = 0;
    extra = {};
    fetchMock.mockImplementation(async (input, init) => {
      const url = new URL(String(input), 'http://localhost');
      const method = (init?.method ?? 'GET').toUpperCase();
      calls.push({
        method,
        path: url.pathname + url.search,
        body: typeof init?.body === 'string' ? JSON.parse(init.body) : undefined,
      });
      return fakeServer({
        'GET /api/auth/me': () =>
          json(200, { user: { id: 'a', name: 'Administrator', role: 'Admin' }, permissions: {} }),
        'GET /api/brand': () =>
          json(200, { name: "Mau's Village", logoFileId: null, currency: 'KES' }),
        'GET /api/admin/stock': () => json(200, overview()),
        'GET /api/admin/stock/awaiting': () => json(200, []),
        'GET /api/admin/deliveries': () => json(200, []),
        'GET /api/admin/suppliers': () =>
          json(200, [
            {
              id: 's-kbl',
              name: 'KBL',
              phone: '',
              kraPin: '',
              notes: '',
              active: true,
              owed: 0,
              rowVersion: 1,
            },
          ]),
        'GET /api/admin/deliveries/last-cost': () =>
          json(200, {
            unitCost:
              url.searchParams.get('stockItemId') === tusker.id &&
              url.searchParams.get('unit') === 'crate'
                ? 2500
                : null,
          }),
        ...extra,
      })(input, init);
    });
  });

  afterEach(() => {
    fetchMock.mockReset();
    vi.unstubAllGlobals();
  });

  it('defaults a line to the largest pack and the last cost, totals it, and shows the invoice check', async () => {
    const user = userEvent.setup();
    extra = {
      'POST /api/admin/deliveries': () =>
        json(409, {
          status: 409,
          detail: 'Invoice 4471 from KBL is already recorded on 12 Oct.',
          code: 'invoice_used',
        }),
    };
    renderRoutes(routes, '/admin/deliveries');
    await user.click(await screen.findByRole('button', { name: 'Receive a delivery' }));
    await user.click(screen.getByRole('button', { name: '+ Add item' }));

    expect(screen.getByLabelText('Pack, line 1')).toHaveValue('crate');
    await waitFor(() => expect(screen.getByLabelText('Unit cost, line 1')).toHaveValue('2500'));
    await user.type(screen.getByLabelText('Quantity, line 1'), '4');
    expect(screen.getByTestId('delivery-total')).toHaveTextContent('KSh 10,000');

    await user.click(screen.getByRole('button', { name: 'Paid M-Pesa / bank' }));
    expect(screen.getByRole('button', { name: 'Save delivery' })).toBeDisabled();
    await user.type(screen.getByLabelText('Reference'), 'QWE123');
    await user.click(screen.getByRole('button', { name: 'On credit' }));
    expect(
      screen.getByText(
        'On credit adds to what you owe this supplier. Stock and its value go up when you save.',
      ),
    ).toBeInTheDocument();

    await user.type(screen.getByLabelText('Supplier invoice no.'), '4471');
    await user.click(screen.getByRole('button', { name: 'Save delivery' }));
    expect(
      await screen.findByText('Invoice 4471 from KBL is already recorded on 12 Oct.'),
    ).toBeInTheDocument();
    expect(
      calls.find((c) => c.path === '/api/admin/deliveries' && c.method === 'POST')!.body,
    ).toMatchObject({
      supplierId: 's-kbl',
      invoiceNumber: '4471',
      payment: 'OnCredit',
      paymentReference: null,
      lines: [{ stockItemId: tusker.id, unit: 'crate', quantity: 4, unitCost: 2500 }],
    });
  });

  it('opens a delivery and reverses it with a reason', async () => {
    const user = userEvent.setup();
    const delivery: Delivery = {
      id: 'd1',
      date: '2026-10-24',
      supplierId: 's-kbl',
      supplier: 'KBL',
      invoiceNumber: '4471',
      locationId: store.id,
      location: 'Store',
      total: 10000,
      payment: 'OnCredit',
      paymentReference: null,
      status: 'Posted',
      reversalStatus: null,
      reversalReason: null,
      recordedBy: 'MAU',
      approvedBy: null,
      createdAt: '2026-10-24T10:00:00Z',
      rejectNote: null,
      lines: [
        {
          stockItemId: tusker.id,
          stockItem: 'Tusker Lager',
          unit: 'crate',
          factor: 25,
          quantity: 4,
          unitCost: 2500,
          lineTotal: 10000,
        },
      ],
    };
    extra = {
      'GET /api/admin/deliveries': () => json(200, [delivery]),
      'POST /api/admin/deliveries/d1/reverse': () =>
        json(409, {
          status: 409,
          detail: 'Some of this delivery has already been sold. Count the stock instead.',
          code: 'already_sold',
        }),
    };
    renderRoutes(routes, '/admin/deliveries');
    await user.click(await screen.findByRole('button', { name: 'Open delivery KBL 4471' }));
    const dialog = screen.getByRole('dialog', { name: 'KBL · invoice 4471' });
    await user.click(within(dialog).getByRole('button', { name: 'Reverse delivery' }));
    const confirm = within(dialog).getByRole('button', { name: 'Reverse it' });
    expect(confirm).toBeDisabled();
    await user.type(within(dialog).getByLabelText('Why is it reversed?'), 'Wrong invoice');
    await user.click(confirm);
    expect(
      await within(dialog).findByText(
        'Some of this delivery has already been sold. Count the stock instead.',
      ),
    ).toBeInTheDocument();
  });
});

describe('Stock count', () => {
  const fetchMock = vi.fn<typeof fetch>();
  const calls: { method: string; path: string; body: unknown }[] = [];
  let count: StockCount;
  let extra: Parameters<typeof fakeServer>[0];

  const line = (name: string, extraLine: Partial<CountLine> = {}): CountLine => ({
    stockItemId: `i-${name}`,
    stockItem: name,
    baseUnit: 'bottle',
    units: [
      { name: 'bottle', factor: 1 },
      { name: 'crate', factor: 25 },
    ],
    entered: {},
    counted: null,
    countedText: null,
    expected: null,
    expectedText: null,
    difference: null,
    value: null,
    unitCost: null,
    ...extraLine,
  });

  const draft = (): StockCount => ({
    id: 'c1',
    locationId: bar.id,
    location: 'Bar',
    kind: 'Full',
    isOpening: false,
    status: 'Draft',
    asAt: '2026-10-24T22:00:00Z',
    countedBy: 'MAU',
    submittedAt: null,
    approvedBy: null,
    approvedAt: null,
    rejectNote: null,
    dayOpen: true,
    blind: true,
    uncounted: 2,
    itemsShort: 0,
    varianceValue: null,
    canApprove: false,
    lines: [line('Tusker Lager'), line('Guiness')],
    rowVersion: 1,
  });

  beforeEach(() => {
    vi.stubGlobal('fetch', fetchMock);
    calls.length = 0;
    count = draft();
    extra = {};
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
        'GET /api/brand': () =>
          json(200, { name: "Mau's Village", logoFileId: null, currency: 'KES' }),
        'GET /api/admin/stock-counts/c1': () => json(200, count),
        'PUT /api/admin/stock-counts/c1/lines': () => json(200, count),
        ...extra,
      })(input, init);
    });
  });

  afterEach(() => {
    fetchMock.mockReset();
    vi.unstubAllGlobals();
  });

  it('is blind while counting, totals packs and bottles, and shows why it can’t be submitted', async () => {
    const user = userEvent.setup();
    extra = {
      'POST /api/admin/stock-counts/c1/submit': () =>
        json(400, {
          status: 400,
          detail: '1 item not counted. Enter 0 if there are none.',
          code: 'uncounted',
        }),
    };
    renderRoutes(routes, '/admin/stock-counts/c1');
    expect(await screen.findByRole('heading', { name: 'Stock count · Bar' })).toBeInTheDocument();
    expect(screen.queryByRole('columnheader', { name: 'Expected' })).not.toBeInTheDocument();
    expect(screen.getByText(/Sales after .* are still recorded/)).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Print count sheet' })).toHaveAttribute(
      'href',
      '/api/admin/stock-counts/c1/sheet.pdf',
    );

    await user.type(screen.getByLabelText('Tusker Lager: number of crate'), '3');
    await user.type(screen.getByLabelText('Tusker Lager: number of bottle'), '7');
    expect(screen.getByTestId('count-total-Tusker Lager')).toHaveTextContent('= 82 bottle');
    expect(
      screen.getByText('1 items not counted yet. Enter 0 if there are none.'),
    ).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Submit for approval' }));
    expect(
      await screen.findByText('1 item not counted. Enter 0 if there are none.'),
    ).toBeInTheDocument();
    expect(calls.find((c) => c.path === '/api/admin/stock-counts/c1/lines')!.body).toEqual({
      lines: [
        { stockItemId: 'i-Tusker Lager', entered: { crate: 3, bottle: 7 }, unitCost: null },
        { stockItemId: 'i-Guiness', entered: {}, unitCost: null },
      ],
    });
  });

  it('after submitting shows the differences in orange, and an approver approves or rejects', async () => {
    const user = userEvent.setup();
    count = {
      ...draft(),
      status: 'Submitted',
      blind: false,
      uncounted: 0,
      itemsShort: 1,
      varianceValue: -300,
      canApprove: true,
      lines: [
        line('Tusker Lager', {
          counted: 47,
          countedText: '1 crate 22 bottles',
          expected: 50,
          expectedText: '2 crates',
          difference: -3,
          value: -300,
          entered: { bottle: 47 },
        }),
        line('Guiness', {
          counted: 25,
          countedText: '1 crate',
          expected: 25,
          expectedText: '1 crate',
          difference: 0,
          value: 0,
          entered: { crate: 1 },
        }),
      ],
    };
    extra = {
      'POST /api/admin/stock-counts/c1/approve': () =>
        json(
          200,
          (count = {
            ...count,
            status: 'Approved',
            canApprove: false,
            approvedBy: 'Administrator',
          }),
        ),
      'POST /api/admin/stock-counts/c1/reject': () => json(200, (count = draft())),
    };
    renderRoutes(routes, '/admin/stock-counts/c1');
    expect(await screen.findByRole('columnheader', { name: 'Expected' })).toBeInTheDocument();
    const tuskerRow = screen.getByRole('rowheader', { name: 'Tusker Lager' }).closest('tr')!;
    expect(tuskerRow).toHaveClass('stock-row--low');
    expect(within(tuskerRow).getByText('−3')).toHaveClass('stock-warn');
    expect(screen.getByRole('rowheader', { name: 'Guiness' }).closest('tr')).not.toHaveClass(
      'stock-row--low',
    );
    expect(screen.getByTestId('total-variance')).toHaveTextContent('KSh −300');
    expect(
      screen.getByText(
        'Approving the count sets stock to the counted figures and posts the variance to the books as stock loss.',
      ),
    ).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Reject' }));
    await user.type(screen.getByLabelText('Note for the counter'), 'Count the cooler');
    await user.click(screen.getByRole('button', { name: 'Return to counting' }));
    await waitFor(() =>
      expect(calls.find((c) => c.path === '/api/admin/stock-counts/c1/reject')!.body).toEqual({
        note: 'Count the cooler',
      }),
    );
    expect(await screen.findByRole('button', { name: 'Submit for approval' })).toBeInTheDocument();
  });

  it('approves a submitted count', async () => {
    const user = userEvent.setup();
    count = {
      ...draft(),
      status: 'Submitted',
      blind: false,
      canApprove: true,
      varianceValue: 0,
      uncounted: 0,
    };
    extra = {
      'POST /api/admin/stock-counts/c1/approve': () =>
        json(200, (count = { ...count, status: 'Approved', canApprove: false })),
    };
    renderRoutes(routes, '/admin/stock-counts/c1');
    await user.click(await screen.findByRole('button', { name: 'Approve count' }));
    expect(
      await screen.findByText('Count approved. Stock set to the counted figures.'),
    ).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Approve count' })).not.toBeInTheDocument();
  });
});
