import { useQuery } from '@tanstack/react-query';
import { useState } from 'react';
import { useNavigate } from 'react-router';
import { cellText } from '../../admin/reports/format';
import type {
  ReportGroup,
  ReportOptions,
  ReportResult,
  ReportRow,
  ReportTable,
} from '../../admin/reports/types';
import { ApiError, api } from '../../shared/api/client';
import type { Me } from '../../shared/api/types';
import { OfflineBanner } from '../../shared/connection/OfflineBanner';
import { formatSigned } from '../../shared/format/money';
import { t } from '../../shared/i18n/i18n';
import { Button, Modal, SelectField, TopBar, useToast } from '../../shared/ui';
import { Calendar } from '../../shared/ui/Calendar';
import { isoDate, type DateRange } from '../../shared/ui/dates';
import {
  Quick,
  ReportKeys,
  headline,
  type ReportKey,
  type TerminalFilters,
  type TerminalReport,
} from './reportFigures';
import './reports.css';

function Rows({
  rows,
  table,
  className,
}: {
  rows: ReportRow[];
  table: ReportTable;
  className?: string;
}) {
  return (
    <>
      {rows.map((row, i) => (
        <tr
          key={i}
          className={
            [className, row.muted ? 'pos-report__row--muted' : ''].filter(Boolean).join(' ') ||
            undefined
          }
        >
          {table.columns.map((column, c) => (
            <td key={column.key} className={column.rightAligned ? 'pos-report__number' : undefined}>
              {cellText(row.cells[c] ?? null, column)}
            </td>
          ))}
        </tr>
      ))}
    </>
  );
}

/** One group: its header (a button when its items fold away, as on Sales by user), rows, subtotal and footer. */
function Group({
  group,
  table,
  folds,
}: {
  group: ReportGroup;
  table: ReportTable;
  folds: boolean;
}) {
  const [open, setOpen] = useState(!folds);
  const span = table.columns.length;
  const title = group.subtitle ? `${group.title} (${group.subtitle})` : group.title;
  return (
    <tbody>
      {group.title !== null && (
        <tr className="pos-report__group">
          <th scope="rowgroup" colSpan={span - 1}>
            {folds ? (
              <button
                type="button"
                className="pos-report__fold"
                aria-expanded={open}
                onClick={() => setOpen(!open)}
              >
                <span className="pos-report__fold-name">{title}</span>
                <span className="pos-report__fold-hint">
                  {open ? t('pos.reportsPage.hideItems') : t('pos.reportsPage.showItems')}
                </span>
              </button>
            ) : (
              title
            )}
          </th>
          <th className="pos-report__number">
            {group.total !== null ? formatSigned(group.total) : ''}
          </th>
        </tr>
      )}
      {open && <Rows rows={group.rows} table={table} />}
      {group.subtotal && (
        <Rows rows={[group.subtotal]} table={table} className="pos-report__subtotal" />
      )}
      {group.footer?.map((line) => (
        <tr key={line} className="pos-report__footer">
          <td colSpan={span}>{line}</td>
        </tr>
      ))}
    </tbody>
  );
}

function Result({ reportKey, result }: { reportKey: ReportKey; result: ReportResult }) {
  return (
    <>
      {result.notes.length > 0 && (
        <ul className="pos-report__notes">
          {result.notes.map((note) => (
            <li key={note}>{note}</li>
          ))}
        </ul>
      )}
      {result.isEmpty ? (
        <p className="pos-report__empty" role="status">
          {t('pos.reportsPage.empty')}
        </p>
      ) : (
        result.tables.map((table, i) => (
          <table key={i} className="pos-report__table">
            <caption className="visually-hidden">{table.title ?? result.title}</caption>
            <thead>
              <tr>
                {table.columns.map((column) => (
                  <th
                    key={column.key}
                    scope="col"
                    className={column.rightAligned ? 'pos-report__number' : undefined}
                  >
                    {column.label}
                  </th>
                ))}
              </tr>
            </thead>
            {table.groups.map((group, g) => (
              <Group
                key={`${group.title}-${g}`}
                group={group}
                table={table}
                folds={reportKey === 'sales-by-user'}
              />
            ))}
            {table.totals && (
              <tfoot>
                <Rows rows={[table.totals]} table={table} className="pos-report__total" />
              </tfoot>
            )}
          </table>
        ))
      )}
    </>
  );
}

/**
 * Task 17b: Sales by user, Daily sales and Sales by item on the terminal, revenue only. With only
 * reports.own the server returns the user's own figures, whatever is asked; the user and terminal
 * filters show for reports.all.
 */
