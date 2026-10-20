import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useCallback, useEffect, useRef, useState } from 'react';
import { api } from '../../shared/api/client';
import { onPosEvent } from '../../shared/connection/hub';
import type { TableEvent, TableMap, TicketSummary } from './types';

export const tableMapKey = ['table-map'] as const;
export const takeawayKey = ['takeaway'] as const;

/**
 * The table map, kept live: a ticket.changed or ticket.lock event that follows on from the version
 * we have refetches just that table; a gap in versions (a missed event, or a server restart)
 * refetches everything. tables.changed and day.opened refetch everything.
 */
export function useTableMap() {
  const queries = useQueryClient();
  const version = useRef<number | null>(null);
  const [clockOffset, setClockOffset] = useState(0);

  const map = useQuery({
    queryKey: tableMapKey,
    queryFn: async () => {
      const result = await api<TableMap>('/api/pos/tables');
      version.current = result.version;
      setClockOffset(Date.parse(result.serverTime) - Date.now());
      return result;
    },
    staleTime: 0,
  });

  const refetchAll = useCallback(() => {
    version.current = null;
    void queries.invalidateQueries({ queryKey: tableMapKey });
  }, [queries]);

  const patch = useCallback(
    async (tableId: string) => {
      try {
        const fresh = await api<TableMap>(`/api/pos/tables?tableIds=${tableId}`);
        const table = fresh.tables.find((t) => t.id === tableId);
        queries.setQueryData<TableMap>(tableMapKey, (old) => {
          if (!old) return old;
          if (!table) return old; // switched off meanwhile: tables.changed refetches everything
          return {
            ...old,
            dayOpen: fresh.dayOpen,
            tableIdleMinutes: fresh.tableIdleMinutes,
            tables: old.tables.map((t) => (t.id === tableId ? table : t)),
          };
        });
      } catch {
        refetchAll();
      }
    },
    [queries, refetchAll],
  );

  useEffect(() => {
    const onTableEvent = (payload: unknown) => {
      const event = payload as TableEvent;
      if (event.tableId === null) void queries.invalidateQueries({ queryKey: takeawayKey });
      const known = version.current;
      if (known === null) return refetchAll();
      if (event.version <= known) return;
      if (event.version !== known + 1) return refetchAll();
      version.current = event.version;
      if (event.tableId) void patch(event.tableId);
    };
    const off = [
      onPosEvent('ticket.changed', onTableEvent),
      onPosEvent('ticket.lock', onTableEvent),
      onPosEvent('tables.changed', refetchAll),
      onPosEvent('day.opened', refetchAll),
    ];
    return () => off.forEach((unsubscribe) => unsubscribe());
  }, [queries, patch, refetchAll]);

  return { map, clockOffset, refetchAll };
}

export function useTakeaway(enabled: boolean) {
  return useQuery({
    queryKey: takeawayKey,
    queryFn: () => api<TicketSummary[]>('/api/pos/tickets/takeaway'),
    enabled,
    staleTime: 0,
  });
}

/** The time now on the server's clock, updated every few seconds for idle minutes and ages. */
export function useServerNow(offsetMs: number, everyMs = 15_000): number {
  const [now, setNow] = useState(() => Date.now());
  useEffect(() => {
    const timer = window.setInterval(() => setNow(Date.now()), everyMs);
    return () => window.clearInterval(timer);
  }, [everyMs]);
  // A new offset arrives with fresh data: count from this moment, not from the last tick.
  useEffect(() => {
    const timer = window.setTimeout(() => setNow(Date.now()), 0);
    return () => window.clearTimeout(timer);
  }, [offsetMs]);
  return now + offsetMs;
}
