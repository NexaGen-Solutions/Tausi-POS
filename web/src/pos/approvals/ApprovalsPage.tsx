import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useEffect, useState } from 'react';
import { useNavigate } from 'react-router';
import { ApiError, api } from '../../shared/api/client';
import type { Brand } from '../../shared/api/types';
import { OfflineBanner } from '../../shared/connection/OfflineBanner';
import { formatMoney } from '../../shared/format/money';
import { t } from '../../shared/i18n/i18n';
import type { ApprovalRequest } from '../../shared/approval/types';
import { usePosRules } from '../../shared/device/usePosRules';
import { Button, TopBar, useToast } from '../../shared/ui';
import { usePinSession } from '../usePinSession';
import { usePendingApprovals } from './useApprovals';
import './approvals.css';

const approveLabel: Record<string, string> = {
  Void: 'approval.approveVoid',
  Gift: 'approval.approveGift',
  Discount: 'approval.approveDiscount',
  PriceChange: 'approval.approvePrice',
  Reprint: 'approval.approveReprint',
  Move: 'approval.approveMove',
  Merge: 'approval.approveMerge',
};

function ago(iso: string, now: number): string {
  const minutes = Math.max(0, Math.floor((now - Date.parse(iso)) / 60_000));
  return minutes === 0 ? t('approval.justNow') : t('approval.minutesAgo', { minutes });
}

/** Ticks every 15 seconds, so "1 min ago" stays true. */
function useNow(everyMs = 15_000) {
  const [now, setNow] = useState(() => Date.now());
  useEffect(() => {
    const timer = window.setInterval(() => setNow(Date.now()), everyMs);
    return () => window.clearInterval(timer);
  }, [everyMs]);
  return now;
}

