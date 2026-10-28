import { useQueryClient } from '@tanstack/react-query';
import { useEffect, useRef } from 'react';
import { useNavigate } from 'react-router';
import { onPosEvent } from '../../shared/connection/hub';
import { t } from '../../shared/i18n/i18n';
import type { ApprovalEvent } from '../../shared/approval/types';
import { useToast } from '../../shared/ui';
import { usePinSession } from '../usePinSession';
import { usePosRules } from '../../shared/device/usePosRules';
import { playChime, prepareChime } from './chime';
import { usePendingApprovals } from './useApprovals';

const nouns: Record<string, string> = {
  Void: 'void',
  Gift: 'gift',
  Discount: 'discount',
  PriceChange: 'price change',
  Reprint: 'reprint',
  Move: 'move',
  Merge: 'merge',
};

/**
 * On every terminal screen after login: keeps the approval lists live. A new request I can
 * decide plays a chime and shows "edna asks: Void 1 × Guiness · Table 7 · Open"; a decision on
 * my own request shows "SAM approved the void" or "SAM rejected: …".
 */
export function ApprovalsWatcher() {
  const session = usePinSession();
  const queries = useQueryClient();
  const toast = useToast();
  const navigate = useNavigate();
  const { pending, waiting } = usePendingApprovals();
  const seen = useRef<Set<string> | null>(null);
  const me = session?.user.id;
  const chime = usePosRules().approvalChime;

  useEffect(() => prepareChime(), []);

  useEffect(() => {
    if (!me) return;
    const refresh = (payload: unknown) => {
      const event = payload as ApprovalEvent;
      void queries.invalidateQueries({ queryKey: ['approvals'] });
      if (event.ticketId) void queries.invalidateQueries({ queryKey: ['ticket', event.ticketId] });
    };
    const resolved = (payload: unknown) => {
      refresh(payload);
      const event = payload as ApprovalEvent;
      if (event.requestedByUserId !== me) return;
      const what = nouns[event.kind] ?? event.kind;
      if (event.status === 'Approved')
        toast(t('approval.approved', { name: event.decidedBy ?? '', what }));
      else if (event.status === 'Rejected')
        toast(
          event.note
            ? t('approval.rejectedNote', { name: event.decidedBy ?? '', note: event.note })
            : t('approval.rejected', { name: event.decidedBy ?? '', what }),
          { tone: 'warning' },
        );
      else if (event.status === 'Expired')
        toast(t('approval.expired', { what, note: event.note ?? '' }), { tone: 'warning' });
    };
    const off = [
      onPosEvent('approval.requested', refresh),
      onPosEvent('approval.resolved', resolved),
    ];
    return () => off.forEach((unsubscribe) => unsubscribe());
  }, [me, queries, toast]);

  // Announce requests I can decide that weren't there before (not the ones waiting at login).
  useEffect(() => {
    if (!pending.data) return;
    const ids = new Set(waiting.map((r) => r.id));
    if (seen.current === null) {
      seen.current = ids;
      return;
    }
    const fresh = waiting.filter((r) => !seen.current!.has(r.id));
    seen.current = ids;
    if (fresh.length === 0) return;
    if (chime) playChime();
    for (const r of fresh)
      toast(t('approval.asks', { name: r.requestedBy, what: r.summary, where: r.where }), {
        action: { label: t('approval.open'), onClick: () => navigate('/pos/approvals') },
      });
  }, [pending.data, waiting, toast, navigate, chime]);

  return null;
}
