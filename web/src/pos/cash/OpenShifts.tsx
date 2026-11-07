import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useEffect, useState } from 'react';
import { useNavigate, useParams } from 'react-router';
import { api } from '../../shared/api/client';
import { onPosEvent } from '../../shared/connection/hub';
import { formatAmount, formatSigned } from '../../shared/format/money';
import { t } from '../../shared/i18n/i18n';
import { Button, Modal, TopBar } from '../../shared/ui';
import { CloseOthersShift } from './CloseOthersShift';
import { openShiftsKey, type OpenShiftRow, type ShiftNotice } from './types';
import './cash.css';

const when = (iso: string) =>
  new Date(iso).toLocaleString('en-GB', { weekday: 'short', hour: '2-digit', minute: '2-digit' });

/** Every open shift, each with Close this shift (users with cash.close_others_shift; their own shift is left out). */
export function OpenShiftsList({
  myId,
  closeHref,
}: {
  myId: string | null;
  closeHref: (id: string) => string;
}) {
  const navigate = useNavigate();
  const list = useQuery({
    queryKey: openShiftsKey,
    queryFn: () => api<OpenShiftRow[]>('/api/pos/shifts/open'),
    staleTime: 0,
  });
  const rows = (list.data ?? []).filter((s) => s.cashierId !== myId);
  if (list.data && rows.length === 0)
    return <p className="cash-note">{t('pos.closeOther.noneOpen')}</p>;
  return (
    <table className="cash-table">
      <thead>
        <tr>
          <th scope="col">{t('pos.closeOther.cashier')}</th>
          <th scope="col">{t('pos.closeOther.terminal')}</th>
          <th scope="col">{t('pos.closeOther.openedAt')}</th>
          <th scope="col">{t('pos.closeOther.cashTaken')}</th>
          <th scope="col">
            <span className="visually-hidden">{t('pos.closeOther.close')}</span>
          </th>
        </tr>
      </thead>
      <tbody>
        {rows.map((s) => (
          <tr key={s.id}>
            <td>
              {s.cashier}
              {s.closingBy && (
                <span className="cash-note">
                  {' '}
                  · {t('pos.closeOther.closingBy', { name: s.closingBy })}
                </span>
              )}
            </td>
            <td>{s.terminal}</td>
            <td className="cash-amount">{when(s.openedAt)}</td>
            <td className="cash-amount">{formatAmount(s.cashTaken)}</td>
            <td>
              <Button onClick={() => navigate(closeHref(s.id))}>{t('pos.closeOther.close')}</Button>
            </td>
          </tr>
        ))}
      </tbody>
    </table>
  );
}

/** The terminal's Open shifts screen (from My shift). */
export function OpenShiftsPage() {
  const navigate = useNavigate();
  const me = useQuery({
    queryKey: ['me'],
    queryFn: () => api<{ user: { id: string } }>('/api/auth/me'),
  });
  return (
    <div className="pos-screen cash-screen">
      <TopBar
        start={
          <Button variant="ghost-dark" onClick={() => navigate('/pos/shift')}>
            {t('pos.closeOther.backToShift')}
          </Button>
        }
        title={t('pos.closeOther.openShifts')}
      />
      <main className="cash-body cash-body--start">
        <section className="cash-panel">
          <OpenShiftsList
            myId={me.data?.user.id ?? null}
            closeHref={(id) => `/pos/shifts/${id}/close-other`}
          />
        </section>
      </main>
    </div>
  );
}

/** The terminal's screen for closing one other cashier's shift. */
export function CloseOthersShiftPage() {
  const { id = '' } = useParams();
  const navigate = useNavigate();
  return (
    <div className="pos-screen cash-screen">
      <TopBar
        start={
          <Button variant="ghost-dark" onClick={() => navigate(-1)}>
            {t('common.back')}
          </Button>
        }
        title={t('pos.closeOther.screenTitle')}
      />
      <main className="cash-body cash-body--start">
        <CloseOthersShift shiftId={id} canMove onDone={() => navigate('/pos/day')} />
      </main>
    </div>
  );
}

/**
 * The notice a cashier sees at their next login when someone else closed their shift:
 * "Your shift on Counter A was closed by SAM on Sun 23:40. Difference: −100." OK dismisses it.
 */
export function ShiftNoticeGate() {
  const queries = useQueryClient();
  const notices = useQuery({
    queryKey: ['shift-notices'],
    queryFn: () => api<ShiftNotice[]>('/api/pos/shift-notices'),
  });
  const [busy, setBusy] = useState(false);
  useEffect(() => onPosEvent('reconnected', () => void notices.refetch()), [notices]);
  const notice = notices.data?.[0];
  if (!notice) return null;
  const ok = async () => {
    setBusy(true);
    try {
      await api(`/api/pos/shift-notices/${notice.shiftId}/ok`, { method: 'POST' });
    } finally {
      setBusy(false);
      void queries.invalidateQueries({ queryKey: ['shift-notices'] });
    }
  };
  return (
    <Modal title={t('pos.closeOther.noticeTitle')}>
      <p>
        {t('pos.closeOther.notice', {
          terminal: notice.terminal,
          name: notice.closedBy,
          at: when(notice.closedAt),
          difference: formatSigned(notice.difference),
        })}
      </p>
      {notice.reason && (
        <p className="cash-note">{t('pos.closeOther.noticeReason', { reason: notice.reason })}</p>
      )}
      <Button variant="primary" size="lg" block disabled={busy} onClick={() => void ok()}>
        {t('pos.closeOther.ok')}
      </Button>
    </Modal>
  );
}
