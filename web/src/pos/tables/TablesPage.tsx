import { useQuery } from '@tanstack/react-query';
import { useRef, useState } from 'react';
import { useNavigate, useSearchParams } from 'react-router';
import { ApiError, RequestSentError, api } from '../../shared/api/client';
import type { Brand, Me } from '../../shared/api/types';
import { OfflineBanner } from '../../shared/connection/OfflineBanner';
import { isOffline } from '../../shared/connection/status';
import { useConnection } from '../../shared/connection/useConnection';
import { t } from '../../shared/i18n/i18n';
import { Banner, Button, useToast } from '../../shared/ui';
import { PosTopBar } from '../PosTopBar';
import { usePinSession } from '../usePinSession';
import { MergeDialog } from './MergeDialog';
import { TablePanel } from './TablePanel';
import { TableTileButton } from './TableTileButton';
import { groupByArea, isMyTable, tableLabel, tileState } from './tableState';
import { SettledList } from './SettledList';
import { TakeawayList } from './TakeawayList';
import type { TableTile, TicketSummary } from './types';
import { useServerNow, useTableMap, useTakeaway } from './useTableMap';
import './tables.css';

type Tab = 'all' | 'mine' | 'takeaway' | 'settled';

/** A second tap on the same table within this time opens it. */
export const doubleTapMs = 400;

