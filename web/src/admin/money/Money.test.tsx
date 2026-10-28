import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { routes } from '../../app/routes';
import { fakeServer, json, renderRoutes } from '../../test/render';
import type { StockActivity, Supplier } from '../stock/types';
import { addDays, todayIso } from './dates';
import type { Accounts, CashBook, DailySummary, EntryDetail, MoneyDocument } from './types';

vi.mock('../../shared/connection/hub', () => ({
  startHub: vi.fn(),
  stopHub: vi.fn(async () => {}),
  onPosEvent: vi.fn(() => () => {}),
}));

const today = todayIso();
const yesterday = addDays(today, -1);

const summary: DailySummary = {
  date: yesterday,
  salesInclVat: 11600,
  vat: 1600,
  netSales: 10000,
  costOfSales: 4000,
  grossProfit: 6000,
  grossMarginPercent: 60,
  expenses: 1500,
  expensesByCategory: [
    { name: 'Repairs', amount: 1200 },
    { name: 'Transport', amount: 300 },
  ],
  stockLossAndCash: 100,
  stockLossParts: [{ name: 'Cash short', amount: 100 }],
  bankCharges: 50,
  otherIncome: 0,
  net: 4350,
  noRecipeItems: ['Pool Table', 'Darts'],
  rounding: -0.5,
};

function book(extra: Partial<CashBook> = {}): CashBook {
  return {
    date: yesterday,
    tiles: {
      cashOnHand: 18500,
      inOpenDrawers: 2500,
      mpesaReceived: 4200,
      mpesaBalance: 9200,
      bank: 150000,
      owedToSuppliers: 30000,
    },
    rows: [
      {
        entryId: 'e-sales',
        at: `${yesterday}T15:00:00Z`,
        text: 'Cash sales · CAROL shift',
        account: 'Cash',
        in: 8000,
        out: null,
        late: false,
        reversed: false,
        reversal: false,
        moneyDocumentId: null,
        entries: 12,
      },
      {
        entryId: 'e-exp',
        at: `${yesterday}T16:00:00Z`,
        text: 'Repairs (expense)',
        account: 'Safe',
        in: null,
        out: 1500,
        late: true,
        reversed: false,
        reversal: false,
        moneyDocumentId: 'd-exp',
        entries: 1,
      },
    ],
    summary,
    canRecord: true,
    ...extra,
  };
}

const kbl: Supplier = {
  id: 's-kbl',
  name: 'KBL',
  phone: '',
  kraPin: '',
  notes: '',
  active: true,
  owed: 10000,
  rowVersion: 1,
};

function document(extra: Partial<MoneyDocument> = {}): MoneyDocument {
  return {
    id: 'd-exp',
    kind: 'Expense',
    title: 'Repairs (expense)',
    status: 'Posted',
    businessDate: yesterday,
    amount: 1500,
    fee: 0,
    from: 'Safe',
    to: null,
    category: 'Repairs',
    supplierId: null,
    supplier: null,
    payee: 'Fundi',
    reference: 'R-1',
    note: null,
    hasAttachment: true,
    createdBy: 'Administrator',
    createdAt: `${yesterday}T16:00:00Z`,
    approvedBy: null,
    rejectNote: null,
    reversalStatus: null,
    reversalReason: null,
    journalEntryId: 'e-exp',
    reversalEntryId: null,
    warning: null,
    openingLines: [],
    ...extra,
  };
}

const entry: EntryDetail = {
  id: 'e-exp',
  businessDate: yesterday,
  at: `${yesterday}T16:00:00Z`,
  memo: 'Repairs (expense)',
  by: 'Administrator',
  late: true,
  reversesEntryId: null,
  reversedByEntryId: null,
  lines: [
    {
      account: 'Expenses',
      code: 'EXPENSES',
      debit: 1500,
      credit: 0,
      memo: 'Fundi',
      category: 'Repairs',
    },
    { account: 'Safe', code: 'SAFE', debit: 0, credit: 1500, memo: 'Fundi', category: null },
  ],
  document: document(),
};

