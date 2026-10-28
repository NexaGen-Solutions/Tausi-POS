import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { routes } from '../../app/routes';
import { fakeServer, json, renderRoutes } from '../../test/render';
import type { StaffList, StaffMember } from './types';

vi.mock('../../shared/connection/hub', () => ({
  startHub: vi.fn(),
  stopHub: vi.fn(async () => {}),
  onPosEvent: vi.fn(() => () => {}),
}));

const roles = {
  waiter: { id: 'r-w', name: 'Waiter', canUseAdminSite: false },
  cashier: { id: 'r-c', name: 'Cashier', canUseAdminSite: false },
  supervisor: { id: 'r-s', name: 'Supervisor', canUseAdminSite: true },
  admin: { id: 'r-a', name: 'Admin', canUseAdminSite: true },
};

function member(
  name: string,
  role: keyof typeof roles,
  extra: Partial<StaffMember> = {},
): StaffMember {
  return {
    id: `id-${name}`,
    name,
    roleId: roles[role].id,
    roleName: roles[role].name,
    active: true,
    mustChangePin: false,
    adminUserName: null,
    hasAdminPassword: false,
    lastLogin: null,
    terminalsLast7Days: [],
    rowVersion: 1,
    ...extra,
  };
}

const adminList: StaffList = {
  members: [
    member('Administrator', 'admin', { id: 'me', hasAdminPassword: true, adminUserName: 'admin' }),
    member('SAM', 'supervisor'),
    member('CAROL', 'cashier', { terminalsLast7Days: ['Counter A', 'Pillar 1'] }),
    member('edna', 'waiter', { mustChangePin: true }),
    member('Ruth', 'waiter', { active: false }),
  ],
  assignableRoles: [roles.waiter, roles.cashier, roles.supervisor, roles.admin],
};

