import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { useNavigate } from 'react-router';
import { ApiError, api } from '../../shared/api/client';
import { onPosEvent } from '../../shared/connection/hub';
import { t } from '../../shared/i18n/i18n';
import { useToast } from '../../shared/ui';
import { usePinSession } from '../usePinSession';
import type { OrderLine, TableEvent, TicketDetail } from '../tables/types';
import { priced, type PosItem } from './menu';

/** An add that the server hasn't confirmed yet. */
interface PendingAdd {
  key: string;
  item: PosItem;
  optionIds: string[];
  quantity: number;
  unitPrice: number;
  options: string | null;
}

/** A line as the screen shows it: saved, or still being saved (pending). */
export interface ShownLine extends OrderLine {
  pending: boolean;
}

export const ticketKey = (id: string | null) => ['ticket', id] as const;

/**
 * The order screen's ticket. Every change goes to the server as it happens, one after the other
 * in the order it was made (fast taps are never lost or reordered). Adds show straight away
 * (optimistic) and turn solid when the server confirms; a refused add disappears with the reason
 * in a toast. The first add on a draft saves the ticket and moves to its address.
 */
export function useOrder(routeId: string, tableId: string | null, draftCustomer: string | null) {
  const navigate = useNavigate();
  const queries = useQueryClient();
  const toast = useToast();
  const session = usePinSession();
  const [ticketId, setTicketId] = useState<string | null>(routeId === 'new' ? null : routeId);
  const idRef = useRef(ticketId);
  const chain = useRef<Promise<unknown>>(Promise.resolve());
  const [pending, setPending] = useState<PendingAdd[]>([]);
  const [busy, setBusy] = useState(0);

  // Opened another ticket (e.g. through the address bar): start over with it.
  useEffect(() => {
    if (routeId !== 'new' && routeId !== idRef.current) {
      idRef.current = routeId;
      setTicketId(routeId);
      setPending([]);
    }
  }, [routeId]);

  const ticket = useQuery({
    queryKey: ticketKey(ticketId),
    queryFn: () => api<TicketDetail>(`/api/pos/tickets/${ticketId}`),
    enabled: ticketId !== null,
    staleTime: 0,
  });

  useEffect(() => {
    if (!ticketId) return;
    const refetch = (payload: unknown) => {
      const event = payload as Partial<TableEvent>;
      if (event.ticketId === ticketId)
        void queries.invalidateQueries({ queryKey: ticketKey(ticketId) });
    };
    const off = [onPosEvent('ticket.changed', refetch), onPosEvent('print.failed', refetch)];
    return () => off.forEach((unsubscribe) => unsubscribe());
  }, [ticketId, queries]);

  const fail = useCallback(
    (error: unknown) =>
      toast(error instanceof ApiError ? error.message : t('common.somethingWrong'), {
        tone: 'warning',
      }),
    [toast],
  );

  /** Runs writes one at a time, in the order they were asked for. */
  const enqueue = useCallback(<T>(write: () => Promise<T>): Promise<T> => {
    const run = chain.current.then(write);
    chain.current = run.catch(() => undefined);
    setBusy((n) => n + 1);
    void run.finally(() => setBusy((n) => n - 1)).catch(() => undefined);
    return run;
  }, []);

  const saved = useCallback(
    (detail: TicketDetail) => {
      queries.setQueryData(ticketKey(detail.id), detail);
      void queries.invalidateQueries({ queryKey: ['table-map'] });
    },
    [queries],
  );

  const add = useCallback(
    (item: PosItem, optionIds: string[], quantity: number) => {
      const price = priced(item, optionIds);
      const entry: PendingAdd = {
        key: crypto.randomUUID(),
        item,
        optionIds,
        quantity,
        unitPrice: price.unitPrice,
        options: price.options,
      };
      setPending((list) => [...list, entry]);
      void enqueue(async () => {
        const line = { menuItemId: item.id, optionIds, quantity };
        if (!idRef.current) {
          const created = await api<TicketDetail>('/api/pos/tickets', {
            method: 'POST',
            body: { tableId, customerName: draftCustomer, lines: [line] },
          });
          idRef.current = created.id;
          saved(created);
          setTicketId(created.id);
          navigate(`/pos/ticket/${created.id}${tableId ? `?table=${tableId}` : ''}`, {
            replace: true,
          });
          return created;
        }
        const detail = await api<TicketDetail>(`/api/pos/tickets/${idRef.current}/lines`, {
          method: 'POST',
          body: line,
        });
        saved(detail);
        return detail;
      })
        .catch(fail)
        .finally(() => setPending((list) => list.filter((p) => p.key !== entry.key)));
    },
    [enqueue, fail, navigate, saved, tableId, draftCustomer],
  );

  /** A write on the saved ticket that returns it (line changes, printing, the note). */
  const write = useCallback(
    (path: string, method: 'POST' | 'PATCH' | 'DELETE', body?: unknown) =>
      enqueue(async () => {
        if (!idRef.current) return null;
        const detail = await api<TicketDetail>(`/api/pos/tickets/${idRef.current}${path}`, {
          method,
          body,
        });
        saved(detail);
        return detail;
      }),
    [enqueue, saved],
  );

  const lines = useMemo(
    () =>
      withPending(
        ticket.data?.lines ?? [],
        pending,
        session?.user.id ?? '',
        session?.user.name ?? '',
        session?.user.terminalName ?? '',
      ),
    [ticket.data, pending, session],
  );

  return { ticketId, ticket, lines, pending: pending.length, busy: busy > 0, add, write, fail };
}

/**
 * The saved lines with the unconfirmed adds on top. An add of the same item, options and price by
 * the same person adds to their New line, as the server will; otherwise it shows as a new line.
 */
export function withPending(
  saved: OrderLine[],
  pending: PendingAdd[],
  userId: string,
  userName: string,
  terminal: string,
): ShownLine[] {
  const shown: ShownLine[] = saved.map((l) => ({ ...l, pending: false }));
  for (const p of pending) {
    const same = shown.find(
      (l) =>
        l.status === 'New' &&
        l.menuItemId === p.item.id &&
        l.options === p.options &&
        l.unitPrice === p.unitPrice &&
        l.addedById === userId &&
        !l.note,
    );
    if (same) {
      same.quantity += p.quantity;
      same.gross += p.unitPrice * p.quantity;
      same.pending = true;
      continue;
    }
    shown.push({
      id: `pending-${p.key}`,
      menuItemId: p.item.id,
      name: p.item.name,
      shortName: p.item.shortName,
      options: p.options,
      note: null,
      quantity: p.quantity,
      unitPrice: p.unitPrice,
      gross: p.unitPrice * p.quantity,
      status: 'New',
      printRound: null,
      addedById: userId,
      addedBy: userName,
      addedOn: terminal,
      addedAt: new Date().toISOString(),
      printedAt: null,
      voided: false,
      pending: true,
    });
  }
  return shown;
}
