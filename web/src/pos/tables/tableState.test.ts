import { describe, expect, it } from 'vitest';
import { groupByArea, isMyTable, oldest, tableLabel, tileLabel, tileState } from './tableState';
import type { TableTile, TicketSummary } from './types';

const now = Date.parse('2026-10-20T19:00:00Z');
const minutesAgo = (m: number) => new Date(now - m * 60_000).toISOString();

function ticket(extra: Partial<TicketSummary> = {}): TicketSummary {
  return {
    id: 'k1',
    number: 1042,
    tableId: 't7',
    customerName: null,
    openedById: 'u-edna',
    openedBy: 'edna',
    total: 1370,
    openedAt: minutesAgo(40),
    lastActivityAt: minutesAgo(5),
    mine: false,
    lock: null,
    newLines: 0,
    ...extra,
  };
}

function table(name: string, tickets: TicketSummary[] = [], areaId = 'main'): TableTile {
  return { id: `t-${name}`, name, areaId, sortOrder: 0, tickets, idleMinutes: null };
}

describe('table tiles', () => {
  it('a table without tickets is free', () => {
    const state = tileState(table('4'), now, 30, 'term-a');
    expect(state).toMatchObject({ kind: 'free', waiter: null, lockedElsewhere: false });
    expect(tileLabel(table('4'), state)).toBe('4: Free');
  });

  it('an occupied table shows the waiter and the running total of its tickets', () => {
    const tile = table('7', [ticket(), ticket({ id: 'k2', number: 1047, total: 540 })]);
    const state = tileState(tile, now, 30, 'term-a');
    expect(state).toMatchObject({
      kind: 'occupied',
      total: 1910,
      waiter: 'edna · 2 tickets',
      idleMinutes: null,
    });
  });

  it('turns idle after the setting with no activity, and says so in words', () => {
    const tile = table('5', [ticket({ lastActivityAt: minutesAgo(42) })]);
    const state = tileState(tile, now, 30, 'term-a');
    expect(state).toMatchObject({ kind: 'idle', idleMinutes: 42 });
    expect(tileLabel(tile, state)).toBe('5: edna, 1,370, Idle 42 min');
    expect(tileState(tile, now, 60, 'term-a').kind).toBe('occupied');
  });

  it('shows a lock only when a ticket is open on another terminal', () => {
    const lock = { terminalId: 'term-b', terminal: 'Pillar 1', userId: 'u-ruth', user: 'Ruth' };
    const tile = table('7', [ticket({ lock })]);
    expect(tileState(tile, now, 30, 'term-a').lockedElsewhere).toBe(true);
    expect(tileState(tile, now, 30, 'term-b').lockedElsewhere).toBe(false);
  });

  it('counts lines not printed yet on all its tickets, so they are not forgotten', () => {
    const t7 = table('7', [ticket({ newLines: 2 }), ticket({ id: 'k2', newLines: 1 })]);
    const state = tileState(t7, now, 30, 'term-a');
    expect(state.notPrinted).toBe(3);
    expect(tileLabel(t7, state)).toBe('7: edna · 2 tickets, 2,740, 3 not printed');
    expect(tileState(table('8', [ticket()]), now, 30, 'term-a').notPrinted).toBe(0);
  });

  it('my tables are those with a ticket I opened or added a line to', () => {
    expect(isMyTable(table('7', [ticket({ mine: true })]))).toBe(true);
    expect(isMyTable(table('7', [ticket()]))).toBe(false);
    expect(isMyTable(table('4'))).toBe(false);
  });

  it('groups by area in area order and keeps empty areas out', () => {
    const areas = [
      { id: 'bar', name: 'Bar', sortOrder: 30 },
      { id: 'main', name: 'Main', sortOrder: 10 },
      { id: 'pool', name: 'Pool', sortOrder: 20 },
    ];
    const groups = groupByArea(areas, [table('1'), table('Bar 1', [], 'bar'), table('2')]);
    expect(groups.map((g) => [g.area.name, g.tables.map((t) => t.name)])).toEqual([
      ['Main', ['1', '2']],
      ['Bar', ['Bar 1']],
    ]);
  });

  it('keeps the oldest ticket in a merge, and names tables for people', () => {
    expect(
      oldest([ticket({ id: 'b', number: 1047, openedAt: minutesAgo(10) }), ticket()]).number,
    ).toBe(1042);
    expect(tableLabel('7')).toBe('Table 7');
    expect(tableLabel('Pool 1')).toBe('Pool 1');
  });
});
