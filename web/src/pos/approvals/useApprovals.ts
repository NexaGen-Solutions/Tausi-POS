import { useQuery } from '@tanstack/react-query';
import { api } from '../../shared/api/client';
import type { Me } from '../../shared/api/types';
import { pendingKey, permissionFor, type ApprovalRequest } from '../../shared/approval/types';
import { usePinSession } from '../usePinSession';

/** Whether this user can approve anything at all (then the Approvals button shows). */
export function useCanApprove() {
  const session = usePinSession();
  const me = useQuery({
    queryKey: ['me'],
    queryFn: () => api<Me>('/api/auth/me'),
    enabled: !!session,
  });
  return Object.values(permissionFor).some((key) => me.data?.permissions[key] === 'Allowed');
}

/** The pending requests I can decide, and my own; refetched on every approval event. */
export function usePendingApprovals() {
  const session = usePinSession();
  const pending = useQuery({
    queryKey: pendingKey,
    queryFn: () => api<ApprovalRequest[]>('/api/pos/approvals?status=pending'),
    enabled: !!session,
    staleTime: 0,
  });
  const list = pending.data ?? [];
  return { pending, waiting: list.filter((r) => r.canDecide), mine: list.filter((r) => r.mine) };
}
