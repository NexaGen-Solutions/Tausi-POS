import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { routes } from '../../app/routes';
import { fakeServer, json, renderRoutes } from '../../test/render';
import type { AdminMenu, ImportPreview } from './types';

vi.mock('../../shared/connection/hub', () => ({
  startHub: vi.fn(),
  stopHub: vi.fn(async () => {}),
  onPosEvent: vi.fn(() => () => {}),
}));

const standard = { id: 'l-std', name: 'Standard', active: true, isDefault: true, rowVersion: 1 };
const price2 = { id: 'l-p2', name: 'Price 2', active: true, isDefault: false, rowVersion: 1 };

function menu(extra: Partial<AdminMenu> = {}): AdminMenu {
  return {
    categories: [
      { id: 'c-beer', name: 'Beers', sortOrder: 10, active: true, activeItems: 2, rowVersion: 1 },
      {
        id: 'c-soft',
        name: 'Soft drinks',
        sortOrder: 20,
        active: true,
        activeItems: 1,
        rowVersion: 1,
      },
    ],
    items: [
      {
        id: 'i-tusker',
        name: 'Tusker Lager',
        shortName: 'Tusker Lager',
        categoryId: 'c-beer',
        sortOrder: 10,
        vatRate: null,
        vatExempt: false,
        active: true,
        erpItemCode: null,
        prices: { 'l-std': 250 },
        ownGroupIds: [],
        inheritedGroupIds: ['g-temp'],
        rowVersion: 3,
      },
      {
        id: 'i-guiness',
        name: 'Guiness',
        shortName: 'Guiness',
        categoryId: 'c-beer',
        sortOrder: 20,
        vatRate: null,
        vatExempt: false,
        active: true,
        erpItemCode: null,
        prices: { 'l-std': 300, 'l-p2': 320 },
        ownGroupIds: [],
        inheritedGroupIds: ['g-temp'],
        rowVersion: 1,
      },
      {
        id: 'i-soda',
        name: 'Sodas 300ml',
        shortName: 'Sodas 300ml',
        categoryId: 'c-soft',
        sortOrder: 10,
        vatRate: null,
        vatExempt: false,
        active: true,
        erpItemCode: null,
        prices: { 'l-std': 100 },
        ownGroupIds: [],
        inheritedGroupIds: [],
        rowVersion: 1,
      },
    ],
    priceLists: [standard, price2],
    groups: [
      {
        id: 'g-temp',
        name: 'Temperature',
        minSelect: 1,
        maxSelect: 1,
        sortOrder: 10,
        active: true,
        options: [
          { id: 'o-cold', name: 'Cold', priceAddOn: 0, sortOrder: 10, active: true },
          { id: 'o-warm', name: 'Warm', priceAddOn: 0, sortOrder: 20, active: true },
        ],
        categoryIds: ['c-beer'],
        itemIds: [],
        rowVersion: 1,
      },
    ],
    canEdit: true,
    canEditPrices: true,
    businessVatRate: 16,
    currency: 'KES',
    ...extra,
  };
}