/** Wireframe 6: the supervisor's counter. Requests wait here; approving does the action for the requester. */
export function ApprovalsPage() {
  const navigate = useNavigate();
  const queries = useQueryClient();
  const toast = useToast();
  const session = usePinSession();
  const now = useNow();
  const brand = useQuery({ queryKey: ['brand'], queryFn: () => api<Brand>('/api/brand') });
  const currency = brand.data?.currency ?? 'KES';
  const { waiting, mine } = usePendingApprovals();
  const decided = useQuery({
    queryKey: ['approvals', 'decided'],
    queryFn: () => api<ApprovalRequest[]>('/api/pos/approvals?status=decided'),
    staleTime: 0,
  });
  const [busy, setBusy] = useState<string | null>(null);
  const [rejecting, setRejecting] = useState<string | null>(null);
  const [note, setNote] = useState('');
  // The approver's own answer to "Was it already poured?", per void request (theirs is stored).
  const [poured, setPoured] = useState<Record<string, boolean>>({});
  const askPoured = usePosRules().returnOnVoidIfNotServed;

  const decide = async (request: ApprovalRequest, how: 'approve' | 'reject' | 'cancel') => {
    setBusy(request.id);
    try {
      await api(`/api/pos/approvals/${request.id}/${how}`, {
        method: 'POST',
        body:
          how === 'reject'
            ? { note: note.trim() || null }
            : how === 'approve' && request.kind === 'Void' && askPoured
              ? { wasPoured: poured[request.id] ?? request.wasPoured ?? false }
              : undefined,
      });
      setRejecting(null);
      setNote('');
    } catch (error) {
      toast(error instanceof ApiError ? error.message : t('common.somethingWrong'), {
        tone: 'warning',
      });
    } finally {
      setBusy(null);
      void queries.invalidateQueries({ queryKey: ['approvals'] });
    }
  };

  const user = session?.user;
  return (
    <div className="pos-screen approvals-screen">
      <TopBar
        start={
          <Button variant="ghost-dark" onClick={() => navigate('/pos/tables')}>
            {t('pos.order.tables')}
          </Button>
        }
        title={
          <span className="approvals-title">
            {t('approval.screenTitle')}
            {waiting.length > 0 && (
              <span className="approvals-badge">
                {t('approval.waitingCount', { count: waiting.length })}
              </span>
            )}
          </span>
        }
        end={
          user && (
            <span className="order-who">
              {t('pos.whoWhere', {
                terminal: user.terminalName ?? '',
                name: user.name,
                role: user.role,
              })}
            </span>
          )
        }
      />
      <OfflineBanner />
      <div className="approvals-body">
        <main className="approvals-main">
          <h2 className="approvals-heading">{t('approval.waitingForYou')}</h2>
          {waiting.length === 0 && (
            <p className="approvals-empty">{t('approval.nothingWaiting')}</p>
          )}
          <ul className="approvals-cards">
            {waiting.map((r) => (
              <li
                key={r.id}
                className={`approvals-card${r.sentToMe ? ' approvals-card--to-me' : ''}`}
              >
                <div className="approvals-card__text">
                  {r.sentToMe && <span className="approvals-to-me">{t('approval.sentToYou')}</span>}
                  <h3 className="approvals-card__title">
                    {r.amount != null
                      ? `${r.summary} · ${formatMoney(r.amount, currency)}`
                      : r.summary}
                  </h3>
                  <p className="approvals-card__meta">
                    {t('approval.cardMeta', {
                      where: r.where,
                      number: r.ticketNumber,
                      name: r.requestedBy,
                      terminal: r.terminal,
                      ago: ago(r.requestedAt, now),
                    })}
                  </p>
                  {r.reason && (
                    <p className="approvals-card__reason">
                      {t('approval.reason', { reason: r.reason })}
                    </p>
                  )}
                  {r.kind === 'Void' && askPoured && r.wasPoured != null && (
                    <div
                      className="approvals-card__poured"
                      role="group"
                      aria-label={t('pos.actions.poured')}
                    >
                      <span>{t('pos.actions.poured')}</span>
                      {[false, true].map((yes) => (
                        <button
                          key={String(yes)}
                          type="button"
                          className={`approvals-yesno${(poured[r.id] ?? r.wasPoured) === yes ? ' approvals-yesno--on' : ''}`}
                          aria-pressed={(poured[r.id] ?? r.wasPoured) === yes}
                          onClick={() => setPoured((p) => ({ ...p, [r.id]: yes }))}
                        >
                          {yes ? t('common.yes') : t('common.no')}
                        </button>
                      ))}
                    </div>
                  )}
                  {rejecting === r.id && (
                    <label className="approvals-card__note">
                      <span>{t('approval.rejectNote')}</span>
                      <input
                        value={note}
                        maxLength={200}
                        onChange={(e) => setNote(e.target.value)}
                        autoFocus
                      />
                    </label>
                  )}
                </div>
                <div className="approvals-card__actions">
                  {rejecting === r.id ? (
                    <>
                      <Button size="lg" onClick={() => setRejecting(null)}>
                        {t('common.cancel')}
                      </Button>
                      <Button
                        size="lg"
                        disabled={busy === r.id}
                        onClick={() => void decide(r, 'reject')}
                      >
                        {t('approval.confirmReject')}
                      </Button>
                    </>
                  ) : (
                    <>
                      <Button size="lg" disabled={busy === r.id} onClick={() => setRejecting(r.id)}>
                        {t('approval.reject')}
                      </Button>
                      <Button
                        variant="primary"
                        size="lg"
                        disabled={busy === r.id}
                        onClick={() => void decide(r, 'approve')}
                      >
                        {t(approveLabel[r.kind] ?? 'approval.approve')}
                      </Button>
                    </>
                  )}
                </div>
              </li>
            ))}
          </ul>

          {mine.length > 0 && (
            <>
              <h2 className="approvals-heading">{t('approval.yourRequests')}</h2>
              <ul className="approvals-cards">
                {mine.map((r) => (
                  <li key={r.id} className="approvals-card approvals-card--mine">
                    <div className="approvals-card__text">
                      <h3 className="approvals-card__title">{r.summary}</h3>
                      <p className="approvals-card__meta">
                        {r.where} · #{r.ticketNumber} · {ago(r.requestedAt, now)}
                      </p>
                    </div>
                    <div className="approvals-card__actions">
                      <Button
                        size="lg"
                        disabled={busy === r.id}
                        onClick={() => void decide(r, 'cancel')}
                      >
                        {t('approval.cancelRequest')}
                      </Button>
                    </div>
                  </li>
                ))}
              </ul>
            </>
          )}
          <p className="approvals-footnote">{t('approval.footnote')}</p>
        </main>

        <aside className="approvals-earlier" aria-labelledby="approvals-earlier">
          <h2 id="approvals-earlier" className="approvals-heading">
            {t('approval.earlier')}
          </h2>
          <ul>
            {(decided.data ?? []).map((r) => (
              <li key={r.id} className="approvals-earlier__item">
                <span className="approvals-earlier__top">
                  <span>{r.summary}</span>
                  <span className={`approvals-tag approvals-tag--${r.status.toLowerCase()}`}>
                    {t(`approval.status${r.status}`)}
                  </span>
                </span>
                <span className="approvals-earlier__meta">
                  {[r.where, r.requestedBy, r.status === 'Rejected' ? r.decisionNote : r.reason]
                    .filter(Boolean)
                    .join(' · ')}
                </span>
              </li>
            ))}
          </ul>
        </aside>
      </div>
    </div>
  );
}
