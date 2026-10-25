import { useQuery } from '@tanstack/react-query';
import { useState } from 'react';
import { Link, useNavigate, useParams } from 'react-router';
import { ApiError, api, apiFile } from '../../shared/api/client';
import { formatDayTime } from '../../shared/format/dates';
import { formatAmount, formatSigned } from '../../shared/format/money';
import { t } from '../../shared/i18n/i18n';
import { Button, Modal, Table, useToast, type Column } from '../../shared/ui';
import { formatBusinessDate } from '../money/dates';
import type {
  TicketJournalEntry,
  TicketLine,
  TicketPayment,
  TicketPrint,
  TicketReport,
  TimelineEvent,
} from './types';
import './reports.css';

const qty = (n: number) => n.toLocaleString('en-KE', { maximumFractionDigits: 3 });

/**
 * The ticket detail (read-only), opened from any ticket number in a report: the header, its
 * timeline, every line in every state, every print with what it printed, payments, requests,
 * stock and the ledger. "What happened on Table 7 last night?" starts here.
 */
export function TicketPage() {
  const { id } = useParams();
  const navigate = useNavigate();
  const toast = useToast();
  const detail = useQuery({
    queryKey: ['reports', 'ticket', id],
    queryFn: () => api<TicketReport>(`/api/admin/tickets/${id}`),
  });
  const [slip, setSlip] = useState<TicketPrint | null>(null);

  const pdf = async () => {
    try {
      const file = await apiFile(`/api/admin/tickets/${id}/pdf`);
      if (typeof URL.createObjectURL === 'function')
        window.open(URL.createObjectURL(file.blob), '_blank', 'noopener');
    } catch (e) {
      toast(e instanceof ApiError ? e.message : t('common.somethingWrong'), { tone: 'warning' });
    }
  };

  if (detail.error)
    return (
      <main className="reports">
        <h1 className="admin__title">{t('admin.ticket.title', { number: '' })}</h1>
        <p role="alert">
          {detail.error instanceof ApiError ? detail.error.message : t('common.somethingWrong')}
        </p>
      </main>
    );
  const d = detail.data;
  if (!d) return <main className="reports">{t('common.loading')}</main>;
  const k = d.ticket;

  const timeline: Column<TimelineEvent>[] = [
    { key: 'at', header: t('admin.ticket.time'), mono: true, render: (e) => formatDayTime(e.at) },
    {
      key: 'kind',
      header: t('admin.ticket.what'),
      render: (e) => (
        <span className={`ticket-event ticket-event--${e.kind.toLowerCase().replace(' ', '-')}`}>
          {e.kind}
        </span>
      ),
    },
    { key: 'text', header: t('admin.ticket.detail'), render: (e) => e.text },
    { key: 'by', header: t('admin.ticket.who'), render: (e) => e.by ?? '' },
  ];
  const lines: Column<TicketLine>[] = [
    {
      key: 'item',
      header: t('admin.ticket.item'),
      render: (l) => (
        <>
          {l.name}
          {l.options && <span className="report-muted"> ({l.options})</span>}
          {l.note && <span className="report-muted"> · {l.note}</span>}
          {l.priceChanges.map((p) => (
            <span key={p.at} className="report-muted ticket-sub">
              {t('admin.ticket.priceChange', {
                old: formatAmount(p.oldPrice ?? 0),
                new: formatAmount(p.newPrice ?? 0),
                by: p.by,
              })}
              {p.approvedBy && ` · ${t('admin.ticket.approvedBy', { name: p.approvedBy })}`}
            </span>
          ))}
          {l.voidReason && (
            <span className="report-muted ticket-sub">
              {t('admin.ticket.voided', { reason: l.voidReason, by: l.voidedBy ?? '' })}
            </span>
          )}
        </>
      ),
    },
    {
      key: 'qty',
      header: t('admin.ticket.qty'),
      align: 'right',
      mono: true,
      render: (l) => qty(l.quantity),
    },
    {
      key: 'price',
      header: t('admin.ticket.price'),
      align: 'right',
      mono: true,
      render: (l) => formatAmount(l.unitPrice),
    },
    {
      key: 'discount',
      header: t('admin.ticket.discount'),
      align: 'right',
      mono: true,
      render: (l) => (l.discount ? formatAmount(l.discount) : ''),
    },
    {
      key: 'amount',
      header: t('admin.ticket.amount'),
      align: 'right',
      mono: true,
      render: (l) => formatAmount(l.amount),
    },
    {
      key: 'status',
      header: t('admin.ticket.status'),
      render: (l) => (
        <span className={`ticket-status ticket-status--${l.status.toLowerCase()}`}>{l.status}</span>
      ),
    },
    { key: 'by', header: t('admin.ticket.addedBy'), render: (l) => `${l.addedBy} · ${l.terminal}` },
    {
      key: 'at',
      header: t('admin.ticket.added'),
      mono: true,
      render: (l) => formatDayTime(l.addedAt),
    },
    {
      key: 'round',
      header: t('admin.ticket.round'),
      align: 'right',
      mono: true,
      render: (l) => l.printRound ?? '',
    },
  ];
  const prints: Column<TicketPrint>[] = [
    { key: 'at', header: t('admin.ticket.time'), mono: true, render: (p) => formatDayTime(p.at) },
    { key: 'label', header: t('admin.ticket.slip'), render: (p) => p.label },
    { key: 'printer', header: t('admin.ticket.printer'), render: (p) => p.printer },
    { key: 'by', header: t('admin.ticket.who'), render: (p) => p.by ?? '' },
    {
      key: 'status',
      header: t('admin.ticket.status'),
      render: (p) => (p.error ? `${p.status} · ${p.error}` : p.status),
    },
    {
      key: 'view',
      header: '',
      render: (p) =>
        p.text ? (
          <button type="button" className="report-link" onClick={() => setSlip(p)}>
            {t('admin.ticket.viewSlip')}
          </button>
        ) : (
          <span className="report-muted">{t('admin.ticket.slipCleared')}</span>
        ),
    },
  ];
  const payments: Column<TicketPayment>[] = [
    { key: 'at', header: t('admin.ticket.time'), mono: true, render: (p) => formatDayTime(p.at) },
    { key: 'type', header: t('admin.ticket.type'), render: (p) => p.type },
    {
      key: 'amount',
      header: t('admin.ticket.amount'),
      align: 'right',
      mono: true,
      render: (p) => formatAmount(p.amount),
    },
    { key: 'ref', header: t('admin.ticket.code'), mono: true, render: (p) => p.reference ?? '' },
    {
      key: 'taken',
      header: t('admin.ticket.takenBy'),
      render: (p) => (p.shift ? `${p.takenBy} (${p.shift})` : p.takenBy),
    },
    { key: 'brought', header: t('admin.ticket.broughtBy'), render: (p) => p.broughtBy },
    {
      key: 'status',
      header: t('admin.ticket.status'),
      render: (p) => (p.removedBy ? `${p.status} · ${p.removedBy}` : p.status),
    },
  ];

  return (
    <main className="reports ticket-page">
      <div className="reports__header">
        <h1 className="admin__title">{t('admin.ticket.title', { number: k.number })}</h1>
        <div className="reports__buttons">
          <Button onClick={() => navigate(-1)}>{t('common.back')}</Button>
          <Button variant="primary" onClick={() => void pdf()}>
            {t('admin.reports.print')}
          </Button>
        </div>
      </div>
      <dl className="ticket-facts">
        <dt>{t('admin.ticket.status')}</dt>
        <dd>{k.status}</dd>
        <dt>{t('admin.ticket.receipt')}</dt>
        <dd className="mono">{k.receiptNumber ?? '-'}</dd>
        <dt>{t('admin.ticket.table')}</dt>
        <dd>{k.table ?? '-'}</dd>
        <dt>{t('admin.ticket.opened')}</dt>
        <dd>
          {t('admin.ticket.openedText', {
            name: k.openedBy,
            at: formatDayTime(k.openedAt),
            terminal: k.openedOn,
          })}
        </dd>
        <dt>{t('admin.ticket.settled')}</dt>
        <dd>
          {k.settledAt
            ? t('admin.ticket.openedText', {
                name: k.settledBy ?? '',
                at: formatDayTime(k.settledAt),
                terminal: k.settledOn ?? '',
              })
            : '-'}
        </dd>
        <dt>{t('admin.ticket.day')}</dt>
        <dd>
          {k.businessDate ? formatBusinessDate(k.businessDate) : formatDayTime(k.periodOpenedAt)}
        </dd>
        <dt>{t('admin.ticket.total')}</dt>
        <dd className="mono">
          {formatAmount(k.total)}
          {k.discount > 0 &&
            ` (${k.discountLabel ?? t('admin.ticket.discount')} ${formatAmount(k.discount)})`}
          {k.rounding !== 0 && ` · ${t('admin.ticket.rounding')} ${formatSigned(k.rounding)}`}
        </dd>
        {k.mergedIntoId && (
          <>
            <dt>{t('admin.ticket.mergedInto')}</dt>
            <dd>
              <Link to={`/admin/tickets/${k.mergedIntoId}`}>#{k.mergedIntoNumber}</Link>
            </dd>
          </>
        )}
        {k.mergedInNumbers.length > 0 && (
          <>
            <dt>{t('admin.ticket.mergedIn')}</dt>
            <dd>{k.mergedInNumbers.map((n) => `#${n}`).join(', ')}</dd>
          </>
        )}
        {k.note && (
          <>
            <dt>{t('admin.ticket.note')}</dt>
            <dd>{k.note}</dd>
          </>
        )}
      </dl>

      <Section title={t('admin.ticket.timeline')}>
        <Table
          caption={t('admin.ticket.timeline')}
          columns={timeline}
          rows={d.timeline}
          rowKey={(e) => `${e.at}-${e.kind}-${e.text}`}
        />
      </Section>
      <Section title={t('admin.ticket.lines')}>
        <Table
          caption={t('admin.ticket.lines')}
          columns={lines}
          rows={d.lines}
          rowKey={(l) => l.id}
          rowClassName={(l) =>
            l.status === 'Voided' || l.status === 'Cancelled' ? 'report-row--muted' : undefined
          }
        />
      </Section>
      <Section title={t('admin.ticket.prints')}>
        <Table
          caption={t('admin.ticket.prints')}
          columns={prints}
          rows={d.prints}
          rowKey={(p) => p.id}
          empty={t('admin.ticket.none')}
        />
      </Section>
      <Section title={t('admin.ticket.payments')}>
        <Table
          caption={t('admin.ticket.payments')}
          columns={payments}
          rows={d.payments}
          rowKey={(p) => p.id}
          empty={t('admin.ticket.none')}
        />
      </Section>
      {d.approvals.length > 0 && (
        <Section title={t('admin.ticket.requests')}>
          <ul className="ticket-list">
            {d.approvals.map((a) => (
              <li key={a.id}>
                <strong>{a.status}</strong> · {a.summary} ·{' '}
                {t('admin.ticket.askedBy', {
                  name: a.requestedBy,
                  at: formatDayTime(a.requestedAt),
                })}
                {a.decidedBy && ` · ${t('admin.ticket.decidedBy', { name: a.decidedBy })}`}
                {a.note && ` · ${a.note}`}
              </li>
            ))}
          </ul>
        </Section>
      )}
      {d.movements.length > 0 && (
        <Section title={t('admin.ticket.stock')}>
          <ul className="ticket-list">
            {d.movements.map((m, i) => (
              <li key={i}>
                {formatDayTime(m.at)} · {m.kind} · {m.item} ({m.location}){' '}
                <span className="mono">{m.quantityText}</span> ·{' '}
                <span className="mono">KSh {formatSigned(m.value)}</span>
              </li>
            ))}
          </ul>
        </Section>
      )}
      {d.journal.length > 0 && (
        <Section title={t('admin.ticket.ledger')}>
          {d.journal.map((e) => (
            <Journal key={e.id} entry={e} />
          ))}
        </Section>
      )}
      {slip && (
        <Modal
          title={slip.label}
          subtitle={`${slip.printer} · ${formatDayTime(slip.at)}`}
          onClose={() => setSlip(null)}
          width={460}
        >
          <pre className="ticket-slip">{slip.text}</pre>
          <div className="reports__dialog-buttons">
            <Button onClick={() => setSlip(null)}>{t('common.close')}</Button>
          </div>
        </Modal>
      )}
    </main>
  );
}

function Section({ title, children }: { title: string; children: React.ReactNode }) {
  return (
    <section className="ticket-section" aria-label={title}>
      <h2 className="report-table__title">{title}</h2>
      {children}
    </section>
  );
}

function Journal({ entry }: { entry: TicketJournalEntry }) {
  return (
    <div className="ticket-entry">
      <p className="ticket-entry__memo">
        {entry.memo} <span className="report-muted">· {formatDayTime(entry.at)}</span>
      </p>
      <ul className="ticket-list">
        {entry.lines.map((l, i) => (
          <li key={i} className="ticket-entry__line">
            <Link
              to={`/admin/accounts?account=${l.code}${entry.businessDate ? `&date=${entry.businessDate}` : ''}`}
            >
              {l.account}
            </Link>
            <span className="mono">
              {l.debit
                ? `${t('admin.accounts.debit')} ${formatAmount(l.debit)}`
                : `${t('admin.accounts.credit')} ${formatAmount(l.credit)}`}
            </span>
          </li>
        ))}
      </ul>
    </div>
  );
}
