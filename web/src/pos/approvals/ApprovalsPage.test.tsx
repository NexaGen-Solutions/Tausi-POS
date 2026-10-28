import { act, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { routes } from '../../app/routes';
import type { ApprovalRequest } from '../../shared/approval/types';
import { updateConnection } from '../../shared/connection/status';
import { setDeviceForTests } from '../../shared/device/device';
import { setPinSession } from '../../shared/session/session';
import { deviceInfo, fakeServer, json, renderRoutes, sessionFor } from '../../test/render';

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

const minutesAgo = (m: number) => new Date(Date.now() - m * 60_000).toISOString();

function request(id: string, extra: Partial<ApprovalRequest> = {}): ApprovalRequest {
  return {
    id,
    kind: 'Void',
    status: 'Pending',
    ticketId: 'k1',
    ticketNumber: 1042,
    where: 'Table 7',
    lineId: 'l1',
    summary: 'Void 1 × Guiness',
    amount: 300,
    reason: 'Wrong item',
    requestedById: 'u-edna',
    requestedBy: 'edna',
    terminal: 'Pillar 1',
    requestedAt: minutesAgo(1),
    preferredApproverId: 'id-SAM',
    sentToMe: true,
    mine: false,
    canDecide: true,
    decidedBy: null,
    decidedAt: null,
    decisionNote: null,
    ...extra,
  };
}

describe('approvals screen', () => {
  const fetchMock = vi.fn<typeof fetch>();
  const calls: { method: string; path: string; body: unknown }[] = [];
  let pending: ApprovalRequest[];
  let decided: ApprovalRequest[];

  beforeEach(() => {
    vi.stubGlobal('fetch', fetchMock);
    setDeviceForTests({ deviceToken: 'device-1' });
    setPinSession(sessionFor('SAM', 'Supervisor'));
    pending = [request('r1')];
    decided = [
      request('d1', {
        summary: 'Void 2 × Sodas 300ml',
        where: 'Table 3',
        requestedBy: 'CYNTHIA',
        reason: 'Spilled or broken',
        status: 'Approved',
        canDecide: false,
      }),
      request('d2', {
        summary: 'Void 1 × Viceroy 375ml',
        where: 'Table 11',
        requestedBy: 'CYNTHIA',
        status: 'Rejected',
        decisionNote: 'Customer changed mind',
        canDecide: false,
      }),
    ];
    calls.length = 0;
    hub.clear();
    updateConnection({ apiReachable: true, hub: 'connected' });
    fetchMock.mockImplementation(async (input, init) => {
      const url = new URL(String(input), 'http://localhost');
      const method = (init?.method ?? 'GET').toUpperCase();
      const body = typeof init?.body === 'string' ? JSON.parse(init.body) : undefined;
      calls.push({ method, path: url.pathname, body });
      if (method === 'GET' && url.pathname === '/api/pos/approvals')
        return json(200, url.searchParams.get('status') === 'decided' ? decided : pending);
      return fakeServer({
        'GET /api/device': () => json(200, deviceInfo({ openedAt: '2026-10-20T08:14:00' })),
        'GET /api/brand': () => json(200, { name: 'Demo Bar', currency: 'KES' }),
        'GET /api/auth/me': () =>
          json(200, {
            user: { id: 'id-SAM', name: 'SAM', role: 'Supervisor' },
            permissions: { 'order.void': 'Allowed' },
          }),
        'GET /api/pos/tables': () =>
          json(200, {
            version: 1,
            serverTime: new Date().toISOString(),
            dayOpen: true,
            tableIdleMinutes: 30,
            areas: [],
            tables: [],
          }),
        'POST /api/pos/approvals/r1/approve': () => {
          pending = [];
          return json(200, { ...request('r1'), status: 'Approved' });
        },
        'POST /api/pos/approvals/r1/reject': () => {
          pending = [];
          return json(200, { ...request('r1'), status: 'Rejected' });
        },
      })(input, init);
    });
  });

  afterEach(() => {
    fetchMock.mockReset();
    vi.unstubAllGlobals();
    setPinSession(null);
  });

  it('shows what is waiting, marked sent to me, and the earlier decisions of this shift', async () => {
    renderRoutes(routes, '/pos/approvals');

    const card = (
      await screen.findByRole('heading', { name: 'Void 1 × Guiness · KSh 300' })
    ).closest('li')!;
    expect(screen.getByText('1 waiting')).toBeInTheDocument();
    expect(within(card).getByText('Sent to you')).toBeInTheDocument();
    expect(
      within(card).getByText('Table 7 · #1042 · requested by edna at Pillar 1 · 1 min ago'),
    ).toBeInTheDocument();
    expect(within(card).getByText('Reason: Wrong item')).toBeInTheDocument();
    expect(within(card).getByRole('button', { name: 'Approve void' })).toBeInTheDocument();

    const earlier = screen.getByRole('complementary', { name: 'Earlier this shift' });
    expect(await within(earlier).findByText('Void 2 × Sodas 300ml')).toBeInTheDocument();
    expect(within(earlier).getByText('Approved')).toBeInTheDocument();
    expect(within(earlier).getByText('Rejected')).toBeInTheDocument();
    expect(
      within(earlier).getByText('Table 11 · CYNTHIA · Customer changed mind'),
    ).toBeInTheDocument();
    expect(
      screen.getByText(/The supervisor can also open any table and void directly/),
    ).toBeInTheDocument();
  });

  it('approves', async () => {
    const user = userEvent.setup();
    renderRoutes(routes, '/pos/approvals');
    await user.click(await screen.findByRole('button', { name: 'Approve void' }));

    await waitFor(() =>
      expect(calls.some((c) => c.path === '/api/pos/approvals/r1/approve')).toBe(true),
    );
    expect(await screen.findByText('Nothing is waiting for you.')).toBeInTheDocument();
  });

  it('the approver confirms or changes "Was it already poured?" and their answer is sent', async () => {
    const user = userEvent.setup();
    pending = [request('r1', { wasPoured: true })];
    renderRoutes(routes, '/pos/approvals');
    const card = (
      await screen.findByRole('heading', { name: 'Void 1 × Guiness · KSh 300' })
    ).closest('li')!;
    const poured = within(card).getByRole('group', { name: 'Was it already poured?' });
    expect(within(poured).getByRole('button', { name: 'Yes' })).toHaveAttribute(
      'aria-pressed',
      'true',
    );

    await user.click(within(poured).getByRole('button', { name: 'No' }));
    await user.click(within(card).getByRole('button', { name: 'Approve void' }));

    await waitFor(() =>
      expect(calls.find((c) => c.path === '/api/pos/approvals/r1/approve')!.body).toEqual({
        wasPoured: false,
      }),
    );
  });

  it('rejects with a note', async () => {
    const user = userEvent.setup();
    renderRoutes(routes, '/pos/approvals');
    await user.click(await screen.findByRole('button', { name: 'Reject' }));
    await user.type(screen.getByLabelText('Note for the requester (optional)'), 'Already poured');
    await user.click(screen.getByRole('button', { name: 'Reject request' }));

    await waitFor(() =>
      expect(calls.find((c) => c.path === '/api/pos/approvals/r1/reject')?.body).toEqual({
        note: 'Already poured',
      }),
    );
  });

  it('a new request raises the badge and shows a toast that opens the screen', async () => {
    const user = userEvent.setup();
    const router = renderRoutes(routes, '/pos/tables');
    expect(await screen.findByRole('button', { name: 'Approvals, 1 waiting' })).toBeInTheDocument();

    pending = [
      request('r2', {
        summary: 'Reprint bill',
        kind: 'Reprint',
        where: 'Table 5',
        requestedBy: 'Ruth',
        amount: 1230,
      }),
      ...pending,
    ];
    emit('approval.requested', {
      requestId: 'r2',
      kind: 'Reprint',
      ticketId: 'k5',
      requestedBy: 'Ruth',
    });

    expect(await screen.findByText('Ruth asks: Reprint bill · Table 5.')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Approvals, 2 waiting' })).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Open' }));
    expect(router.state.location.pathname).toBe('/pos/approvals');
  });

  it('a user who can approve nothing sees no Approvals button', async () => {
    setPinSession(sessionFor('edna', 'Waiter'));
    fetchMock.mockImplementation(
      fakeServer({
        'GET /api/device': () => json(200, deviceInfo({ openedAt: '2026-10-20T08:14:00' })),
        'GET /api/brand': () => json(200, { name: 'Demo Bar', currency: 'KES' }),
        'GET /api/auth/me': () =>
          json(200, {
            user: { id: 'id-edna', name: 'edna', role: 'Waiter' },
            permissions: { 'order.void': 'NeedsApproval' },
          }),
        'GET /api/pos/approvals': () => json(200, []),
        'GET /api/pos/tables': () =>
          json(200, {
            version: 1,
            serverTime: new Date().toISOString(),
            dayOpen: true,
            tableIdleMinutes: 30,
            areas: [],
            tables: [],
          }),
      }),
    );
    renderRoutes(routes, '/pos/tables');
    await screen.findByRole('button', { name: 'Log out' });
    expect(screen.queryByRole('button', { name: /Approvals/ })).not.toBeInTheDocument();
  });
});
