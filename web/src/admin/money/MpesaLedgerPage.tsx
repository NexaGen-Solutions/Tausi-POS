import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { Link } from 'react-router';
import { ApiError, api, apiUrl } from '../../shared/api/client';
import { formatAmount } from '../../shared/format/money';
import { t } from '../../shared/i18n/i18n';
import { Button, Field, Modal, SelectField, useToast } from '../../shared/ui';
import './money.css';

interface Tile {
  count: number;
  amount: number;
}

interface Use {
  ticketId: string;
  ticketNumber: number;
  receipt: number | null;
  cashier: string | null;
  amount: number;
  settled: boolean;
}

interface Row {
  id: string;
  at: string;
  code: string;
  payerName: string | null;
  phone: string | null;
  how: string;
  amount: number;
  available: number;
  status: 'Unapplied' | 'PartlyApplied' | 'Applied' | 'NotFromPos' | 'NotConfirmed';
  confirmed: boolean;
  neverConfirmed: boolean;
  uses: Use[];
  note: string | null;
  ticketId: string | null;
}

interface Ledger {
  period: string;
  tiles: { received: Tile; applied: Tile; unapplied: Tile; notFromPos: Tile; neverConfirmed: Tile };
  rows: Row[];
}

interface OpenTicket {
  id: string;
  number: number;
  where: string;
  remaining: number;
}

const QUICK = ['Today', 'Yesterday', 'ThisWeek', 'ThisMonth'] as const;
const STATUSES = ['', 'Unapplied', 'PartlyApplied', 'Applied', 'NotFromPos'] as const;
const time = (iso: string) =>
  new Date(iso).toLocaleTimeString('en-GB', { hour: '2-digit', minute: '2-digit' });

/**
 * Board 27: every M-Pesa payment in the dates, with tiles that add up to the rows. Apply to ticket
 * (ticket.settle), Mark as not from POS (cash.book, posted to Other income or Owner's capital),
 * Check on a typed code Safaricom never confirmed, Fetch payments now and Export to Excel.
 */
