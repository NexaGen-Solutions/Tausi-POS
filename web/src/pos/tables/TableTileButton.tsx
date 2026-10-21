import { formatAmount } from '../../shared/format/money';
import { t } from '../../shared/i18n/i18n';
import { tileLabel, type TileState } from './tableState';
import type { TableTile } from './types';

/** A padlock, for tables with a ticket open on another terminal. */
export function LockIcon() {
  return (
    <svg
      className="map-lock"
      viewBox="0 0 16 16"
      width="16"
      height="16"
      aria-hidden="true"
      focusable="false"
    >
      <path d="M4.5 7V5a3.5 3.5 0 0 1 7 0v2" fill="none" stroke="currentColor" strokeWidth="1.6" />
      <rect x="3" y="7" width="10" height="7.5" rx="1.2" fill="currentColor" />
    </svg>
  );
}

/** One table on the map (wireframe 2). Colour is never the only signal: idle tiles say "Idle n min". */
export function TableTileButton({
  table,
  state,
  selected,
  disabled,
  onTap,
}: {
  table: TableTile;
  state: TileState;
  selected: boolean;
  disabled: boolean;
  onTap: (timeStamp: number) => void;
}) {
  return (
    <button
      type="button"
      className={`map-tile map-tile--${state.kind}${selected ? ' map-tile--selected' : ''}`}
      aria-pressed={selected}
      aria-label={tileLabel(table, state)}
      disabled={disabled}
      onClick={(event) => onTap(event.timeStamp)}
    >
      <span className="map-tile__top">
        <span className="map-tile__name">{table.name}</span>
        {state.kind !== 'free' && (
          <span className="map-tile__total">{formatAmount(state.total)}</span>
        )}
      </span>
      <span className="map-tile__bottom">
        <span className="map-tile__waiter">
          {state.kind === 'free' ? t('pos.map.free') : state.waiter}
        </span>
        {state.idleMinutes !== null && (
          <span className="map-tile__idle">
            {t('pos.map.idle', { minutes: state.idleMinutes })}
          </span>
        )}
        {state.partPaidLeft !== null && (
          <span className="map-tile__unprinted">
            {t('pos.map.partPaid', { left: formatAmount(state.partPaidLeft) })}
          </span>
        )}
        {state.notPrinted > 0 && (
          <span className="map-tile__unprinted">
            {t('pos.map.notPrinted', { count: state.notPrinted })}
          </span>
        )}
        {state.lockedElsewhere && <LockIcon />}
      </span>
    </button>
  );
}