describe('Menu and prices', () => {
  const fetchMock = vi.fn<typeof fetch>();
  let requests: { method: string; path: string; body: unknown }[] = [];

  function serve(data: AdminMenu, handlers: Parameters<typeof fakeServer>[0] = {}) {
    const server = fakeServer({
      'GET /api/auth/me': () =>
        json(200, { user: { id: 'me', name: 'Administrator', role: 'Admin' }, permissions: {} }),
      'GET /api/brand': () => json(200, { name: 'Demo Bar' }),
      'GET /api/admin/menu': () => json(200, data),
      'GET /api/admin/menu/price-log': () => json(200, []),
      'PUT /api/admin/menu/items/i-tusker': () => json(200, { id: 'i-tusker', rowVersion: 4 }),
      'PUT /api/admin/menu/items/i-tusker/prices': () => json(200, { changed: 1 }),
      ...handlers,
    });
    fetchMock.mockImplementation(async (input, init) => {
      const url = new URL(String(input), 'http://x');
      if (init?.method && init.method !== 'GET')
        requests.push({
          method: init.method,
          path: url.pathname,
          body: typeof init.body === 'string' ? JSON.parse(init.body) : init.body,
        });
      return server(input, init);
    });
  }

  beforeEach(() => {
    vi.stubGlobal('fetch', fetchMock);
    requests = [];
  });
  afterEach(() => {
    fetchMock.mockReset();
    vi.unstubAllGlobals();
  });

  it('lists items by category with a price column per active list, and filters', async () => {
    serve(menu());
    renderRoutes(routes, '/admin/menu');
    const table = await screen.findByRole('table', { name: 'Menu and prices' });

    const headers = within(table)
      .getAllByRole('columnheader')
      .map((h) => h.textContent);
    expect(headers).toEqual(['Name', 'Category', 'Standard', 'Price 2', 'Options', 'Active', '']);
    const guiness = within(table).getAllByText('Guiness')[0].closest('tr')!;
    expect(within(guiness).getByText('320')).toBeInTheDocument();
    expect(within(guiness).getByText('Temperature')).toBeInTheDocument();

    await userEvent.click(screen.getByRole('button', { name: 'Soft drinks' }));
    expect(within(table).queryAllByText('Guiness')).toHaveLength(0);
    expect(within(table).getAllByText('Sodas 300ml').length).toBeGreaterThan(0);

    await userEvent.click(screen.getByRole('button', { name: 'All' }));
    await userEvent.type(screen.getByLabelText('Search by name'), 'tusk');
    expect(within(table).queryAllByText('Guiness')).toHaveLength(0);
    expect(within(table).getAllByText('Tusker Lager').length).toBeGreaterThan(0);
  });

  it('counts the short name and shows net and VAT live as the price is typed', async () => {
    serve(menu());
    renderRoutes(routes, '/admin/menu');
    await userEvent.click(await screen.findByRole('button', { name: 'Edit Tusker Lager' }));
    const panel = screen.getByRole('complementary', { name: 'Edit Tusker Lager' });

    expect(within(panel).getByText(/12 of 18/)).toBeInTheDocument();
    await userEvent.clear(within(panel).getByLabelText('Short name'));
    await userEvent.type(within(panel).getByLabelText('Short name'), 'Tusker');
    expect(within(panel).getByText(/6 of 18/)).toBeInTheDocument();

    expect(within(panel).getByText('Net KSh 215.52 · VAT KSh 34.48')).toBeInTheDocument();
    const price = within(panel).getByLabelText('Standard price');
    await userEvent.clear(price);
    await userEvent.type(price, '260');
    expect(within(panel).getByText('Net KSh 224.14 · VAT KSh 35.86')).toBeInTheDocument();

    await userEvent.selectOptions(within(panel).getByLabelText('VAT rate'), 'exempt');
    expect(within(panel).getByText('Net KSh 260.00 · VAT KSh 0.00')).toBeInTheDocument();
  });

  it('saves details and prices as separate calls behind one Save', async () => {
    serve(menu());
    renderRoutes(routes, '/admin/menu');
    await userEvent.click(await screen.findByRole('button', { name: 'Edit Tusker Lager' }));
    const panel = screen.getByRole('complementary', { name: 'Edit Tusker Lager' });
    const price = within(panel).getByLabelText('Standard price');
    await userEvent.clear(price);
    await userEvent.type(price, '260');
    await userEvent.click(within(panel).getByRole('button', { name: 'Save' }));

    await waitFor(() => expect(requests).toHaveLength(2));
    expect(requests[0].path).toBe('/api/admin/menu/items/i-tusker');
    expect(requests[0].body).not.toHaveProperty('prices');
    expect(requests[1]).toEqual({
      method: 'PUT',
      path: '/api/admin/menu/items/i-tusker/prices',
      body: { prices: { 'l-std': 260 }, allowZero: false, rowVersion: 4 },
    });
  });

  it('an item can be zero-rated or VAT exempt, and the two are told apart', async () => {
    serve(menu());
    renderRoutes(routes, '/admin/menu');
    await userEvent.click(await screen.findByRole('button', { name: 'Edit Tusker Lager' }));
    let panel = screen.getByRole('complementary', { name: 'Edit Tusker Lager' });
    await userEvent.selectOptions(within(panel).getByLabelText('VAT rate'), 'Zero-rated (0%)');
    await userEvent.click(within(panel).getByRole('button', { name: 'Save' }));
    await waitFor(() => expect(requests).toHaveLength(1));
    expect(requests[0].body).toMatchObject({ vatRate: 0, vatExempt: false });

    requests.length = 0;
    await userEvent.click(await screen.findByRole('button', { name: 'Edit Tusker Lager' }));
    panel = screen.getByRole('complementary', { name: 'Edit Tusker Lager' });
    await userEvent.selectOptions(within(panel).getByLabelText('VAT rate'), 'VAT exempt');
    await userEvent.click(within(panel).getByRole('button', { name: 'Save' }));
    await waitFor(() => expect(requests).toHaveLength(1));
    expect(requests[0].body).toMatchObject({ vatRate: 0, vatExempt: true });
  });

  it('a price of 0 asks for confirmation first', async () => {
    serve(menu());
    renderRoutes(routes, '/admin/menu');
    await userEvent.click(await screen.findByRole('button', { name: 'Edit Tusker Lager' }));
    const panel = screen.getByRole('complementary', { name: 'Edit Tusker Lager' });
    await userEvent.clear(within(panel).getByLabelText('Standard price'));
    await userEvent.type(within(panel).getByLabelText('Standard price'), '0');
    await userEvent.click(within(panel).getByRole('button', { name: 'Save' }));

    const dialog = await screen.findByRole('dialog', { name: 'Save a price of 0?' });
    expect(requests).toEqual([]);
    await userEvent.click(within(dialog).getByRole('button', { name: 'Save as free' }));
    await waitFor(() => expect(requests.at(-1)?.body).toMatchObject({ allowZero: true }));
  });

  it('prices are read-only without menu.edit_prices, and nothing price-related is sent', async () => {
    serve(menu({ canEditPrices: false }));
    renderRoutes(routes, '/admin/menu');
    await userEvent.click(await screen.findByRole('button', { name: 'Edit Tusker Lager' }));
    const panel = screen.getByRole('complementary', { name: 'Edit Tusker Lager' });

    expect(within(panel).getByLabelText('Standard price')).toHaveAttribute('readonly');
    expect(within(panel).getByText(/Prices are read-only for you/)).toBeInTheDocument();
    await userEvent.type(within(panel).getByLabelText('Standard price'), '9');
    await userEvent.click(within(panel).getByRole('button', { name: 'Save' }));

    await waitFor(() => expect(requests).toHaveLength(1));
    expect(requests[0].path).toBe('/api/admin/menu/items/i-tusker');
  });

  it('shows the import preview and applies after a confirmation', async () => {
    const preview: ImportPreview = {
      headers: ['category', 'name', 'price'],
      mapping: ['category', 'name', 'price:l-std'],
      fields: [],
      needsMapping: false,
      mappingProblem: null,
      rows: [
        {
          line: 2,
          status: 'Updated',
          name: 'Tusker Lager',
          category: 'Beers',
          changes: [{ field: 'Price: Standard', old: '250', new: '260', isPrice: true }],
          error: null,
        },
        {
          line: 3,
          status: 'New',
          name: 'Chips',
          category: 'Food',
          changes: [{ field: 'Price: Standard', old: null, new: '150', isPrice: true }],
          error: null,
        },
        {
          line: 4,
          status: 'Unchanged',
          name: 'Guiness',
          category: 'Beers',
          changes: [],
          error: null,
        },
      ],
      newCategories: ['Food'],
      toDeactivate: [],
      new: 1,
      updated: 1,
      unchanged: 1,
      errors: 0,
      priceChanges: 2,
      existing: 0,
    };
    serve(menu(), {
      'POST /api/admin/menu/import/preview': () => json(200, preview),
      'POST /api/admin/menu/import/apply': () =>
        json(200, { new: 1, updated: 1, deactivated: 0, priceChanges: 2, skipped: 0 }),
    });
    renderRoutes(routes, '/admin/menu');
    await userEvent.click(await screen.findByRole('button', { name: 'Import CSV' }));
    const dialog = screen.getByRole('dialog', { name: 'Import menu from CSV' });

    await userEvent.upload(
      within(dialog).getByLabelText('CSV file'),
      new File(['category,name,price'], 'menu.csv', { type: 'text/csv' }),
    );

    expect(
      await within(dialog).findByText('1 new · 1 updated · 1 unchanged · 0 errors'),
    ).toBeInTheDocument();
    expect(within(dialog).getByText('New categories: Food')).toBeInTheDocument();
    expect(within(dialog).getByText('Price: Standard: 250 → 260')).toHaveClass(
      'menu-import__price',
    );
    expect(within(dialog).queryByText('Guiness')).not.toBeInTheDocument();

    await userEvent.click(within(dialog).getByRole('button', { name: 'Apply import' }));
    const confirm = screen.getByRole('dialog', { name: 'Apply this import?' });
    expect(confirm).toHaveTextContent('1 new, 1 updated, 2 prices changed');
    expect(requests.some((r) => r.path.endsWith('/apply'))).toBe(false);

    await userEvent.click(within(confirm).getByRole('button', { name: 'Apply import' }));
    expect(
      await screen.findByText('Imported: 1 new, 1 updated, 2 prices changed.'),
    ).toBeInTheDocument();
    expect(requests.filter((r) => r.path.endsWith('/apply'))).toHaveLength(1);
  });

  it('imports from SambaPOS after setup, with one price per item ticked', async () => {
    serve(menu());
    renderRoutes(routes, '/admin/menu');
    await userEvent.click(await screen.findByRole('button', { name: 'Import from SambaPOS' }));
    const dialog = screen.getByRole('dialog', { name: 'Import from SambaPOS' });
    expect(within(dialog).getByLabelText('SQL Server')).toBeInTheDocument();
    expect(within(dialog).getByLabelText(/One price per item/)).toBeChecked();
  });

  it('deactivating a category with items offers to deactivate them too', async () => {
    serve(menu(), {
      'POST /api/admin/menu/categories/c-beer/deactivate': () => json(200, { itemsDeactivated: 2 }),
    });
    renderRoutes(routes, '/admin/menu');
    await userEvent.click(await screen.findByRole('button', { name: 'Manage categories' }));
    await userEvent.click(screen.getByRole('button', { name: 'Deactivate Beers' }));

    const dialog = screen.getByRole('dialog', { name: 'Deactivate Beers?' });
    expect(dialog).toHaveTextContent('Beers has 2 active items. Deactivate them too?');
    await userEvent.click(
      within(dialog).getByRole('button', { name: 'Deactivate category and items' }),
    );

    await waitFor(() =>
      expect(requests).toContainEqual({
        method: 'POST',
        path: '/api/admin/menu/categories/c-beer/deactivate',
        body: { includeItems: true, rowVersion: 1 },
      }),
    );
  });
});
