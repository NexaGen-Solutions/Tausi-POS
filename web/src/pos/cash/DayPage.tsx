import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { Link, useNavigate } from 'react-router';
import { ApiError, api } from '../../shared/api/client';
import type { Brand, Me } from '../../shared/api/types';
import { BackupBanner } from '../../shared/connection/BackupBanner';
import { OfflineBanner } from '../../shared/connection/OfflineBanner';
import { formatAmount, formatMoney, formatSigned } from '../../shared/format/money';
import { t } from '../../shared/i18n/i18n';
import { Button, Modal, TopBar, useToast } from '../../shared/ui';
import { usePinSession } from '../usePinSession';
import './cash.css';

interface DaySummary {
  tickets: number;
  gross: number;
  discounts: number;
  gifts: number;
  voids: number;
  net: number;
  vat: number;
  vatRates: { label: string; net: number; vat: number }[];
  payments: { type: string; count: number; amount: number }[];
  paymentsTotal: number;
  rounding: number;
  shiftDifferences: { cashier: string; terminal: string; difference: number }[];
  payOuts: number;
  /** The day's profit from the ledger (cash book daily summary); null when the work period spans several dates. */
  profit?: { net: number } | null;
  /** A work period over several dates: its sales (by date served) and takings (by date paid) per date. */
  dates?: { date: string; items: number; sales: number; vat: number; taken: number }[];
  /** "This work period covered 3 days (Sun 2 – Tue 4 Nov)."; null for one date. */
  spans?: string | null;
}

/** "Sun 2 Nov" from "2026-11-02". */
function shortDate(iso: string): string {
  const [y, m, d] = iso.split('-').map(Number);
  return new Date(y, m - 1, d).toLocaleDateString('en-GB', {
    weekday: 'short',
    day: 'numeric',
    month: 'short',
  });
}

interface DayStatus {
  /** M-Pesa payments from Safaricom not on a bill yet: a warning, never a block. */
  mpesa?: { count: number; amount: number } | null;
  /** 1.0.2: this user may close other cashiers' shifts (cash.close_others_shift). */
  canCloseShifts?: boolean;
  open: boolean;
  workPeriodId: string | null;
  openedAt: string | null;
  openedBy: string | null;
  blockers: {
    openTickets: {
      id: string;
      number: number;
      where: string;
      waiter: string;
      total: number;
      paid: number;
    }[];
    openShifts: { id: string; cashier: string; terminal: string; openedAt: string }[];
    pendingRequests: { id: string; summary: string; requestedBy: string }[];
    blocked: boolean;
  };
  summary: DaySummary | null;
}

const signed = (n: number) => (n === 0 ? '0' : `${n > 0 ? '+' : '−'} ${formatAmount(Math.abs(n))}`);

