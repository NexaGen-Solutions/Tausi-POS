import { useState } from 'react';
import { t } from '../i18n/i18n';
import { isoDate, type DateRange, type IsoDate } from './dates';

const parse = (iso: IsoDate) => {
  const [y, m, d] = iso.split('-').map(Number);
  return new Date(y, m - 1, d);
};

/** The weeks of a month, Monday first, with the days before and after it left empty. */
function weeks(year: number, month: number): (Date | null)[][] {
  const first = new Date(year, month, 1);
  const days = new Date(year, month + 1, 0).getDate();
  const cells: (Date | null)[] = Array.from({ length: (first.getDay() + 6) % 7 }, () => null);
  for (let d = 1; d <= days; d++) cells.push(new Date(year, month, d));
  while (cells.length % 7 !== 0) cells.push(null);
  return Array.from({ length: cells.length / 7 }, (_, i) => cells.slice(i * 7, i * 7 + 7));
}

/**
 * A touch calendar for a range of dates: the first tap picks the start, the second the end (an
 * earlier day starts again). Days after <code>max</code> can't be picked. Big buttons, no hover.
 */
export function Calendar({
  value,
  onChange,
  max,
}: {
  value: DateRange | null;
  onChange: (range: DateRange) => void;
  max?: IsoDate;
}) {
  const start = value ? parse(value.from) : max ? parse(max) : new Date();
  const [shown, setShown] = useState({ year: start.getFullYear(), month: start.getMonth() });
  const [picking, setPicking] = useState<IsoDate | null>(null);

  const month = new Date(shown.year, shown.month, 1);
  const move = (by: number) => {
    const next = new Date(shown.year, shown.month + by, 1);
    setShown({ year: next.getFullYear(), month: next.getMonth() });
  };
  const pick = (day: IsoDate) => {
    if (picking === null || day < picking) {
      setPicking(day);
      onChange({ from: day, to: day });
    } else {
      setPicking(null);
      onChange({ from: picking, to: day });
    }
  };
  const inRange = (day: IsoDate) => value !== null && day >= value.from && day <= value.to;

  return (
    <div className="nx-calendar">
      <div className="nx-calendar__head">
        <button
          type="button"
          className="nx-calendar__move"
          onClick={() => move(-1)}
          aria-label={t('common.calendar.previous')}
        >
          ‹
        </button>
        <span className="nx-calendar__month" aria-live="polite">
          {month.toLocaleDateString('en-GB', { month: 'long', year: 'numeric' })}
        </span>
        <button
          type="button"
          className="nx-calendar__move"
          onClick={() => move(1)}
          aria-label={t('common.calendar.next')}
        >
          ›
        </button>
      </div>
      <table className="nx-calendar__grid">
        <thead>
          <tr>
            {['mon', 'tue', 'wed', 'thu', 'fri', 'sat', 'sun'].map((d) => (
              <th key={d} scope="col">
                {t(`common.calendar.${d}`)}
              </th>
            ))}
          </tr>
        </thead>
        <tbody>
          {weeks(shown.year, shown.month).map((week, i) => (
            <tr key={i}>
              {week.map((day, j) => {
                if (!day) return <td key={j} />;
                const iso = isoDate(day);
                const chosen = inRange(iso);
                return (
                  <td key={j}>
                    <button
                      type="button"
                      className={`nx-calendar__day${chosen ? ' nx-calendar__day--chosen' : ''}`}
                      aria-pressed={chosen}
                      aria-label={day.toLocaleDateString('en-GB', {
                        weekday: 'long',
                        day: 'numeric',
                        month: 'long',
                        year: 'numeric',
                      })}
                      disabled={max !== undefined && iso > max}
                      onClick={() => pick(iso)}
                    >
                      {day.getDate()}
                    </button>
                  </td>
                );
              })}
            </tr>
          ))}
        </tbody>
      </table>
      <p className="nx-calendar__hint">
        {picking ? t('common.calendar.pickEnd') : t('common.calendar.pickStart')}
      </p>
    </div>
  );
}
