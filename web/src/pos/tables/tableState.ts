import { formatAmount } from '../../shared/format/money';
import { t } from '../../shared/i18n/i18n';
import type { Area, TableTile, TicketSummary } from './types';

export type TileKind = 'free' | 'occupied' | 'idle';

export interface TileState {
  kind: TileKind;
  total: number;
  /** "edna", "edna · 2 tickets", or null when free. */
  waiter: string | null;
  /** Minutes without activity, only when idle. */
  idleMinutes: number | null;
  /** One of its tickets is open on another terminal. */
  lockedElsewhere: boolean;
  /** Lines added but not printed yet, on all its tickets. */
  notPrinted: number;
}

/**
 * How a tile looks now. Idle is worked out here, on the terminal's own clock (corrected by the
 * server's time when the map was fetched), so tiles turn orange without waiting for an event.
 */
export function tileState(
  table: TableTile,
  nowMs: number,
  idleLimitMinutes: number,
  myTerminalId: string | null,
): TileState {
  const tickets = table.tickets;
  if (tickets.length === 0)
    return {
      kind: 'free',
      total: 0,
      waiter: null,
      idleMinutes: null,
      lockedElsewhere: false,
      notPrinted: 0,
    };

  const total = tickets.reduce((sum, t) => sum + t.total, 0);
  const waiter =
    tickets.length === 1
      ? tickets[0].openedBy
      : `${tickets[0].openedBy} · ${tickets.length} tickets`;
  const quietest = Math.min(...tickets.map((t) => Date.parse(t.lastActivityAt)));
  const idle = Math.floor((nowMs - quietest) / 60_000);
  const lockedElsewhere = tickets.some(
    (t) => t.lock !== null && t.lock.terminalId !== myTerminalId,
  );
  const notPrinted = tickets.reduce((sum, t) => sum + (t.newLines ?? 0), 0);
  return idle >= idleLimitMinutes
    ? { kind: 'idle', total, waiter, idleMinutes: idle, lockedElsewhere, notPrinted }
    : { kind: 'occupied', total, waiter, idleMinutes: null, lockedElsewhere, notPrinted };
}

/** My tables: at least one open ticket opened by me, or with a line added by me. */
export function isMyTable(table: TableTile): boolean {
  return table.tickets.some((t) => t.mine);
}

/** Tables grouped by area, in area order; tables keep the server's order within an area. */
export function groupByArea(
  areas: Area[],
  tables: TableTile[],
): { area: Area; tables: TableTile[] }[] {
  return [...areas]
    .sort((a, b) => a.sortOrder - b.sortOrder || a.name.localeCompare(b.name))
    .map((area) => ({ area, tables: tables.filter((t) => t.areaId === area.id) }))
    .filter((group) => group.tables.length > 0);
}

/** The ticket that is kept in a merge: the oldest. */
export function oldest(tickets: TicketSummary[]): TicketSummary {
  return [...tickets].sort(
    (a, b) => Date.parse(a.openedAt) - Date.parse(b.openedAt) || a.number - b.number,
  )[0];
}

/** "Table 7" for numbered tables, the name itself for "Pool 1" or "Bar 2". */
export function tableLabel(name: string): string {
  return /^\d+$/.test(name) ? `Table ${name}` : name;
}

/** "12 min", "1 h 5 min". */
export function age(fromIso: string, nowMs: number): string {
  const minutes = Math.max(0, Math.floor((nowMs - Date.parse(fromIso)) / 60_000));
  if (minutes < 60) return `${minutes} min`;
  return `${Math.floor(minutes / 60)} h ${minutes % 60} min`;
}

/** The tile's accessible name: "7: edna · 2 tickets, 1,910, Idle 42 min". */
export function tileLabel(table: TableTile, state: TileState): string {
  if (state.kind === 'free') return `${table.name}: ${t('pos.map.free')}`;
  const parts = [state.waiter, formatAmount(state.total)];
  if (state.idleMinutes !== null) parts.push(t('pos.map.idle', { minutes: state.idleMinutes }));
  if (state.notPrinted > 0) parts.push(t('pos.map.notPrinted', { count: state.notPrinted }));
  if (state.lockedElsewhere) parts.push(t('pos.map.lockedElsewhere'));
  return `${table.name}: ${parts.join(', ')}`;
}
