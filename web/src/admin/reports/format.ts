import { formatAmount, formatSigned } from '../../shared/format/money';
import { t } from '../../shared/i18n/i18n';
import type { Cell, ReportColumn } from './types';

/** The first reports are tabs (the wireframe's row); the rest sit in "More reports" so the row never scrolls sideways. */
export const TabCount = 7;

/** Groups longer than this show their first rows and "… n more". */
export const GroupPreview = 50;

function pad(n: number) {
  return String(n).padStart(2, '0');
}

/** A cell as text, the same way the PDF and receipt versions show it. */
export function cellText(value: Cell, column: ReportColumn): string {
  if (value === null || value === undefined) return '';
  if (typeof value === 'boolean') return value ? t('common.yes') : t('common.no');
  if (typeof value === 'number') {
    switch (column.type) {
      case 'Money':
        return formatSigned(value);
      case 'Percent':
        return `${value.toLocaleString('en-KE', { maximumFractionDigits: 1 })}%`;
      case 'Ticket':
        return `#${value}`;
      default:
        return formatAmount(value);
    }
  }
  if (column.type === 'Date' && /^\d{4}-\d{2}-\d{2}$/.test(value)) {
    const [y, m, d] = value.split('-').map(Number);
    return new Date(y, m - 1, d).toLocaleDateString('en-GB', {
      weekday: 'short',
      day: 'numeric',
      month: 'short',
      year: 'numeric',
    });
  }
  if ((column.type === 'DateTime' || column.type === 'Time') && /^\d{4}-\d{2}-\d{2}T/.test(value)) {
    const at = new Date(value);
    const time = `${pad(at.getHours())}:${pad(at.getMinutes())}`;
    if (column.type === 'Time') return time;
    return `${at.toLocaleDateString('en-GB', { day: 'numeric', month: 'short' })} ${time}`;
  }
  return value;
}