/** Wireframe 2: the table map, the first screen after login. */
export function TablesPage() {
  const navigate = useNavigate();
  const toast = useToast();
  const session = usePinSession();
  const myTerminal = session?.user.terminalId ?? null;
  const { map, clockOffset, refetchAll } = useTableMap();
  const now = useServerNow(clockOffset);
  const me = useQuery({ queryKey: ['me'], queryFn: () => api<Me>('/api/auth/me') });
  const brand = useQuery({ queryKey: ['brand'], queryFn: () => api<Brand>('/api/brand') });
  const offline = isOffline(useConnection());
  const [tab, setTab] = useState<Tab>('all');
  const takeaway = useTakeaway(tab === 'takeaway');
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const [chosenId, setChosenId] = useState<string | null>(null);
  const [merging, setMerging] = useState(false);
  const [busy, setBusy] = useState(false);
  const lastTap = useRef<{ id: string; at: number } | null>(null);
  const [params, setParams] = useSearchParams();
  // "Move table" on the order screen comes here with ?move={ticketId}: choose its new table.
  const [movingId, setMovingId] = useState<string | null>(() => params.get('move'));

  const data = map.data;
  const moving =
    data?.tables.flatMap((table) => table.tickets).find((k) => k.id === movingId) ?? null;
  const setMoving = (ticket: TicketSummary | null) => {
    setMovingId(ticket?.id ?? null);
    if (params.has('move')) setParams({}, { replace: true });
  };
  const currency = brand.data?.currency ?? 'KES';
  const permission = (key: string) => me.data?.permissions[key] ?? 'NotAllowed';
  const can = (key: string) => permission(key) !== 'NotAllowed';
  const dayOpen = data?.dayOpen ?? true;
  const canAdd = can('order.add');
  // Settling can't be approved for someone else, so only Allowed counts.
  const canSettle = permission('ticket.settle') === 'Allowed';
  const selected = data?.tables.find((table) => table.id === selectedId) ?? null;
  const chosen = selected?.tickets.find((k) => k.id === chosenId) ?? selected?.tickets[0] ?? null;
  const shown = data ? (tab === 'mine' ? data.tables.filter(isMyTable) : data.tables) : [];
  const groups = data ? groupByArea(data.areas, shown) : [];
  const idleLimit = data?.tableIdleMinutes ?? 30;

  const fail = (error: unknown) => {
    // Sent to a supervisor instead: the approval pad already said so.
    if (error instanceof RequestSentError) {
      setMoving(null);
      setMerging(false);
      return;
    }
    toast(error instanceof ApiError ? error.message : t('common.somethingWrong'), {
      tone: 'warning',
    });
  };

  const openTicket = (ticket: TicketSummary) =>
    navigate(`/pos/ticket/${ticket.id}${ticket.tableId ? `?table=${ticket.tableId}` : ''}`);

  const newTicket = (tableId: string | null) => {
    if (!dayOpen) return toast(t('pos.map.dayClosed'), { tone: 'warning' });
    navigate(tableId ? `/pos/ticket/new?table=${tableId}` : '/pos/ticket/new?takeaway=1');
  };

  const move = async (ticket: TicketSummary, table: TableTile) => {
    setBusy(true);
    try {
      await api(`/api/pos/tickets/${ticket.id}/move`, {
        method: 'POST',
        body: { toTableId: table.id },
        approval: { kind: 'Move', ticketId: ticket.id, payload: { toTableId: table.id } },
      });
      toast(t('pos.map.moved', { number: ticket.number, table: tableLabel(table.name) }));
      setMoving(null);
      setSelectedId(table.id);
      setChosenId(ticket.id);
    } catch (error) {
      fail(error);
    } finally {
      setBusy(false);
    }
  };

  const merge = async (intoId: string, otherIds: string[]) => {
    setBusy(true);
    try {
      const kept = await api<TicketSummary>('/api/pos/tickets/merge', {
        method: 'POST',
        body: { ticketIds: otherIds, intoTicketId: intoId },
        approval: {
          kind: 'Merge',
          ticketId: intoId,
          payload: { ticketIds: otherIds, intoTicketId: intoId },
        },
      });
      toast(t('pos.map.merged', { number: kept.number }));
      setMerging(false);
      setChosenId(intoId);
    } catch (error) {
      fail(error);
    } finally {
      setBusy(false);
    }
  };

  const openDay = async () => {
    try {
      await api('/api/day/open', { method: 'POST' });
    } catch (error) {
      if (!(error instanceof ApiError && error.status === 409)) fail(error);
    }
    refetchAll();
  };

  const tap = (table: TableTile, at: number) => {
    if (offline || busy) return;
    if (moving) {
      if (table.id !== moving.tableId) void move(moving, table);
      return;
    }
    const double = lastTap.current?.id === table.id && at - lastTap.current.at < doubleTapMs;
    lastTap.current = double ? null : { id: table.id, at };
    setSelectedId(table.id);
    if (!double) setChosenId(null);
    else if (table.tickets.length === 0) {
      if (canAdd) newTicket(table.id);
    } else if (table.tickets.length === 1) openTicket(table.tickets[0]);
  };

  return (
    <div className="pos-screen map-screen">
      <PosTopBar
        extra={
          <Button variant="ghost-dark" onClick={() => navigate('/pos/my-sales')}>
            {t('pos.mySales')}
          </Button>
        }
      />
      <OfflineBanner />
      {data && !dayOpen && (
        <Banner
          action={
            can('day.open_close')
              ? { label: t('pos.map.openDay'), onClick: () => void openDay() }
              : undefined
          }
        >
          {t('pos.map.dayClosed')}
        </Banner>
      )}
      <div className="map-body">
        <main className="map-main">
          <div className="map-toolbar">
            <div className="map-tabs" role="group" aria-label={t('pos.map.tabs')}>
              {(canSettle
                ? (['all', 'mine', 'takeaway', 'settled'] as const)
                : (['all', 'mine', 'takeaway'] as const)
              ).map((key) => (
                <button
                  key={key}
                  type="button"
                  className={`map-tab${tab === key ? ' map-tab--active' : ''}`}
                  aria-pressed={tab === key}
                  onClick={() => setTab(key)}
                >
                  {t(
                    key === 'all'
                      ? 'pos.map.allTables'
                      : key === 'mine'
                        ? 'pos.map.myTables'
                        : key === 'settled'
                          ? 'pos.settled.title'
                          : 'pos.map.takeaway',
                  )}
                </button>
              ))}
            </div>
            {(tab === 'all' || tab === 'mine') && (
              <ul className="map-legend" aria-label="Legend">
                <li>
                  <span className="map-swatch map-swatch--free" aria-hidden="true" />
                  {t('pos.map.legendFree')}
                </li>
                <li>
                  <span className="map-swatch map-swatch--occupied" aria-hidden="true" />
                  {t('pos.map.legendOccupied')}
                </li>
                <li>
                  <span className="map-swatch map-swatch--idle" aria-hidden="true" />
                  {t('pos.map.legendIdle', { minutes: idleLimit })}
                </li>
              </ul>
            )}
          </div>

          {moving && (
            <div className="map-moving" role="status">
              <span>{t('pos.map.moveTitle', { number: moving.number })}</span>
              <Button onClick={() => setMoving(null)}>{t('pos.map.cancel')}</Button>
            </div>
          )}

          {tab === 'settled' ? (
            <SettledList currency={currency} disabled={offline} />
          ) : tab === 'takeaway' ? (
            <TakeawayList
              tickets={takeaway.data ?? []}
              now={now}
              currency={currency}
              canAdd={canAdd && dayOpen}
              disabled={offline}
              onOpen={openTicket}
              onNew={() => newTicket(null)}
            />
          ) : (
            <div className="map-grid-scroll">
              {tab === 'mine' && data && shown.length === 0 && (
                <p className="map-empty">{t('pos.map.noMyTables')}</p>
              )}
              {groups.map(({ area, tables }) => (
                <section key={area.id} className="map-area" aria-label={area.name}>
                  {groups.length > 1 && <h2 className="map-area__title">{area.name}</h2>}
                  <div className="map-grid">
                    {tables.map((table) => (
                      <TableTileButton
                        key={table.id}
                        table={table}
                        state={tileState(table, now, idleLimit, myTerminal)}
                        selected={!moving && table.id === selectedId}
                        disabled={
                          offline || busy || (moving !== null && table.id === moving.tableId)
                        }
                        onTap={(at) => tap(table, at)}
                      />
                    ))}
                  </div>
                </section>
              ))}
            </div>
          )}
        </main>

        {tab !== 'takeaway' && (
          <TablePanel
            table={selected}
            chosen={chosen}
            now={now}
            currency={currency}
            canAdd={canAdd && dayOpen}
            canMove={can('ticket.move')}
            canMerge={can('ticket.merge')}
            disabled={offline || busy || moving !== null}
            onChoose={(ticket) => setChosenId(ticket.id)}
            onOpen={openTicket}
            onNew={() => selected && newTicket(selected.id)}
            onMerge={() => setMerging(true)}
            onMove={(ticket) => setMoving(ticket)}
          />
        )}
      </div>

      {merging && selected && (
        <MergeDialog
          table={selected}
          currency={currency}
          busy={busy || offline}
          onMerge={(into, others) => void merge(into, others)}
          onClose={() => setMerging(false)}
        />
      )}
    </div>
  );
}
