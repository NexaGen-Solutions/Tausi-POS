import { screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { routes } from '../../app/routes';
import { fakeServer, json, renderRoutes } from '../../test/render';

vi.mock('../../shared/connection/hub', () => ({
  startHub: vi.fn(),
  stopHub: vi.fn(async () => {}),
  onPosEvent: vi.fn(() => () => {}),
}));

const area = (id: string, name: string, sortOrder: number, tables: number) => ({
  id,
  name,
  sortOrder,
  active: true,
  tables,
  rowVersion: 1,
});
const table = (name: string, areaId: string, areaName: string, openTickets: number[] = []) => ({
  id: `t-${name}`,
  name,
  areaId,
  areaName,
  sortOrder: 1,
  active: true,
  openTickets,
  hasHistory: openTickets.length > 0,
  rowVersion: 1,
});

describe('Tables admin', () => {
  const fetchMock = vi.fn<typeof fetch>();
  let sent: { path: string; body: unknown }[];

  beforeEach(() => {
    vi.stubGlobal('fetch', fetchMock);
    sent = [];
    fetchMock.mockImplementation(
      fakeServer({
        'GET /api/auth/me': () =>
          json(200, { user: { id: 'me', name: 'Administrator', role: 'Admin' }, permissions: {} }),
        'GET /api/brand': () => json(200, { name: 'Test Bar' }),
        'GET /api/admin/tables': () =>
          json(200, {
            areas: [area('main', 'Main', 10, 2), area('pool', 'Pool', 20, 1)],
            tables: [
              table('1', 'main', 'Main'),
              table('7', 'main', 'Main', [1042, 1047]),
              table('Pool 1', 'pool', 'Pool'),
            ],
          }),
        'POST /api/admin/tables/quick-add': ({ body }) => {
          sent.push({ path: 'quick-add', body });
          return json(200, { added: ['8', '9'], skipped: ['7'] });
        },
        'PUT /api/admin/tables/t-7': ({ body }) => {
          sent.push({ path: 'put 7', body });
          return json(400, {
            status: 400,
            title: 'Invalid request',
            code: 'table_in_use',
            detail:
              'These tables have open tickets: 7 (#1042, #1047). Settle, move or merge them first.',
          });
        },
      }),
    );
  });

  afterEach(() => {
    fetchMock.mockReset();
    vi.unstubAllGlobals();
  });

  it('lists areas and tables with their open tickets, and is in the sidebar after Terminals and printers', async () => {
    renderRoutes(routes, '/admin/tables');
    expect(await screen.findByText('#1042, #1047')).toBeInTheDocument();
    expect(
      within(screen.getByRole('table', { name: 'Tables' })).getByText('#1042, #1047'),
    ).toBeInTheDocument();
    const links = within(screen.getByRole('navigation', { name: 'Admin' }))
      .getAllByRole('link')
      .map((a) => a.textContent);
    expect(
      links.slice(
        links.indexOf('Terminals and printers'),
        links.indexOf('Terminals and printers') + 3,
      ),
    ).toEqual(['Terminals and printers', 'Tables', 'Reports']);
  });

  it('quick add sends the range and says what was added and skipped', async () => {
    renderRoutes(routes, '/admin/tables');
    const form = await screen.findByRole('form', { name: 'Quick add' });

    await userEvent.clear(within(form).getByLabelText('From'));
    await userEvent.type(within(form).getByLabelText('From'), '7');
    await userEvent.clear(within(form).getByLabelText('To'));
    await userEvent.type(within(form).getByLabelText('To'), '9');
    await userEvent.click(within(form).getByRole('button', { name: 'Add tables' }));

    expect(await screen.findByText('Added 8, 9. Already there: 7.')).toBeInTheDocument();
    expect(sent[0].body).toEqual({ from: 7, to: 9, areaId: 'main', prefix: null });
  });

  it('switching off a table with open tickets shows which tickets', async () => {
    renderRoutes(routes, '/admin/tables');
    await userEvent.click(await screen.findByRole('button', { name: 'Edit 7' }));
    const panel = screen.getByRole('complementary', { name: 'Edit table 7' });

    await userEvent.click(within(panel).getByRole('switch', { name: 'In use' }));
    await userEvent.click(within(panel).getByRole('button', { name: 'Save' }));

    expect(await within(panel).findByRole('alert')).toHaveTextContent('7 (#1042, #1047)');
    expect(sent[0].body).toMatchObject({ name: '7', active: false, rowVersion: 1 });
    expect(within(panel).queryByRole('button', { name: 'Delete' })).not.toBeInTheDocument();
  });
});
