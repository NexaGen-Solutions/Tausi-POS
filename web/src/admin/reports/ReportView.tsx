import { useState, type ReactNode } from 'react';
import { Link } from 'react-router';
import { formatSigned } from '../../shared/format/money';
import { t } from '../../shared/i18n/i18n';
import { cellText, GroupPreview } from './format';
import type {
  Cell,
  ReportColumn,
  ReportGroup,
  ReportResult,
  ReportRow,
  ReportTable,
} from './types';

function CellView({
  value,
  column,
  ticketId,
}: {
  value: Cell;
  column: ReportColumn;
  ticketId: string | null;
}) {
  const text = cellText(value, column);
  if (column.type === 'Ticket' && ticketId && text)
    return (
      <Link className="report-ticket" to={`/admin/tickets/${ticketId}`}>
        {text}
      </Link>
    );
  // The audit log's before and after values: long JSON, shown on request.
  if ((column.key === 'before' || column.key === 'after') && text.length > 40)
    return (
      <details className="report-json">
        <summary>{t('admin.reports.show')}</summary>
        <pre>{text}</pre>
      </details>
    );
  return <>{text}</>;
}

function Row({
  row,
  columns,
  className,
}: {
  row: ReportRow;
  columns: ReportColumn[];
  className?: string;
}) {
  return (
    <tr
      className={
        [className, row.muted ? 'report-row--muted' : ''].filter(Boolean).join(' ') || undefined
      }
    >
      {columns.map((column, i) => (
        <td key={column.key} className={column.rightAligned ? 'report-cell--number' : undefined}>
          <CellView value={row.cells[i] ?? null} column={column} ticketId={row.ticketId} />
        </td>
      ))}
    </tr>
  );
}

/** The grey header row: the group's name, its subtitle (a role) under the item column, and its total on the right. */
function GroupHeader({ group, columns }: { group: ReportGroup; columns: ReportColumn[] }) {
  const subtitleAt = Math.min(2, columns.length - 2);
  const cells: ReactNode[] = columns.map(() => null);
  cells[0] = group.title;
  if (group.subtitle && subtitleAt > 0) cells[subtitleAt] = group.subtitle;
  else if (group.subtitle) cells[0] = `${group.title} · ${group.subtitle}`;
  if (group.total !== null && columns.length > 1)
    cells[columns.length - 1] = formatSigned(group.total);
  return (
    <tr className="report-group">
      {columns.map((column, i) => (
        <th
          key={column.key}
          scope={i === 0 ? 'rowgroup' : undefined}
          className={i === columns.length - 1 ? 'report-cell--number' : undefined}
        >
          {cells[i]}
        </th>
      ))}
    </tr>
  );
}

function Group({ group, columns }: { group: ReportGroup; columns: ReportColumn[] }) {
  const [open, setOpen] = useState(false);
  const shown = open ? group.rows : group.rows.slice(0, GroupPreview);
  const hidden = group.rows.length - shown.length;
  return (
    <tbody>
      {group.title !== null && <GroupHeader group={group} columns={columns} />}
      {shown.map((row, i) => (
        <Row key={i} row={row} columns={columns} />
      ))}
      {hidden > 0 && (
        <tr className="report-more">
          <td colSpan={columns.length}>
            <button type="button" className="report-more__button" onClick={() => setOpen(true)}>
              {t('admin.reports.more', { count: hidden })}
            </button>
          </td>
        </tr>
      )}
      {group.subtotal && (
        <Row row={group.subtotal} columns={columns} className="report-row--total" />
      )}
      {group.footer?.map((line) => (
        <tr key={line} className="report-footer">
          <td colSpan={columns.length}>{line}</td>
        </tr>
      ))}
    </tbody>
  );
}

function Table({ table, caption }: { table: ReportTable; caption: string }) {
  return (
    <section className="report-table" aria-label={table.title ?? caption}>
      {table.title && <h2 className="report-table__title">{table.title}</h2>}
      <div className="report-box">
        <table className="report">
          <caption className="visually-hidden">{table.title ?? caption}</caption>
          <thead>
            <tr>
              {table.columns.map((column) => (
                <th
                  key={column.key}
                  scope="col"
                  className={column.rightAligned ? 'report-cell--number' : undefined}
                >
                  {column.label}
                </th>
              ))}
            </tr>
          </thead>
          {table.groups.map((group, i) => (
            <Group key={`${group.title}-${i}`} group={group} columns={table.columns} />
          ))}
          {table.totals && (
            <tfoot>
              <Row row={table.totals} columns={table.columns} className="report-row--grand" />
            </tfoot>
          )}
        </table>
        {table.groups.every((g) => g.rows.length === 0) && (
          <p className="report-empty">{t('admin.reports.emptyTable')}</p>
        )}
      </div>
      {table.footer?.map((line) => (
        <p key={line} className="report-note">
          {line}
        </p>
      ))}
    </section>
  );
}

/** A report's result: its notes, then each table. */
export function ReportView({ result }: { result: ReportResult }) {
  if (result.isEmpty && result.tables.length <= 1)
    return (
      <div className="report-empty-state" role="status">
        <p>{t('admin.reports.empty')}</p>
        {result.notes.map((note) => (
          <p key={note} className="report-note">
            {note}
          </p>
        ))}
      </div>
    );
  return (
    <div className="report-result">
      {result.notes.length > 0 && (
        <ul className="report-notes">
          {result.notes.map((note) => (
            <li key={note}>{note}</li>
          ))}
        </ul>
      )}
      {result.tables.map((table, i) => (
        <Table key={`${table.title}-${i}`} table={table} caption={result.title} />
      ))}
    </div>
  );
}