/** Closing the day (day.open_close): what blocks it, each with a link, then the day's summary and Close day. */
export function DayPage() {
  const navigate = useNavigate();
  const queries = useQueryClient();
  const toast = useToast();
  const session = usePinSession();
  const brand = useQuery({ queryKey: ['brand'], queryFn: () => api<Brand>('/api/brand') });
  const currency = brand.data?.currency ?? 'KES';
  const day = useQuery({
    queryKey: ['day'],
    queryFn: () => api<DayStatus>('/api/pos/day'),
    staleTime: 0,
  });
  const me = useQuery({
    queryKey: ['me'],
    queryFn: () => api<Me>('/api/auth/me'),
    enabled: !!session,
  });
  const canReport = me.data?.permissions['reports.all'] === 'Allowed';
  const [confirming, setConfirming] = useState(false);
  const [busy, setBusy] = useState(false);
  const status = day.data;
  const blockers = status?.blockers;
  const summary = status?.summary;

  const close = async () => {
    setBusy(true);
    try {
      const closed = await api<{ mpesaWarning?: string | null }>('/api/pos/day/close', {
        method: 'POST',
      });
      setConfirming(false);
      toast(t('pos.day.closedToast'));
      if (closed?.mpesaWarning) toast(closed.mpesaWarning, { tone: 'warning' });
      await queries.invalidateQueries();
    } catch (error) {
      toast(error instanceof ApiError ? error.message : t('common.somethingWrong'), {
        tone: 'warning',
      });
      void day.refetch();
    } finally {
      setBusy(false);
    }
  };

  /** The supervisors' Sales by user slip for the day being closed, on this terminal's receipt printer. */
  const printSalesByUser = async () => {
    try {
      await api('/api/pos/reports/sales-by-user/print', { method: 'POST' });
      toast(t('pos.day.salesByUserPrinting'));
    } catch (error) {
      toast(error instanceof ApiError ? error.message : t('common.somethingWrong'), {
        tone: 'warning',
      });
    }
  };

  const user = session?.user;
  return (
    <div className="pos-screen cash-screen">
      <TopBar
        start={
          <Button variant="ghost-dark" onClick={() => navigate('/pos/tables')}>
            {t('pos.order.tables')}
          </Button>
        }
        title={t('pos.day.title')}
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
      <BackupBanner admin={false} />
      {status && !status.open && (
        <main className="cash-body cash-body--start">
          <p className="cash-note">{t('pos.day.notOpen')}</p>
        </main>
      )}
      {status?.open && blockers && (
        <main className="cash-body cash-body--two">
          <section className="cash-panel" aria-label={t('pos.day.blockers')}>
            <h2 className="cash-heading">
              {blockers.blocked ? t('pos.day.blocked') : t('pos.day.nothingBlocks')}
            </h2>
            {blockers.openTickets.length > 0 && (
              <>
                <h3 className="cash-subheading">
                  {t('pos.day.openTickets', { count: blockers.openTickets.length })}
                </h3>
                <ul className="cash-list">
                  {blockers.openTickets.map((k) => (
                    <li key={k.id}>
                      <Link to={`/pos/ticket/${k.id}`}>
                        #{k.number} · {k.where} · {k.waiter} · {formatAmount(k.total)}
                        {k.paid > 0 && (
                          <span className="cash-warn"> · {t('pos.day.partPaid')}</span>
                        )}
                      </Link>
                    </li>
                  ))}
                </ul>
              </>
            )}
            {blockers.openShifts.length > 0 && (
              <>
                <h3 className="cash-subheading">
                  {t('pos.day.openShifts', { count: blockers.openShifts.length })}
                </h3>
                <ul className="cash-list">
                  {blockers.openShifts.map((s) => (
                    <li key={s.id} className="cash-shift-row">
                      <Link to="/pos/shift">
                        {s.cashier} · {s.terminal}
                      </Link>
                      {s.cashier === me.data?.user.name ? null : status.canCloseShifts ? (
                        <Button onClick={() => navigate(`/pos/shifts/${s.id}/close-other`)}>
                          {t('pos.closeOther.close')}
                        </Button>
                      ) : (
                        <span className="cash-note">{t('pos.closeOther.askSupervisor')}</span>
                      )}
                    </li>
                  ))}
                </ul>
              </>
            )}
            {blockers.pendingRequests.length > 0 && (
              <>
                <h3 className="cash-subheading">
                  {t('pos.day.pending', { count: blockers.pendingRequests.length })}
                </h3>
                <ul className="cash-list">
                  {blockers.pendingRequests.map((r) => (
                    <li key={r.id}>
                      <Link to="/pos/approvals">
                        {r.summary} · {r.requestedBy}
                      </Link>
                    </li>
                  ))}
                </ul>
                <p className="cash-note">{t('pos.day.pendingNote')}</p>
              </>
            )}
            {status.mpesa && (
              <p className="cash-note cash-note--warning" role="status">
                {status.mpesa.count === 1
                  ? t('pos.mpesa.closeDayWarningOne', {
                      amount: formatMoney(status.mpesa.amount, currency),
                    })
                  : t('pos.mpesa.closeDayWarning', {
                      count: status.mpesa.count,
                      amount: formatMoney(status.mpesa.amount, currency),
                    })}
                {' · '}
                <a href="/admin/mpesa">{t('pos.mpesa.closeDayLink')}</a>
              </p>
            )}
          </section>

          <section className="cash-panel" aria-label={t('pos.day.summary')}>
            <h2 className="cash-heading">{t('pos.day.summary')}</h2>
            {!summary ? (
              <p className="cash-note">{t('pos.day.summaryAfter')}</p>
            ) : (
              <>
                {summary.spans && (
                  <p className="cash-note cash-note--warning" role="note">
                    {summary.spans}
                  </p>
                )}
                <Line label={t('pos.day.tickets')} value={String(summary.tickets)} />
                <Line label={t('pos.day.gross')} value={formatAmount(summary.gross)} />
                <Line
                  label={t('pos.day.discounts')}
                  value={`− ${formatAmount(summary.discounts)}`}
                />
                <Line label={t('pos.day.gifts')} value={formatAmount(summary.gifts)} />
                <Line label={t('pos.day.voids')} value={formatAmount(summary.voids)} />
                {summary.vatRates.map((r) => (
                  <Line
                    key={r.label}
                    label={t('pos.day.netAndVat', { label: r.label })}
                    value={`${formatAmount(r.net)} + ${formatAmount(r.vat)}`}
                  />
                ))}
                {summary.payments.map((p) => (
                  <Line
                    key={p.type}
                    label={`${p.type} (${p.count})`}
                    value={formatAmount(p.amount)}
                  />
                ))}
                <Line label={t('pos.day.rounding')} value={signed(summary.rounding)} />
                <div className="cash-row cash-row--total">
                  <span>{t('pos.day.taken')}</span>
                  <span className="cash-amount">
                    {formatMoney(summary.paymentsTotal, currency)}
                  </span>
                </div>
                {summary.shiftDifferences.map((s) => (
                  <Line
                    key={`${s.cashier}-${s.terminal}`}
                    label={`${s.cashier} · ${s.terminal}`}
                    value={signed(s.difference)}
                  />
                ))}
                <Line label={t('pos.day.payOuts')} value={formatAmount(summary.payOuts)} />
                {summary.dates && summary.dates.length > 1 && (
                  <>
                    <h3 className="cash-subtitle">{t('pos.day.byDate')}</h3>
                    {summary.dates.map((d) => (
                      <Line
                        key={d.date}
                        label={shortDate(d.date)}
                        value={`${formatAmount(d.sales)} · ${formatAmount(d.taken)}`}
                      />
                    ))}
                  </>
                )}
                {summary.profit && (
                  <div className="cash-row cash-row--total">
                    <span>{t('pos.day.netForDay')}</span>
                    <span className="cash-amount">{formatSigned(summary.profit.net)}</span>
                  </div>
                )}
              </>
            )}
            {canReport && (
              <Button size="lg" block onClick={() => void printSalesByUser()}>
                {t('pos.day.printSalesByUser')}
              </Button>
            )}
            <Button
              variant="primary"
              size="lg"
              block
              disabled={!summary || busy}
              onClick={() => setConfirming(true)}
            >
              {t('pos.day.close')}
            </Button>
          </section>
        </main>
      )}
      {confirming && (
        <Modal title={t('pos.day.confirmTitle')} onClose={() => setConfirming(false)} width={480}>
          <p>{t('pos.day.confirmText')}</p>
          <div className="settle-confirm">
            <Button size="lg" onClick={() => setConfirming(false)}>
              {t('common.cancel')}
            </Button>
            <Button variant="primary" size="lg" disabled={busy} onClick={() => void close()}>
              {t('pos.day.close')}
            </Button>
          </div>
        </Modal>
      )}
    </div>
  );
}

function Line({ label, value }: { label: string; value: string }) {
  return (
    <div className="cash-row">
      <span>{label}</span>
      <span className="cash-amount">{value}</span>
    </div>
  );
}
