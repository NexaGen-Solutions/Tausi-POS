import type { ReportResult, ReportRow, ReportTable } from '../../admin/reports/types';
import { formatAmount, formatSigned } from '../../shared/format/money';
import { t } from '../../shared/i18n/i18n';

export const ReportKeys = ['sales-by-user', 'daily-sales', 'sales-by-item'] as const;
export type ReportKey = (typeof ReportKeys)[number];
export const Quick = ['Today', 'Yesterday', 'ThisWeek'] as const;
type QuickRange = (typeof Quick)[number];

export interface TerminalFilters {
  quick: QuickRange | null;
  from: string | null;
  to: string | null;
  userId: string | null;
  terminalId: string | null;
}

export interface TerminalReport {
  ownOnly: boolean;
  result: ReportResult;
}

const columnOf = (table: ReportTable, key: string) => table.columns.findIndex((c) => c.key === key);
const num = (row: ReportRow | null | undefined, i: number) =>
  row && i >= 0 && typeof row.cells[i] === 'number' ? (row.cells[i] as number) : 0;

/** The big figures at the top of each report. */
export function headline(key: ReportKey, result: ReportResult): { label: string; value: string }[] {
  const table = result.tables[0];
  if (!table) return [];
  if (key === 'daily-sales') {
    const group = (title: string) => table.groups.find((g) => g.title === title);
    const settled = group('Tickets')?.rows[0];
    return [
      {
        label: t('pos.reportsPage.figures.sales'),
        value: formatSigned(group('Sales')?.total ?? 0),
      },
      {
        label: t('pos.reportsPage.figures.paid'),
        value: formatSigned(group('Payments')?.total ?? 0),
      },
      { label: t('pos.reportsPage.figures.tickets'), value: formatAmount(num(settled, 1)) },
    ];
  }
  const qty = num(table.totals, columnOf(table, 'qty'));
  const sales =
    key === 'sales-by-item'
      ? num(table.totals, columnOf(table, 'gross')) - num(table.totals, columnOf(table, 'discount'))
      : num(table.totals, columnOf(table, 'amount'));
  const figures = [
    { label: t('pos.reportsPage.figures.sales'), value: formatSigned(sales) },
    { label: t('pos.reportsPage.figures.items'), value: formatAmount(qty) },
  ];
  if (key === 'sales-by-user')
    figures.push({
      label: t('pos.reportsPage.figures.users'),
      value: String(table.groups.length),
    });
  return figures;
}