describe('Staff and PINs', () => {
  const fetchMock = vi.fn<typeof fetch>();
  let created: unknown[] = [];

  function serve(list: StaffList, handlers: Parameters<typeof fakeServer>[0] = {}) {
    fetchMock.mockImplementation(
      fakeServer({
        'GET /api/auth/me': () =>
          json(200, { user: { id: 'me', name: 'Administrator', role: 'Admin' }, permissions: {} }),
        'GET /api/brand': () => json(200, { name: "Demo Bar" }),
        'GET /api/admin/staff': () => json(200, list),
        'POST /api/admin/staff': ({ body }) => {
          created.push(body);
          const b = body as { name: string };
          return json(200, {
            member: member(b.name, 'waiter', { mustChangePin: true }),
            temporaryPin: '6284',
          });
        },
        ...handlers,
      }),
    );
  }

  beforeEach(() => {
    vi.stubGlobal('fetch', fetchMock);
    created = [];
  });

  afterEach(() => {
    fetchMock.mockReset();
    vi.unstubAllGlobals();
  });

  const table = () => screen.getByRole('table', { name: 'Staff and PINs' });
  const names = () =>
    within(table())
      .getAllByRole('row')
      .slice(1)
      .map((row) => within(row).getAllByRole('cell')[0].textContent);

  it('shows active people by default, and filters by status, role and name', async () => {
    serve(adminList);
    renderRoutes(routes, '/admin/staff');
    await screen.findByRole('button', { name: 'Edit CAROL' });

    expect(names()).toEqual(['Administrator', 'CAROL', 'ednaPIN reset', 'SAM']);
    expect(screen.getByText('Counter A, Pillar 1')).toBeInTheDocument();

    await userEvent.selectOptions(screen.getByLabelText('Status'), 'all');
    expect(names().at(-1)).toBe('Ruth');
    expect(within(table()).getAllByRole('row').at(-1)).toHaveClass('staff-row--inactive');

    await userEvent.selectOptions(screen.getAllByLabelText('Role')[0], 'Waiter');
    expect(names()).toEqual(['ednaPIN reset', 'Ruth']);

    await userEvent.type(screen.getByLabelText('Search by name'), 'ru');
    expect(names()).toEqual(['Ruth']);
    expect(within(table()).getByRole('button', { name: 'Reactivate Ruth' })).toBeInTheDocument();
  });

  it('checks the add form before sending it', async () => {
    serve(adminList);
    renderRoutes(routes, '/admin/staff');
    await screen.findByRole('button', { name: 'Edit CAROL' });
    await userEvent.click(screen.getByRole('button', { name: 'Add staff' }));
    const panel = screen.getByRole('complementary', { name: 'Add staff' });
    const save = within(panel).getByRole('button', { name: 'Save staff member' });

    await userEvent.click(save);
    expect(within(panel).getByText('The name must be 2 to 40 characters.')).toBeInTheDocument();
    expect(within(panel).getByText('A PIN must be 4 to 6 digits.')).toBeInTheDocument();

    await userEvent.type(within(panel).getByLabelText('Name'), 'Grace');
    await userEvent.type(within(panel).getByLabelText('Starting PIN (4–6 digits)'), '1234');
    await userEvent.click(save);
    expect(
      within(panel).getByText('That PIN is too easy to guess. Choose another.'),
    ).toBeInTheDocument();

    // Supervisor role: an admin-site password of 10+ characters is needed.
    await userEvent.selectOptions(within(panel).getByLabelText('Role'), 'Supervisor');
    await userEvent.clear(within(panel).getByLabelText('Starting PIN (4–6 digits)'));
    await userEvent.type(within(panel).getByLabelText('Starting PIN (4–6 digits)'), '6284');
    await userEvent.click(save);
    expect(
      within(panel).getByText('Set an admin-site password for this role.'),
    ).toBeInTheDocument();
    await userEvent.type(within(panel).getByLabelText('Admin-site password'), 'short');
    await userEvent.click(save);
    expect(
      within(panel).getByText('The admin-site password must be at least 10 characters.'),
    ).toBeInTheDocument();
    expect(created).toEqual([]);
  });

  it('shows the starting PIN once, and never again after Done', async () => {
    serve(adminList);
    renderRoutes(routes, '/admin/staff');
    await screen.findByRole('button', { name: 'Edit CAROL' });
    await userEvent.click(screen.getByRole('button', { name: 'Add staff' }));
    const panel = screen.getByRole('complementary', { name: 'Add staff' });

    await userEvent.type(within(panel).getByLabelText('Name'), 'Grace');
    await userEvent.type(within(panel).getByLabelText('Starting PIN (4–6 digits)'), '6284');
    await userEvent.click(within(panel).getByRole('button', { name: 'Save staff member' }));

    const dialog = await screen.findByRole('dialog', { name: 'Temporary PIN' });
    expect(dialog).toHaveTextContent('Give this PIN to Grace. They must change it at first login.');
    expect(within(dialog).getByText('6284')).toBeInTheDocument();
    expect(created).toEqual([
      { name: 'Grace', roleId: 'r-w', startingPin: '6284', adminPassword: null },
    ]);

    await userEvent.click(within(dialog).getByRole('button', { name: 'Done' }));

    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
    expect(screen.queryByText('6284')).not.toBeInTheDocument();
    // The panel closed with the form; adding again starts empty.
    expect(screen.queryByRole('complementary')).not.toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: 'Add staff' }));
    expect(screen.getByLabelText('Starting PIN (4–6 digits)')).toHaveValue('');
  });

  it('a supervisor sees only waiters and cashiers and can give only those roles', async () => {
    serve({
      members: adminList.members.filter((m) => m.roleName === 'Waiter' || m.roleName === 'Cashier'),
      assignableRoles: [roles.waiter, roles.cashier],
    });
    renderRoutes(routes, '/admin/staff');
    await screen.findByRole('button', { name: 'Edit CAROL' });

    expect(screen.queryByText('Administrator')).not.toBeInTheDocument();
    expect(screen.queryByText('SAM')).not.toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: 'Add staff' }));
    const panel = screen.getByRole('complementary', { name: 'Add staff' });
    const options = within(within(panel).getByLabelText('Role')).getAllByRole('option');
    expect(options.map((o) => o.textContent)).toEqual(['Waiter', 'Cashier']);
    expect(within(panel).queryByLabelText('Admin-site password')).not.toBeInTheDocument();
    expect(
      within(panel).getByText('Supervisors can add Waiters and Cashiers only.'),
    ).toBeInTheDocument();
  });

  it('a 409 says someone else changed the person and offers a reload', async () => {
    let gets = 0;
    serve(adminList, {
      'GET /api/admin/staff': () => {
        gets++;
        return json(200, adminList);
      },
      'PUT /api/admin/staff/id-CAROL': () =>
        json(409, {
          status: 409,
          code: 'stale',
          detail: 'This person was changed by someone else. Reload and try again.',
        }),
    });
    renderRoutes(routes, '/admin/staff');
    await screen.findByRole('button', { name: 'Edit CAROL' });

    await userEvent.click(within(table()).getByRole('button', { name: 'Edit CAROL' }));
    const panel = screen.getByRole('complementary', { name: 'Edit CAROL' });
    await userEvent.clear(within(panel).getByLabelText('Name'));
    await userEvent.type(within(panel).getByLabelText('Name'), 'Carol');
    await userEvent.click(within(panel).getByRole('button', { name: 'Save changes' }));

    // Shown in the banner (with Reload) and next to the form.
    expect(
      await screen.findAllByText('This person was changed by someone else. Reload and try again.'),
    ).toHaveLength(2);
    const before = gets;
    await userEvent.click(screen.getByRole('button', { name: 'Reload' }));
    await waitFor(() => expect(gets).toBeGreaterThan(before));
    expect(screen.queryByRole('complementary')).not.toBeInTheDocument();
  });

  it('cannot deactivate yourself; deactivating someone else asks first', async () => {
    const posted: string[] = [];
    serve(adminList, {
      'POST /api/admin/staff/id-CAROL/deactivate': () => {
        posted.push('CAROL');
        return json(200, member('CAROL', 'cashier', { active: false }));
      },
    });
    renderRoutes(routes, '/admin/staff');
    await screen.findByRole('button', { name: 'Edit CAROL' });

    expect(
      within(table()).queryByRole('button', { name: 'Deactivate Administrator' }),
    ).not.toBeInTheDocument();
    await userEvent.click(within(table()).getByRole('button', { name: 'Deactivate CAROL' }));
    const dialog = screen.getByRole('dialog', { name: 'Deactivate CAROL?' });
    expect(posted).toEqual([]);
    await userEvent.click(within(dialog).getByRole('button', { name: 'Deactivate' }));
    await waitFor(() => expect(posted).toEqual(['CAROL']));
  });
});
