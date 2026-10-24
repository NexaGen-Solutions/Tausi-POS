import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { routes } from '../../app/routes';
import { fakeServer, json, renderRoutes } from '../../test/render';
import type { PermissionsGrid, PermissionValue } from './types';

vi.mock('../../shared/connection/hub', () => ({
  startHub: vi.fn(),
  stopHub: vi.fn(async () => {}),
  onPosEvent: vi.fn(() => () => {}),
}));

const roles = [
  {
    id: 'r-waiter',
    name: 'Waiter',
    systemKey: 'waiter',
    users: ['edna', 'CYNTHIA'],
    rowVersion: 1,
  },
  { id: 'r-cashier', name: 'Cashier', systemKey: 'cashier', users: ['CAROL'], rowVersion: 1 },
  {
    id: 'r-supervisor',
    name: 'Supervisor',
    systemKey: 'supervisor',
    users: ['MAU'],
    rowVersion: 1,
  },
  { id: 'r-admin', name: 'Admin', systemKey: 'admin', users: ['Administrator'], rowVersion: 1 },
];
const keys = [
  {
    key: 'order.void',
    section: 'Orders',
    label: 'Void a printed item',
    help: 'Needs approval: supervisor PIN or request.',
    approvable: true,
  },
  {
    key: 'ticket.settle',
    section: 'Tickets',
    label: 'Settle and take payment',
    help: 'Only cashiers settle.',
    approvable: false,
  },
  {
    key: 'permissions.manage',
    section: 'Admin',
    label: 'Permissions',
    help: 'This screen.',
    approvable: false,
  },
];
const cells = (values: Record<string, PermissionValue[]>) =>
  Object.fromEntries(
    roles.map((r, i) => [
      r.id,
      Object.fromEntries(Object.entries(values).map(([k, v]) => [k, v[i]])),
    ]),
  );

function grid(extraRoles: typeof roles = []): PermissionsGrid {
  return {
    version: 'v1',
    roles: [...roles, ...extraRoles],
    keys,
    cells: {
      ...cells({
        'order.void': ['NeedsApproval', 'NeedsApproval', 'Allowed', 'Allowed'],
        'ticket.settle': ['NotAllowed', 'Allowed', 'Allowed', 'Allowed'],
        'permissions.manage': ['NotAllowed', 'NotAllowed', 'NotAllowed', 'Allowed'],
      }),
      ...Object.fromEntries(
        extraRoles.map((r) => [
          r.id,
          {
            'order.void': 'NeedsApproval',
            'ticket.settle': 'NotAllowed',
            'permissions.manage': 'NotAllowed',
          },
        ]),
      ),
    } as PermissionsGrid['cells'],
  };
}

