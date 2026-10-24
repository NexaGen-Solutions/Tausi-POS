import { formatAmount } from '../../shared/format/money';
import { t } from '../../shared/i18n/i18n';
import { countTotal, type CashCount } from './types';

/** One row per note (a number box and its subtotal), coins as one amount, and the total. */
export function CashCountForm({
  denominations,
  value,
  onChange,
  disabled = false,
  totalLabel,
}: {
  denominations: number[];
  value: CashCount;
  onChange: (count: CashCount) => void;
  disabled?: boolean;
  totalLabel: string;
}) {
  const setNote = (note: number, text: string) =>
    onChange({ ...value, notes: { ...value.notes, [note]: Number(text.replace(/\D/g, '') || 0) } });
  return (
    <div className="cash-count">
      {denominations.map((note) => {
        const n = value.notes[note] ?? 0;
        return (
          <label key={note} className="cash-count__row">
            <span className="cash-count__note">{formatAmount(note)}</span>
            <span aria-hidden="true">×</span>
            <input
              inputMode="numeric"
              aria-label={t('pos.cash.notesOf', { note: formatAmount(note) })}
              value={n === 0 ? '' : String(n)}
              placeholder="0"
              disabled={disabled}
              onChange={(e) => setNote(note, e.target.value)}
            />
            <span className="cash-count__subtotal">{formatAmount(note * n)}</span>
          </label>
        );
      })}
      <label className="cash-count__row">
        <span className="cash-count__note">{t('pos.cash.coins')}</span>
        <span aria-hidden="true" />
        <input
          inputMode="decimal"
          aria-label={t('pos.cash.coins')}
          value={value.coins === 0 ? '' : String(value.coins)}
          placeholder="0"
          disabled={disabled}
          onChange={(e) =>
            onChange({ ...value, coins: Number(e.target.value.replace(/[^\d.]/g, '') || 0) })
          }
        />
        <span className="cash-count__subtotal">{formatAmount(value.coins || 0)}</span>
      </label>
      <div className="cash-count__total">
        <span>{totalLabel}</span>
        <span className="cash-amount">{formatAmount(countTotal(value))}</span>
      </div>
    </div>
  );
}