export function MpesaLedgerPage() {
  const queries = useQueryClient();
  const toast = useToast();
  const [quick, setQuick] = useState<(typeof QUICK)[number]>('Today');
  const [status, setStatus] = useState<(typeof STATUSES)[number]>('');
  const [problems, setProblems] = useState(false);
  const [applying, setApplying] = useState<Row | null>(null);
  const [marking, setMarking] = useState<Row | null>(null);
  const [busy, setBusy] = useState(false);
  const filters = { quick, status: status || null, onlyProblems: problems };
  const ledger = useQuery({
    queryKey: ['mpesa-ledger', filters],
    queryFn: () =>
      api<Ledger>('/api/admin/mpesa/ledger', { method: 'POST', body: filters, readOnly: true }),
  });
  const data = ledger.data;
  const refresh = () => queries.invalidateQueries({ queryKey: ['mpesa-ledger'] });

  const act = async (work: () => Promise<unknown>, done?: string) => {
    setBusy(true);
    try {
      await work();
      if (done) toast(done);
      await refresh();
      return true;
    } catch (e) {
      toast(e instanceof ApiError ? e.message : t('common.somethingWrong'), { tone: 'warning' });
      return false;
    } finally {
      setBusy(false);
    }
  };

  const exportExcel = async () => {
    const response = await fetch(apiUrl('/api/admin/mpesa/ledger/export'), {
      method: 'POST',
      credentials: 'include',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(filters),
    });
    if (!response.ok) return toast(t('common.somethingWrong'), { tone: 'warning' });
    const url = URL.createObjectURL(await response.blob());
    const a = document.createElement('a');
    a.href = url;
    a.download = `mpesa-${quick.toLowerCase()}.xlsx`;
    a.click();
    URL.revokeObjectURL(url);
  };

  const tile = (key: keyof Ledger['tiles'], warn = false) =>
    data && (
      <section
        className={`mpesa-tile${warn && data.tiles[key].count > 0 ? ' mpesa-tile--warn' : ''}`}
        aria-label={t(`admin.mpesaLedger.tiles.${key}`)}
      >
        <span className="mpesa-tile__label">{t(`admin.mpesaLedger.tiles.${key}`)}</span>
        <span className="mpesa-tile__amount">{formatAmount(data.tiles[key].amount)}</span>
        <span className="mpesa-tile__count">
          {t('admin.mpesaLedger.payments', { count: data.tiles[key].count })}
        </span>
      </section>
    );

  const statusCell = (r: Row) => {
    const label = r.neverConfirmed ? 'NeverConfirmed' : r.status;
    return (
      <>
        <span className={`mpesa-status-badge mpesa-status-badge--${label}`}>
          {t(`admin.mpesaLedger.status.${label}`)}
        </span>{' '}
        {r.uses.map((u) => (
          <span key={u.ticketId}>
            <Link to={`/admin/tickets/${u.ticketId}`}>#{u.ticketNumber}</Link>
            {u.receipt !== null && ` · ${t('admin.mpesaLedger.receipt', { number: u.receipt })}`}
            {u.cashier && ` · ${u.cashier}`}{' '}
          </span>
        ))}
        {r.note && <span className="mpesa-note">{r.note}</span>}
      </>
    );
  };

  return (
    <main className="money-page mpesa-ledger">
      <div className="mpesa-ledger__head">
        <h1 className="admin__title">{t('admin.mpesaLedger.title')}</h1>
        <div className="mpesa-ledger__filters">
          <SelectField
            label={t('admin.mpesaLedger.dates')}
            value={quick}
            onChange={(e) => setQuick(e.target.value as (typeof QUICK)[number])}
            options={QUICK.map((q) => ({ value: q, label: t(`admin.reports.quick.${q}`) }))}
          />
          <SelectField
            label={t('admin.mpesaLedger.statusFilter')}
            value={status}
            onChange={(e) => setStatus(e.target.value as (typeof STATUSES)[number])}
            options={STATUSES.map((s) => ({
              value: s,
              label: s ? t(`admin.mpesaLedger.status.${s}`) : t('admin.mpesaLedger.allStatuses'),
            }))}
          />
          <label className="mpesa-ledger__check">
            <input
              type="checkbox"
              checked={problems}
              onChange={(e) => setProblems(e.target.checked)}
            />
            {t('admin.mpesaLedger.onlyProblems')}
          </label>
          <Button
            disabled={busy}
            onClick={() =>
              void act(async () => {
                const r = await api<{ saved: number }>('/api/admin/mpesa/fetch', {
                  method: 'POST',
                });
                toast(t('admin.mpesaLedger.fetched', { count: r.saved }));
              })
            }
          >
            {t('admin.mpesaLedger.fetch')}
          </Button>
          <Button onClick={() => void exportExcel()}>{t('admin.mpesaLedger.export')}</Button>
        </div>
      </div>
      {data && (
        <>
          <div className="mpesa-tiles">
            {tile('received')}
            {tile('applied')}
            {tile('unapplied', true)}
            {tile('notFromPos')}
            {tile('neverConfirmed', true)}
          </div>
          <div className="mpesa-ledger__box">
            <table className="mpesa-ledger__table">
              <caption className="visually-hidden">{`${t('admin.mpesaLedger.title')} · ${data.period}`}</caption>
              <thead>
                <tr>
                  <th scope="col">{t('admin.mpesaLedger.col.time')}</th>
                  <th scope="col">{t('admin.mpesaLedger.col.code')}</th>
                  <th scope="col">{t('admin.mpesaLedger.col.from')}</th>
                  <th scope="col">{t('admin.mpesaLedger.col.how')}</th>
                  <th scope="col" className="mpesa-num">
                    {t('admin.mpesaLedger.col.amount')}
                  </th>
                  <th scope="col">{t('admin.mpesaLedger.col.status')}</th>
                  <th scope="col">
                    <span className="visually-hidden">{t('admin.mpesaLedger.col.actions')}</span>
                  </th>
                </tr>
              </thead>
              <tbody>
                {data.rows.map((r) => (
                  <tr key={r.id}>
                    <td className="mpesa-mono">{time(r.at)}</td>
                    <td className="mpesa-mono">{r.code || '—'}</td>
                    <td>{[r.payerName, r.phone].filter(Boolean).join(' · ') || '—'}</td>
                    <td>{r.how}</td>
                    <td className="mpesa-num mpesa-mono">{formatAmount(r.amount)}</td>
                    <td>{statusCell(r)}</td>
                    <td>
                      {r.confirmed && r.available > 0 && r.status !== 'NotFromPos' && (
                        <span className="mpesa-actions">
                          <Button onClick={() => setApplying(r)}>
                            {t('admin.mpesaLedger.apply')}
                          </Button>
                          <Button variant="link" onClick={() => setMarking(r)}>
                            {t('admin.mpesaLedger.notFromPos')}
                          </Button>
                        </span>
                      )}
                      {r.status === 'NotConfirmed' && r.ticketId && (
                        <Link
                          className="nx-btn nx-btn--secondary nx-btn--md"
                          to={`/admin/tickets/${r.ticketId}`}
                        >
                          {t('admin.mpesaLedger.check')}
                        </Link>
                      )}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
            {data.rows.length === 0 && (
              <p className="mpesa-empty">{t('admin.mpesaLedger.empty')}</p>
            )}
          </div>
          <p className="mpesa-ledger__note">{t('admin.mpesaLedger.note')}</p>
        </>
      )}
      {applying && (
        <ApplyDialog
          row={applying}
          busy={busy}
          onClose={() => setApplying(null)}
          onApply={(ticketId, amount) =>
            void act(
              () =>
                api(`/api/admin/mpesa/ledger/${applying.id}/apply`, {
                  method: 'POST',
                  body: { ticketId, amount },
                }),
              t('admin.mpesaLedger.applied'),
            ).then((ok) => ok && setApplying(null))
          }
        />
      )}
      {marking && (
        <NotFromPosDialog
          row={marking}
          busy={busy}
          onClose={() => setMarking(null)}
          onMark={(reason, account) =>
            void act(
              () =>
                api(`/api/admin/mpesa/ledger/${marking.id}/not-from-pos`, {
                  method: 'POST',
                  body: { reason, account },
                }),
              t('admin.mpesaLedger.marked'),
            ).then((ok) => ok && setMarking(null))
          }
        />
      )}
    </main>
  );
}

function ApplyDialog({
  row,
  busy,
  onClose,
  onApply,
}: {
  row: Row;
  busy: boolean;
  onClose: () => void;
  onApply: (ticketId: string, amount: number | null) => void;
}) {
  const open = useQuery({
    queryKey: ['mpesa-open-tickets'],
    queryFn: () => api<OpenTicket[]>('/api/admin/mpesa/ledger/open-tickets'),
  });
  const [ticketId, setTicketId] = useState('');
  const [amount, setAmount] = useState('');
  const tickets = open.data ?? [];
  return (
    <Modal
      title={t('admin.mpesaLedger.applyTitle', { code: row.code })}
      subtitle={t('admin.mpesaLedger.left', { amount: formatAmount(row.available) })}
      onClose={onClose}
      width={520}
    >
      {tickets.length === 0 ? (
        <p>{t('admin.mpesaLedger.noOpenTickets')}</p>
      ) : (
        <SelectField
          label={t('admin.mpesaLedger.ticket')}
          value={ticketId}
          onChange={(e) => setTicketId(e.target.value)}
          options={[
            { value: '', label: t('admin.mpesaLedger.chooseTicket') },
            ...tickets.map((k) => ({
              value: k.id,
              label: `#${k.number} · ${k.where} · ${formatAmount(k.remaining)}`,
            })),
          ]}
        />
      )}
      <Field
        label={t('admin.mpesaLedger.amountOptional')}
        inputMode="decimal"
        value={amount}
        onChange={(e) => setAmount(e.target.value.replace(/[^\d.]/g, ''))}
      />
      <p className="settings-panel__help">{t('admin.mpesaLedger.applyHelp')}</p>
      <div className="mpesa-dialog__buttons">
        <Button onClick={onClose}>{t('common.cancel')}</Button>
        <Button
          variant="primary"
          disabled={!ticketId || busy}
          onClick={() => onApply(ticketId, amount ? Number(amount) : null)}
        >
          {t('admin.mpesaLedger.apply')}
        </Button>
      </div>
    </Modal>
  );
}

function NotFromPosDialog({
  row,
  busy,
  onClose,
  onMark,
}: {
  row: Row;
  busy: boolean;
  onClose: () => void;
  onMark: (reason: string, account: string) => void;
}) {
  const [reason, setReason] = useState('');
  const [account, setAccount] = useState('OWNERS_CAPITAL');
  return (
    <Modal
      title={t('admin.mpesaLedger.notFromPosTitle', { code: row.code })}
      subtitle={formatAmount(row.available)}
      onClose={onClose}
      width={520}
    >
      <Field
        label={t('admin.mpesaLedger.reason')}
        value={reason}
        maxLength={300}
        onChange={(e) => setReason(e.target.value)}
      />
      <SelectField
        label={t('admin.mpesaLedger.account')}
        value={account}
        onChange={(e) => setAccount(e.target.value)}
        options={[
          { value: 'OWNERS_CAPITAL', label: t('admin.mpesaLedger.ownersCapital') },
          { value: 'OTHER_INCOME', label: t('admin.mpesaLedger.otherIncome') },
        ]}
      />
      <p className="settings-panel__help">{t('admin.mpesaLedger.notFromPosHelp')}</p>
      <div className="mpesa-dialog__buttons">
        <Button onClick={onClose}>{t('common.cancel')}</Button>
        <Button
          variant="primary"
          disabled={!reason.trim() || busy}
          onClick={() => onMark(reason.trim(), account)}
        >
          {t('admin.mpesaLedger.notFromPos')}
        </Button>
      </div>
    </Modal>
  );
}