export function PosReportsPage({ today = () => new Date() }: { today?: () => Date }) {
  const navigate = useNavigate();
  const toast = useToast();
  const me = useQuery({ queryKey: ['me'], queryFn: () => api<Me>('/api/auth/me') });
  const canAll = me.data?.permissions['reports.all'] === 'Allowed';
  const [key, setKey] = useState<ReportKey>('sales-by-user');
  const [filters, setFilters] = useState<TerminalFilters>({
    quick: 'Today',
    from: null,
    to: null,
    userId: null,
    terminalId: null,
  });
  const [picking, setPicking] = useState<DateRange | null>(null);
  const [busy, setBusy] = useState(false);

  const options = useQuery({
    queryKey: ['report-options'],
    queryFn: () => api<ReportOptions>('/api/admin/reports/options'),
    enabled: canAll,
  });
  const report = useQuery({
    queryKey: ['pos-report', key, filters],
    queryFn: () =>
      api<TerminalReport>(`/api/pos/reports/${key}/run`, {
        method: 'POST',
        body: filters,
        readOnly: true,
      }),
    staleTime: 0,
  });
  const data = report.data;

  const print = async () => {
    setBusy(true);
    try {
      await api(`/api/pos/reports/${key}/slip`, { method: 'POST', body: filters });
      toast(t('pos.reportsPage.printed'));
    } catch (error) {
      toast(error instanceof ApiError ? error.message : t('common.somethingWrong'), {
        tone: 'warning',
      });
    } finally {
      setBusy(false);
    }
  };

  const max = isoDate(today());
  const set = (change: Partial<TerminalFilters>) => setFilters((f) => ({ ...f, ...change }));

  return (
    <div className="pos-screen pos-reports">
      <TopBar
        start={
          <Button variant="ghost-dark" onClick={() => navigate('/pos/tables')}>
            {t('pos.order.tables')}
          </Button>
        }
        title={t('pos.reportsPage.title')}
        end={
          data?.ownOnly ? <span className="order-who">{t('pos.reportsPage.ownOnly')}</span> : null
        }
      />
      <OfflineBanner />
      <div className="pos-reports__bar">
        <div className="pos-reports__tabs" role="group" aria-label={t('pos.reportsPage.tabs')}>
          {ReportKeys.map((k) => (
            <button
              key={k}
              type="button"
              className={`pos-reports__tab${k === key ? ' pos-reports__tab--active' : ''}`}
              aria-pressed={k === key}
              onClick={() => setKey(k)}
            >
              {t(`pos.reportsPage.report.${k}`)}
            </button>
          ))}
        </div>
        <Button variant="primary" size="lg" disabled={busy || !data} onClick={() => void print()}>
          {t('pos.reportsPage.print')}
        </Button>
      </div>
      <div className="pos-reports__filters">
        <div className="pos-reports__dates" role="group" aria-label={t('pos.reportsPage.dates')}>
          {Quick.map((q) => (
            <button
              key={q}
              type="button"
              className={`pos-reports__date${filters.quick === q ? ' pos-reports__date--active' : ''}`}
              aria-pressed={filters.quick === q}
              onClick={() => set({ quick: q, from: null, to: null })}
            >
              {t(`pos.reportsPage.${q}`)}
            </button>
          ))}
          <button
            type="button"
            className={`pos-reports__date${filters.quick === null ? ' pos-reports__date--active' : ''}`}
            aria-pressed={filters.quick === null}
            onClick={() =>
              setPicking(
                filters.from && filters.to
                  ? { from: filters.from, to: filters.to }
                  : { from: max, to: max },
              )
            }
          >
            {filters.quick === null && filters.from
              ? (data?.result.period ?? t('pos.reportsPage.pick'))
              : t('pos.reportsPage.pick')}
          </button>
        </div>
        {canAll && (
          <>
            <SelectField
              className="pos-reports__select"
              label={t('pos.reportsPage.user')}
              value={filters.userId ?? ''}
              onChange={(e) => set({ userId: e.target.value || null })}
              options={[
                { value: '', label: t('pos.reportsPage.everyone') },
                ...(options.data?.users ?? []).map((u) => ({ value: u.id, label: u.name })),
              ]}
            />
            <SelectField
              className="pos-reports__select"
              label={t('pos.reportsPage.terminal')}
              value={filters.terminalId ?? ''}
              onChange={(e) => set({ terminalId: e.target.value || null })}
              options={[
                { value: '', label: t('pos.reportsPage.everyTerminal') },
                ...(options.data?.terminals ?? []).map((u) => ({ value: u.id, label: u.name })),
              ]}
            />
          </>
        )}
      </div>
      {data && (
        <main className="pos-reports__body">
          <p className="pos-reports__period">
            {[data.result.period, ...data.result.filterText].join(' · ')}
          </p>
          <div className="pos-reports__figures">
            {headline(key, data.result).map((f) => (
              <section key={f.label} className="pos-reports__figure">
                <span className="pos-reports__figure-label">{f.label}</span>
                <span className="pos-reports__figure-value">{f.value}</span>
              </section>
            ))}
          </div>
          <div className="pos-reports__scroll">
            <Result reportKey={key} result={data.result} />
          </div>
        </main>
      )}
      {report.error && (
        <p className="pos-reports__error" role="alert">
          {report.error instanceof ApiError ? report.error.message : t('common.somethingWrong')}
        </p>
      )}
      {picking && (
        <Modal title={t('pos.reportsPage.pickTitle')} onClose={() => setPicking(null)} width={460}>
          <Calendar value={picking} onChange={setPicking} max={max} />
          <div className="pos-reports__pick-actions">
            <Button variant="secondary" size="lg" onClick={() => setPicking(null)}>
              {t('common.cancel')}
            </Button>
            <Button
              variant="primary"
              size="lg"
              onClick={() => {
                set({ quick: null, from: picking.from, to: picking.to });
                setPicking(null);
              }}
            >
              {t('pos.reportsPage.use')}
            </Button>
          </div>
        </Modal>
      )}
    </div>
  );
}
