import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { ApiError, api } from '../../shared/api/client';
import { formatAmount, formatSigned } from '../../shared/format/money';
import { t } from '../../shared/i18n/i18n';
import { Button, useToast } from '../../shared/ui';
import { CashCountForm } from './CashCountForm';
import {
  CLOSE_REASONS,
  countTotal,
  openShiftsKey,
  type CashCount,
  type PendingPayment,
  type Shift,
} from './types';

const when = (iso: string) =>
  new Date(iso).toLocaleString('en-GB', { weekday: 'short', hour: '2-digit', minute: '2-digit' });

/**
 * Task 19: closing a shift its cashier left open (cash.close_others_shift). The reason, any pending
 * payments (moved to my shift or removed), the count by denomination (blind if the setting says so),
 * then the sign-off: another supervisor's PIN on the approval pad, or "I counted this cash alone".
 */
export function CloseOthersShift({
  shiftId,
  onDone,
  canMove,
}: {
  shiftId: string;
  onDone: () => void;
  /** On a terminal: pending payments can move to the closer's own shift here. */
  canMove: boolean;
}) {
  const queries = useQueryClient();
  const toast = useToast();
  const key = ['shift-other', shiftId];
  const view = useQuery({
    queryKey: key,
    queryFn: () => api<Shift>(`/api/pos/shifts/${shiftId}/other`),
  });
  const pendingQuery = useQuery({
    queryKey: [...key, 'pending'],
    queryFn: async () =>
      (await api<{ id: string; pending: PendingPayment[] }[]>('/api/pos/shifts/open')).find(
        (s) => s.id === shiftId,
      )?.pending ?? [],
  });
  const [reason, setReason] = useState<string>('');
  const [note, setNote] = useState('');
  const [count, setCount] = useState<CashCount>({ notes: {}, coins: 0 });
  const [alone, setAlone] = useState(false);
  const [busy, setBusy] = useState(false);
  const shift = view.data;
  const pending = pendingQuery.data ?? [];
  const counted = !!shift?.countSubmitted;

  const run = async (work: () => Promise<void>) => {
    setBusy(true);
    try {
      await work();
    } catch (e) {
      toast(e instanceof ApiError ? e.message : t('common.somethingWrong'), { tone: 'warning' });
    } finally {
      setBusy(false);
    }
  };

  const submitCount = () =>
    run(async () => {
      const result = await api<Shift>(`/api/pos/shifts/${shiftId}/other/count`, {
        method: 'POST',
        body: { count, reason, note: note.trim() || null },
      });
      queries.setQueryData(key, result);
    });
  const pendingAction = (what: 'move' | 'remove') =>
    run(async () => {
      const left = await api<PendingPayment[]>(`/api/pos/shifts/${shiftId}/other/pending/${what}`, {
        method: 'POST',
      });
      queries.setQueryData([...key, 'pending'], left);
    });
  // Without the tick box the server asks for another supervisor's PIN (the approval pad).
  const close = () =>
    run(async () => {
      await api<Shift>(`/api/pos/shifts/${shiftId}/other/close`, {
        method: 'POST',
        body: { countedAlone: alone },
      });
      toast(t('pos.closeOther.closed', { name: shift?.cashier ?? '' }));
      void queries.invalidateQueries({ queryKey: openShiftsKey });
      void queries.invalidateQueries({ queryKey: ['day'] });
      onDone();
    });

  if (!shift) return null;
  const reasonOk = reason !== '' && (reason !== 'Other' || note.trim() !== '');

  return (
    <div className="close-other">
      <h2 className="cash-heading">
        {t('pos.closeOther.title', {
          name: shift.cashier,
          terminal: shift.terminal,
          opened: when(shift.openedAt),
        })}
      </h2>
      <p className="cash-note cash-note--warning" role="note">
        {t('pos.closeOther.warning')}
      </p>

      {pending.length > 0 && (
        <section className="cash-panel" aria-label={t('pos.closeOther.pendingTitle')}>
          <h3 className="cash-subheading">{t('pos.closeOther.pendingTitle')}</h3>
          <ul className="cash-list">
            {pending.map((p) => (
              <li key={p.id}>
                #{p.ticketNumber} · {t(`pos.settle.type.${p.type}`)} · {formatAmount(p.amount)}
              </li>
            ))}
          </ul>
          <p className="cash-note">{t('pos.closeOther.pendingHelp')}</p>
          <div className="cash-actions">
            <Button disabled={busy || !canMove} onClick={() => void pendingAction('move')}>
              {t('pos.closeOther.move')}
            </Button>
            <Button variant="danger" disabled={busy} onClick={() => void pendingAction('remove')}>
              {t('pos.closeOther.remove')}
            </Button>
          </div>
          {!canMove && <p className="cash-note">{t('pos.closeOther.moveOnTerminal')}</p>}
        </section>
      )}

      {!counted ? (
        <section className="cash-panel" aria-label={t('pos.closeOther.countTitle')}>
          <label className="cash-field">
            <span>{t('pos.closeOther.reason')}</span>
            <select value={reason} onChange={(e) => setReason(e.target.value)} disabled={busy}>
              <option value="">{t('pos.closeOther.chooseReason')}</option>
              {CLOSE_REASONS.map((r) => (
                <option key={r} value={r}>
                  {t(`pos.closeOther.reasons.${CLOSE_REASONS.indexOf(r)}`)}
                </option>
              ))}
            </select>
          </label>
          {reason === 'Other' && (
            <label className="cash-field">
              <span>{t('pos.closeOther.note')}</span>
              <input
                value={note}
                maxLength={200}
                onChange={(e) => setNote(e.target.value)}
                disabled={busy}
              />
            </label>
          )}
          <h3 className="cash-subheading">{t('pos.closeOther.countTitle')}</h3>
          {shift.expected === null && (
            <p className="cash-note cash-blind">{t('pos.cash.shownAfterCount')}</p>
          )}
          <CashCountForm
            denominations={shift.denominations}
            value={count}
            onChange={setCount}
            disabled={busy}
            totalLabel={t('pos.cash.counted')}
          />
          <Button
            variant="primary"
            size="lg"
            block
            disabled={busy || !reasonOk}
            onClick={() => void submitCount()}
          >
            {t('pos.closeOther.submitCount', { amount: formatAmount(countTotal(count)) })}
          </Button>
        </section>
      ) : (
        <section className="cash-panel" aria-label={t('pos.closeOther.signOff')}>
          <div className="cash-row">
            <span>{t('pos.cash.expected')}</span>
            <span className="cash-amount">{formatAmount(shift.expected ?? 0)}</span>
          </div>
          <div className="cash-row">
            <span>{t('pos.cash.counted')}</span>
            <span className="cash-amount">{formatAmount(shift.counted ?? 0)}</span>
          </div>
          <div className="cash-row cash-row--total">
            <span>{t('pos.cash.difference')}</span>
            <span className={`cash-amount${(shift.difference ?? 0) !== 0 ? ' cash-warn' : ''}`}>
              {formatSigned(shift.difference ?? 0)}
            </span>
          </div>
          <p className="cash-note">{t('pos.closeOther.signOffHelp')}</p>
          <label className="close-other__alone">
            <input type="checkbox" checked={alone} onChange={(e) => setAlone(e.target.checked)} />
            {t('pos.closeOther.alone')}
          </label>
          <Button
            variant="primary"
            size="lg"
            block
            disabled={busy || pending.length > 0}
            onClick={() => void close()}
          >
            {alone ? t('pos.closeOther.closeAlone') : t('pos.closeOther.closeWithPin')}
          </Button>
        </section>
      )}
    </div>
  );
}