describe('Permissions', () => {
  const fetchMock = vi.fn<typeof fetch>();
  const calls: { method: string; path: string; body: unknown }[] = [];
  let current: PermissionsGrid;
  let extra: Parameters<typeof fakeServer>[0];

  beforeEach(() => {
    vi.stubGlobal('fetch', fetchMock);
    calls.length = 0;
    current = grid();
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
        'GET /api/admin/permissions': () => json(200, current),
        ...extra,
      })(input, init);
    });
  });

  afterEach(() => {
    fetchMock.mockReset();
    vi.unstubAllGlobals();
  });

  const cell = (role: string, label: string) =>
    screen.getByRole('button', { name: new RegExp(`^${role}: ${label}: `) });
  const writes = (path: string) => calls.filter((c) => c.method !== 'GET' && c.path === path);

  it('steps through the values, skipping Needs approval where it can not be approved, and stages changes', async () => {
    const user = userEvent.setup();
    extra = { 'PUT /api/admin/permissions': () => json(200, current) };
    renderRoutes(routes, '/admin/permissions');
    await screen.findByRole('table', { name: 'Permissions' });
    expect(screen.getByText('Only cashiers settle.')).toBeInTheDocument();

    await user.click(cell('Waiter', 'Settle and take payment'));
    expect(cell('Waiter', 'Settle and take payment')).toHaveAccessibleName(
      'Waiter: Settle and take payment: Allowed',
    );
    await user.click(cell('Waiter', 'Settle and take payment'));
    expect(cell('Waiter', 'Settle and take payment')).toHaveAccessibleName(
      'Waiter: Settle and take payment: Not allowed',
    );

    await user.click(cell('Waiter', 'Void a printed item'));
    expect(cell('Waiter', 'Void a printed item')).toHaveAccessibleName(
      'Waiter: Void a printed item: Not allowed',
    );
    expect(cell('Waiter', 'Void a printed item')).toHaveClass('permissions-pill--changed');
    await user.click(cell('Cashier', 'Void a printed item'));
    await user.click(cell('Cashier', 'Void a printed item'));
    expect(cell('Cashier', 'Void a printed item')).toHaveAccessibleName(
      'Cashier: Void a printed item: Allowed',
    );

    expect(screen.getByRole('button', { name: 'Save changes (2)' })).toBeEnabled();
    await user.click(screen.getByRole('button', { name: 'Discard' }));
    expect(screen.getByRole('button', { name: 'Save changes (0)' })).toBeDisabled();
    expect(cell('Waiter', 'Void a printed item')).toHaveAccessibleName(
      'Waiter: Void a printed item: Needs approval',
    );

    await user.click(cell('Waiter', 'Void a printed item'));
    await user.click(screen.getByRole('button', { name: 'Save changes (1)' }));
    await waitFor(() => expect(writes('/api/admin/permissions')).toHaveLength(1));
    expect(writes('/api/admin/permissions')[0].body).toEqual({
      version: 'v1',
      changes: [{ roleId: 'r-waiter', permissionKey: 'order.void', value: 'NotAllowed' }],
    });
    expect(await screen.findByText('Permissions saved. Terminals updated.')).toBeInTheDocument();
  });

  it("keeps the Admin role's permissions.manage locked", async () => {
    renderRoutes(routes, '/admin/permissions');
    const locked = await screen.findByRole('button', { name: /^Admin: Permissions: Allowed/ });
    expect(locked).toBeDisabled();
    expect(locked).toHaveAttribute('title', "Kept so you can't lock yourself out");
  });

  it('warns when nobody could approve a key and does not save', async () => {
    const user = userEvent.setup();
    renderRoutes(routes, '/admin/permissions');
    await screen.findByRole('table', { name: 'Permissions' });
    await user.click(cell('Supervisor', 'Void a printed item'));
    await user.click(cell('Admin', 'Void a printed item'));

    expect(
      screen.getAllByText(
        'Nobody can approve "Void a printed item". Allow it for at least one role before saving.',
      )[0],
    ).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Save changes (2)' })).toBeDisabled();

    await user.click(screen.getByRole('button', { name: 'Who approves what' }));
    expect(
      within(screen.getByRole('complementary', { name: 'Who approves what' })).getByText(
        /Nobody can approve/,
      ),
    ).toBeInTheDocument();
  });

  it('adds a role copied from another', async () => {
    const user = userEvent.setup();
    extra = {
      'POST /api/admin/roles': () =>
        json(
          200,
          (current = grid([
            {
              id: 'r-captain',
              name: 'Bar captain',
              systemKey: null as unknown as string,
              users: [],
              rowVersion: 1,
            },
          ])),
        ),
    };
    renderRoutes(routes, '/admin/permissions');
    await user.click(await screen.findByRole('button', { name: '+ Add role' }));
    const dialog = screen.getByRole('dialog', { name: 'Add a role' });
    await user.type(within(dialog).getByLabelText('Role name'), 'Bar captain');
    await user.selectOptions(within(dialog).getByLabelText('Copy permissions from'), 'r-waiter');
    await user.click(within(dialog).getByRole('button', { name: 'Add role' }));

    await waitFor(() => expect(writes('/api/admin/roles')).toHaveLength(1));
    expect(writes('/api/admin/roles')[0].body).toEqual({
      name: 'Bar captain',
      copyFromRoleId: 'r-waiter',
    });
    expect(await screen.findByRole('columnheader', { name: /Bar captain/ })).toBeInTheDocument();
    expect(cell('Bar captain', 'Void a printed item')).toHaveAccessibleName(
      'Bar captain: Void a printed item: Needs approval',
    );
  });

  it('does not delete a role that people have, and says who', async () => {
    const user = userEvent.setup();
    current = grid([
      {
        id: 'r-captain',
        name: 'Bar captain',
        systemKey: null as unknown as string,
        users: ['Otieno'],
        rowVersion: 1,
      },
    ]);
    renderRoutes(routes, '/admin/permissions');
    expect(await screen.findByRole('table', { name: 'Permissions' })).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Delete Waiter' })).not.toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Delete Bar captain' }));
    const dialog = screen.getByRole('dialog', { name: 'Delete Bar captain?' });
    expect(
      within(dialog).getByText('In use by Otieno. Give them another role in Staff first.'),
    ).toBeInTheDocument();
    expect(within(dialog).queryByRole('button', { name: 'Delete' })).not.toBeInTheDocument();
    expect(writes('/api/admin/roles/r-captain')).toHaveLength(0);
  });
});