const accounts = (difference = 0): Accounts => ({
  asAt: yesterday,
  trial: { debits: 500000, credits: 500000 - difference, difference },
  groups: [
    {
      name: 'Money',
      total: 175000,
      accounts: [
        { code: 'SAFE', name: 'Safe', group: 'Money', balance: 20000 },
        { code: 'BANK', name: 'Bank', group: 'Money', balance: 155000 },
      ],
    },
    {
      name: 'Owner',
      total: -5000,
      accounts: [
        { code: 'OWNERS_DRAWINGS', name: "Owner's drawings", group: 'Owner', balance: -5000 },
      ],
    },
  ],
});

describe('Cash book', () => {
  const fetchMock = vi.fn<typeof fetch>();
  const calls: { method: string; path: string; search: string; body: unknown }[] = [];
  let extra: Parameters<typeof fakeServer>[0];
  let awaiting: StockActivity[];
  let current: CashBook;

  beforeEach(() => {
    vi.stubGlobal('fetch', fetchMock);
    calls.length = 0;
    extra = {};
    awaiting = [];
    current = book();
    fetchMock.mockImplementation(async (input, init) => {
      const url = new URL(String(input), 'http://localhost');
      const method = (init?.method ?? 'GET').toUpperCase();
      calls.push({
        method,
        path: url.pathname,
        search: url.search,
        body: typeof init?.body === 'string' ? JSON.parse(init.body) : init?.body,
      });
      return fakeServer({
        'GET /api/auth/me': () =>
          json(200, { user: { id: 'a', name: 'Administrator', role: 'Admin' }, permissions: {} }),
        'GET /api/brand': () => json(200, { name: 'Demo Bar', logoFileId: null, currency: 'KES' }),
        'GET /api/admin/cashbook': () =>
          json(200, { ...current, date: url.searchParams.get('date') ?? current.date }),
        'GET /api/admin/awaiting': () => json(200, awaiting),
        'GET /api/admin/cashbook/entries/e-exp': () => json(200, entry),
        'GET /api/admin/money/categories': () =>
          json(200, [
            { id: 'c-rep', name: 'Repairs', active: true, inUse: true, rowVersion: 1 },
            { id: 'c-tra', name: 'Transport', active: true, inUse: false, rowVersion: 1 },
            { id: 'c-old', name: 'Old', active: false, inUse: false, rowVersion: 1 },
          ]),
        'GET /api/admin/suppliers': () => json(200, [kbl]),
        'GET /api/admin/deliveries': () => json(200, []),
        'GET /api/admin/money/opening-balances': () => json(200, { locked: false }),
        'GET /api/admin/accounts': () => json(200, accounts()),
        ...extra,
      })(input, init);
    });
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  const gets = (path: string) => calls.filter((c) => c.method === 'GET' && c.path === path);
  const posts = (path: string) => calls.filter((c) => c.method === 'POST' && c.path === path);

  it('shows the day with arrows and a date picker; the next arrow stops at today', async () => {
    renderRoutes(routes, `/admin/cash-book?date=${yesterday}`);
    expect(await screen.findByRole('heading', { level: 1 })).toHaveTextContent('Cash book ·');
    expect(screen.getByLabelText('Business date')).toHaveValue(yesterday);

    await userEvent.click(screen.getByRole('button', { name: 'Previous day' }));
    await waitFor(() =>
      expect(gets('/api/admin/cashbook').at(-1)?.search).toContain(
        `date=${addDays(yesterday, -1)}`,
      ),
    );
    await userEvent.click(screen.getByRole('button', { name: 'Next day' }));
    await userEvent.click(screen.getByRole('button', { name: 'Next day' }));
    await waitFor(() => expect(screen.getByLabelText('Business date')).toHaveValue(today));
    expect(screen.getByRole('button', { name: 'Next day' })).toBeDisabled();
  });

  it('shows the tiles, with the money still in open drawers', async () => {
    renderRoutes(routes, `/admin/cash-book?date=${yesterday}`);
    const cash = await screen.findByRole('region', { name: 'Cash on hand' });
    expect(cash).toHaveTextContent('18,500');
    expect(cash).toHaveTextContent('incl. KSh 2,500 in open drawers');
    expect(screen.getByRole('region', { name: 'M-Pesa received' })).toHaveTextContent('4,200');
    expect(screen.getByRole('region', { name: 'M-Pesa received' })).toHaveTextContent(
      'M-Pesa balance 9,200',
    );
    expect(screen.getByRole('region', { name: 'Bank' })).toHaveTextContent('150,000');
    expect(screen.getByRole('region', { name: 'Owed to suppliers' })).toHaveTextContent('30,000');
  });

  it('lists the entries in plain words and switches between money only and all entries', async () => {
    renderRoutes(routes, `/admin/cash-book?date=${yesterday}`);
    expect(
      await screen.findByRole('button', { name: 'Cash sales · CAROL shift' }),
    ).toBeInTheDocument();
    expect(screen.getByText('Late entry')).toBeInTheDocument();
    expect(gets('/api/admin/cashbook').at(-1)?.search).toContain('all=false');
    expect(screen.getByRole('button', { name: 'Money only' })).toHaveAttribute(
      'aria-pressed',
      'true',
    );

    await userEvent.click(screen.getByRole('button', { name: 'All entries' }));
    await waitFor(() => expect(gets('/api/admin/cashbook').at(-1)?.search).toContain('all=true'));
    expect(screen.getByRole('button', { name: 'All entries' })).toHaveAttribute(
      'aria-pressed',
      'true',
    );
  });

  it('shows the daily summary, opens the expenses split and warns about items with no recipe', async () => {
    renderRoutes(routes, `/admin/cash-book?date=${yesterday}`);
    const panel = await screen.findByRole('region', { name: 'Daily summary' });
    expect(within(panel).getByText('Net sales').nextSibling).toHaveTextContent('10,000');
    expect(within(panel).getByText('Rounding').nextSibling).toHaveTextContent('− 0.5');
    expect(within(panel).getByText('Gross profit').parentElement).toHaveTextContent('60%');
    expect(within(panel).getByText('Net for the day').nextSibling).toHaveTextContent('4,350');
    expect(within(panel).queryByText('Repairs')).not.toBeInTheDocument();

    await userEvent.click(within(panel).getByRole('button', { name: /Expenses/ }));
    expect(within(panel).getByText('Repairs').nextSibling).toHaveTextContent('1,200');
    expect(within(panel).getByText('Transport')).toBeInTheDocument();
    expect(within(panel).getByRole('status')).toHaveTextContent(
      'Cost of sales is missing for 2 items with no recipe.',
    );
    expect(within(panel).getByRole('link', { name: 'See the list' })).toHaveAttribute(
      'href',
      '/admin/stock',
    );
  });

  it('adds an expense; a future date is refused and a date too far back shows the server reason', async () => {
    extra['POST /api/admin/money/expenses'] = () =>
      json(400, {
        status: 400,
        detail: 'The date can be at most 31 days back.',
        code: 'date_too_old',
      });
    renderRoutes(routes, `/admin/cash-book?date=${yesterday}`);
    await userEvent.click(await screen.findByRole('button', { name: 'Add expense' }));
    const dialog = await screen.findByRole('dialog', { name: 'Add expense' });
    expect(dialog).toHaveTextContent('Record it on the POS as a Pay out instead.');
    await userEvent.type(within(dialog).getByLabelText('Amount'), '1500');
    await waitFor(() => expect(within(dialog).getByLabelText('Category')).toHaveValue('c-rep'));
    expect(within(dialog).queryByRole('option', { name: 'Old' })).not.toBeInTheDocument();

    const date = within(dialog).getByLabelText('Date');
    await userEvent.clear(date);
    await userEvent.type(date, addDays(today, 1));
    expect(within(dialog).getByText("The date can't be in the future.")).toBeInTheDocument();
    expect(within(dialog).getByRole('button', { name: 'Save expense' })).toBeDisabled();

    await userEvent.clear(date);
    await userEvent.type(date, addDays(today, -60));
    await userEvent.click(within(dialog).getByRole('button', { name: 'Safe' }));
    await userEvent.click(within(dialog).getByRole('button', { name: 'Save expense' }));
    expect(
      await within(dialog).findByText('The date can be at most 31 days back.'),
    ).toBeInTheDocument();
    expect(posts('/api/admin/money/expenses')[0].body).toMatchObject({
      amount: 1500,
      categoryId: 'c-rep',
      paidFrom: 'Safe',
      date: addDays(today, -60),
      attachmentFileId: null,
    });
  });

  it('uploads the receipt first and refuses other file types', async () => {
    extra['POST /api/admin/money/attachments'] = () =>
      json(200, { fileId: 'f-1', fileName: 'r.png' });
    extra['POST /api/admin/money/expenses'] = () => json(200, document());
    renderRoutes(routes, `/admin/cash-book?date=${yesterday}`);
    await userEvent.click(await screen.findByRole('button', { name: 'Add expense' }));
    const dialog = await screen.findByRole('dialog', { name: 'Add expense' });
    await userEvent.type(within(dialog).getByLabelText('Amount'), '1500');
    const input = within(dialog).getByLabelText('Receipt (optional)');
    await userEvent.upload(input, new File(['x'], 'r.txt', { type: 'text/plain' }), {
      applyAccept: false,
    });
    expect(within(dialog).getByText('The receipt must be a JPG, PNG or PDF.')).toBeInTheDocument();
    await userEvent.upload(input, new File(['png'], 'r.png', { type: 'image/png' }));
    await userEvent.click(within(dialog).getByRole('button', { name: 'Save expense' }));

    await waitFor(() => expect(posts('/api/admin/money/expenses')).toHaveLength(1));
    expect(posts('/api/admin/money/attachments')[0].body).toBeInstanceOf(FormData);
    expect(posts('/api/admin/money/expenses')[0].body).toMatchObject({ attachmentFileId: 'f-1' });
    expect(await screen.findByText('Saved: Repairs (expense).')).toBeInTheDocument();
  });

  it('pays a supplier no more than what is owed', async () => {
    extra['POST /api/admin/money/supplier-payments'] = () =>
      json(200, document({ kind: 'SupplierPayment', title: 'Paid KBL', status: 'Pending' }));
    renderRoutes(routes, `/admin/cash-book?date=${yesterday}`);
    await userEvent.click(await screen.findByRole('button', { name: 'Pay supplier' }));
    const dialog = await screen.findByRole('dialog', { name: 'Pay supplier' });
    expect(await within(dialog).findByText('KSh 10,000')).toBeInTheDocument();

    await userEvent.type(within(dialog).getByLabelText('Amount'), '25000');
    expect(within(dialog).getByText('KBL is owed 10,000. Pay 10,000 or less.')).toBeInTheDocument();
    expect(within(dialog).getByRole('button', { name: 'Save payment' })).toBeDisabled();

    await userEvent.clear(within(dialog).getByLabelText('Amount'));
    await userEvent.type(within(dialog).getByLabelText('Amount'), '10000');
    await userEvent.click(within(dialog).getByRole('button', { name: 'Save payment' }));
    await waitFor(() =>
      expect(posts('/api/admin/money/supplier-payments')[0].body).toMatchObject({
        supplierId: 's-kbl',
        amount: 10000,
        paidFrom: 'Bank',
      }),
    );
    expect(
      await screen.findByText('Saved. It waits for approval before it posts.'),
    ).toBeInTheDocument();
  });

  it('records banking from the Safe to the Bank with a fee, warning when the Safe would go below 0', async () => {
    extra['POST /api/admin/money/moves'] = () => json(200, document({ title: 'Banked cash' }));
    renderRoutes(routes, `/admin/cash-book?date=${yesterday}`);
    await userEvent.click(await screen.findByRole('button', { name: 'Record banking' }));
    const dialog = await screen.findByRole('dialog', { name: 'Record banking' });
    const from = within(dialog).getByRole('group', { name: 'From' });
    expect(within(from).getByRole('button', { name: 'Safe' })).toHaveAttribute(
      'aria-pressed',
      'true',
    );
    const to = within(dialog).getByRole('group', { name: 'To' });
    expect(within(to).getByRole('button', { name: 'Bank' })).toHaveAttribute(
      'aria-pressed',
      'true',
    );

    // The Safe holds 18,500 − 2,500 in drawers = 16,000.
    await userEvent.type(within(dialog).getByLabelText('Amount'), '16000');
    await userEvent.type(within(dialog).getByLabelText('Fee (optional)'), '50');
    expect(within(dialog).getByRole('status')).toHaveTextContent(
      'This takes the Safe below 0 (by 50)',
    );
    await userEvent.click(within(dialog).getByRole('button', { name: 'Save' }));
    await waitFor(() =>
      expect(posts('/api/admin/money/moves')[0].body).toMatchObject({
        from: 'Safe',
        to: 'Bank',
        amount: 16000,
        fee: 50,
      }),
    );
  });

  it('opens other actions from the More menu', async () => {
    extra['POST /api/admin/money/owner'] = () => json(200, document({ title: 'Owner took money' }));
    renderRoutes(routes, `/admin/cash-book?date=${yesterday}`);
    await userEvent.click(await screen.findByRole('button', { name: 'More' }));
    await userEvent.click(screen.getByRole('button', { name: 'Owner money' }));
    const dialog = await screen.findByRole('dialog', { name: 'Owner money' });
    await userEvent.type(within(dialog).getByLabelText('Amount'), '5000');
    await userEvent.click(within(dialog).getByRole('button', { name: 'Save' }));
    await waitFor(() =>
      expect(posts('/api/admin/money/owner')[0].body).toMatchObject({
        tookOut: true,
        account: 'Safe',
        amount: 5000,
      }),
    );
  });

  it('shows opening balances as locked once they are saved', async () => {
    extra['GET /api/admin/money/opening-balances'] = () => json(200, { locked: true });
    renderRoutes(routes, `/admin/cash-book?date=${yesterday}`);
    await userEvent.click(await screen.findByRole('button', { name: 'More' }));
    await userEvent.click(screen.getByRole('button', { name: 'Opening balances' }));
    const dialog = await screen.findByRole('dialog', { name: 'Opening balances' });
    expect(
      await within(dialog).findByText(/Opening balances are saved and locked/),
    ).toBeInTheDocument();
    expect(
      within(dialog).queryByRole('button', { name: 'Save opening balances' }),
    ).not.toBeInTheDocument();
  });

  it('opens an entry and reverses it with a reason', async () => {
    extra['POST /api/admin/money/d-exp/reverse'] = () =>
      json(200, document({ reversalStatus: 'Posted', reversalReason: 'Entered twice' }));
    renderRoutes(routes, `/admin/cash-book?date=${yesterday}`);
    await userEvent.click(await screen.findByRole('button', { name: 'Repairs (expense)' }));
    const dialog = await screen.findByRole('dialog', { name: 'Repairs (expense)' });
    expect(within(dialog).getByText('Expenses · Repairs')).toBeInTheDocument();
    expect(within(dialog).getByRole('link', { name: 'Open the receipt' })).toHaveAttribute(
      'href',
      '/api/admin/money/d-exp/attachment',
    );

    await userEvent.click(within(dialog).getByRole('button', { name: 'Reverse' }));
    expect(within(dialog).getByRole('button', { name: 'Reverse the entry' })).toBeDisabled();
    await userEvent.type(
      within(dialog).getByLabelText('Why reverse it? (required)'),
      'Entered twice',
    );
    await userEvent.click(within(dialog).getByRole('button', { name: 'Reverse the entry' }));
    await waitFor(() =>
      expect(posts('/api/admin/money/d-exp/reverse')[0].body).toEqual({ reason: 'Entered twice' }),
    );
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
  });

  it('approves a money document from the shared queue', async () => {
    awaiting = [
      {
        id: 'd-pay',
        kind: 'Money',
        at: `${today}T09:00:00Z`,
        summary: 'Paid KBL',
        location: '24 Oct',
        value: 800,
        status: 'Pending',
        by: 'SAM',
        approvedBy: null,
        note: null,
        approveKey: 'cash.book',
      },
    ];
    extra['POST /api/admin/money/d-pay/approve'] = () => json(200, document({ id: 'd-pay' }));
    renderRoutes(routes, `/admin/cash-book?date=${yesterday}`);
    const queue = await screen.findByRole('region', { name: 'Awaiting approval (1)' });
    expect(queue).toHaveTextContent('Paid KBL');
    await userEvent.click(within(queue).getByRole('button', { name: 'Approve' }));
    await waitFor(() => expect(posts('/api/admin/money/d-pay/approve')).toHaveLength(1));
  });

  it('shows read-only figures without the action buttons for reports.all only', async () => {
    current = book({ canRecord: false });
    renderRoutes(routes, `/admin/cash-book?date=${yesterday}`);
    expect(await screen.findByRole('region', { name: 'Bank' })).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Add expense' })).not.toBeInTheDocument();
  });
});

describe('Accounts', () => {
  const fetchMock = vi.fn<typeof fetch>();
  const calls: string[] = [];
  let difference = 0;

  beforeEach(() => {
    vi.stubGlobal('fetch', fetchMock);
    calls.length = 0;
    difference = 0;
    fetchMock.mockImplementation(async (input, init) => {
      const url = new URL(String(input), 'http://localhost');
      calls.push(url.pathname + url.search);
      return fakeServer({
        'GET /api/auth/me': () =>
          json(200, { user: { id: 'a', name: 'Administrator', role: 'Admin' }, permissions: {} }),
        'GET /api/brand': () => json(200, { name: 'Demo Bar', logoFileId: null, currency: 'KES' }),
        'GET /api/admin/accounts': () => json(200, accounts(difference)),
        'GET /api/admin/accounts/SAFE/ledger': () =>
          json(200, {
            code: 'SAFE',
            name: 'Safe',
            from: url.searchParams.get('from'),
            to: url.searchParams.get('to'),
            opening: 20000,
            lines: [
              {
                entryId: 'e1',
                date: yesterday,
                at: `${yesterday}T16:00:00Z`,
                entry: 'Repairs (expense)',
                memo: 'Fundi',
                debit: 0,
                credit: 1500,
                balance: 18500,
                late: false,
              },
            ],
            closing: 18500,
          }),
      })(input, init);
    });
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('shows the balances by group and the trial balance check', async () => {
    renderRoutes(routes, '/admin/accounts');
    expect(await screen.findByText('Debits = Credits ✓')).toHaveAttribute('role', 'status');
    const owner = screen.getByRole('region', { name: /Owner/ });
    expect(within(owner).getByText("Owner's drawings").closest('tr')).toHaveTextContent('− 5,000');
  });

  it('warns when the books do not balance', async () => {
    difference = 25;
    renderRoutes(routes, '/admin/accounts');
    expect(await screen.findByRole('alert')).toHaveTextContent(
      "The books don't balance: debits − credits = 25.",
    );
  });

  it('opens an account ledger with its running balance', async () => {
    renderRoutes(routes, '/admin/accounts');
    await userEvent.click(await screen.findByRole('button', { name: 'Safe' }));
    const ledger = await screen.findByRole('complementary', { name: 'Account ledger' });
    expect(await within(ledger).findByText('Repairs (expense)')).toBeInTheDocument();
    expect(within(ledger).getByText('Repairs (expense)').closest('tr')).toHaveTextContent('18,500');
    expect(calls.some((c) => c.startsWith('/api/admin/accounts/SAFE/ledger?from='))).toBe(true);
    expect(ledger).toHaveTextContent('20,000');
  });
});
