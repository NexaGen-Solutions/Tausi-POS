import { screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { json, renderWithProviders } from '../../test/render';
import { api } from '../api/client';
import { setDeviceForTests } from '../device/device';

function VoidButton({ onResult }: { onResult: (r: unknown) => void }) {
  return (
    <button
      onClick={() =>
        api('/api/pos/tickets/k1/lines/l1/void', { method: 'POST' }).then(onResult, (e: unknown) =>
          onResult(e),
        )
      }
    >
      Void
    </button>
  );
}

const approvalNeeded = { status: 403, approvalRequired: true, permission: 'order.void' };

describe('approval pad', () => {
  const fetchMock = vi.fn<typeof fetch>();

  beforeEach(() => {
    vi.stubGlobal('fetch', fetchMock);
    setDeviceForTests({ deviceToken: 'device-1' });
  });

  afterEach(() => {
    fetchMock.mockReset();
    vi.unstubAllGlobals();
  });

  const calls = (path: string) =>
    fetchMock.mock.calls.filter(([url]) => String(url).endsWith(path));

  it('opens on approval required, takes the supervisor PIN and retries with the token', async () => {
    fetchMock.mockImplementation(async (url) => {
      const path = String(url);
      if (path.endsWith('/api/health')) return json(200, { server: 'POSSERVER2' });
      if (path.endsWith('/api/auth/approve'))
        return json(200, { approvalToken: 'tok-1', approvedByName: 'MAU' });
      const retried = (
        calls('/api/pos/tickets/k1/lines/l1/void').at(-1)?.[1]?.headers as Headers
      ).get('X-Approval-Token');
      return retried ? json(200, { ok: true }) : json(403, approvalNeeded);
    });
    const onResult = vi.fn();
    renderWithProviders(<VoidButton onResult={onResult} />);

    await userEvent.click(screen.getByRole('button', { name: 'Void' }));
    const dialog = await screen.findByRole('dialog', { name: 'Supervisor approval' });
    expect(dialog).toHaveTextContent('Void a printed item');

    for (const d of ['1', '2', '3', '4'])
      await userEvent.click(screen.getByRole('button', { name: d }));
    await userEvent.click(screen.getByRole('button', { name: 'Approve' }));

    await waitFor(() => expect(onResult).toHaveBeenCalledWith({ ok: true }));
    const approve = calls('/api/auth/approve')[0][1]!;
    expect(JSON.parse(approve.body as string)).toEqual({
      pin: '1234',
      permissionKey: 'order.void',
    });
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
  });

  it('shows a toast when the approver is not allowed', async () => {
    fetchMock.mockImplementation(async (url) => {
      const path = String(url);
      if (path.endsWith('/api/health')) return json(200, { server: 'POSSERVER2' });
      if (path.endsWith('/api/auth/approve'))
        return json(403, {
          status: 403,
          approvalRequired: false,
          detail: 'You are not allowed to do this.',
        });
      return json(403, approvalNeeded);
    });
    const onResult = vi.fn();
    renderWithProviders(<VoidButton onResult={onResult} />);

    await userEvent.click(screen.getByRole('button', { name: 'Void' }));
    await screen.findByRole('dialog');
    for (const d of ['5', '5', '5', '5'])
      await userEvent.click(screen.getByRole('button', { name: d }));
    await userEvent.click(screen.getByRole('button', { name: 'Approve' }));

    expect(await screen.findByText(/Not approved/)).toBeInTheDocument();
    await waitFor(() => expect(onResult).toHaveBeenCalled());
    expect(calls('/api/pos/tickets/k1/lines/l1/void')).toHaveLength(1);
  });

  it('wrong PIN keeps the pad open with a message', async () => {
    fetchMock.mockImplementation(async (url) => {
      const path = String(url);
      if (path.endsWith('/api/health')) return json(200, { server: 'POSSERVER2' });
      if (path.endsWith('/api/auth/approve')) return json(401, { status: 401, attemptsLeft: 4 });
      return json(403, approvalNeeded);
    });
    renderWithProviders(<VoidButton onResult={vi.fn()} />);

    await userEvent.click(screen.getByRole('button', { name: 'Void' }));
    for (const d of ['0', '0', '0', '0'])
      await userEvent.click(await screen.findByRole('button', { name: d }));
    await userEvent.click(screen.getByRole('button', { name: 'Approve' }));

    expect(await screen.findByText('That PIN is not right.')).toBeInTheDocument();
    expect(screen.getByRole('dialog')).toBeInTheDocument();
  });
});
