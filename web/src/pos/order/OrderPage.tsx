import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useMemo, useState } from 'react';
import { useNavigate, useParams, useSearchParams } from 'react-router';
import { api } from '../../shared/api/client';
import type { Brand, DeviceInfo, Me } from '../../shared/api/types';
import { OfflineBanner } from '../../shared/connection/OfflineBanner';
import { isOffline } from '../../shared/connection/status';
import { useConnection } from '../../shared/connection/useConnection';
import { formatAmount, formatMoney } from '../../shared/format/money';
import { t } from '../../shared/i18n/i18n';
import { Banner, Button, TopBar } from '../../shared/ui';
import { tableLabel } from '../tables/tableState';
import type { TableMap } from '../tables/types';
import { tableMapKey } from '../tables/useTableMap';
import { useTicketLock } from '../ticket/useTicketLock';
import { usePinSession } from '../usePinSession';
import { LeaveDialog } from './LeaveDialog';
import { needsChoice, search, type PosItem, type PosMenu } from './menu';
import { NoteDialog } from './NoteDialog';
import { OptionsDialog } from './OptionsDialog';
import { useOrder, type ShownLine } from './useOrder';
import './order.css';

const time = (iso: string) =>
  new Date(iso).toLocaleTimeString('en-GB', { hour: '2-digit', minute: '2-digit' });

/** Wireframe 3: the order screen, where waiters add items all night and press Print. */
export function OrderPage() {
  const { id = 'new' } = useParams();
  const [params] = useSearchParams();
  const navigate = useNavigate();
  const queries = useQueryClient();
  const session = usePinSession();
  const tableId = params.get('table');
  // The takeaway name being typed; null shows the saved one.
  const [customerEdit, setCustomerEdit] = useState<string | null>(null);
  const order = useOrder(id, tableId, customerEdit?.trim() || null);
  const ticket = order.ticket.data ?? null;
  const customer = customerEdit ?? ticket?.customerName ?? '';
  const lock = useTicketLock(order.ticketId);
  const offline = isOffline(useConnection());

  const device = useQuery({ queryKey: ['device'], queryFn: () => api<DeviceInfo>('/api/device') });
  const me = useQuery({ queryKey: ['me'], queryFn: () => api<Me>('/api/auth/me') });
  const brand = useQuery({ queryKey: ['brand'], queryFn: () => api<Brand>('/api/brand') });
  const menu = useQuery({ queryKey: ['pos-menu'], queryFn: () => api<PosMenu>('/api/pos/menu') });
  const draftTable = useQuery({
    queryKey: ['table-name', tableId],
    queryFn: async () =>
      (await api<TableMap>(`/api/pos/tables?tableIds=${tableId}`)).tables[0]?.name ?? '',
    enabled: tableId !== null && !ticket,
    initialData: () =>
      queries.getQueryData<TableMap>(tableMapKey)?.tables.find((x) => x.id === tableId)?.name,
  });

  const [categoryId, setCategoryId] = useState<string | null>(null);
  const [query, setQuery] = useState('');
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const [choosing, setChoosing] = useState<PosItem | null>(null);
  const [leaving, setLeaving] = useState<null | (() => void)>(null);
  const [noteOpen, setNoteOpen] = useState(false);

  const currency = brand.data?.currency ?? 'KES';
  const printing = device.data?.printing ?? null;
  const sendAndBill = printing?.mode === 'SendAndBill';
  const permission = (key: string) => me.data?.permissions[key] ?? 'NotAllowed';
  const readOnly = lock !== null && !lock.editable;
  const closed = ticket !== null && ticket.status !== 'Open';
  const disabled = readOnly || closed || offline;
  const isTakeaway = ticket ? ticket.tableId === null : params.get('takeaway') === '1';

  const categories = menu.data?.categories ?? [];
  const category = categories.find((c) => c.id === categoryId) ?? categories[0] ?? null;
  const items = query.trim() && menu.data ? search(menu.data, query) : (category?.items ?? []);
  const lines = order.lines;
  const newCount = lines.filter((l) => l.status === 'New').length;
  const total = lines.reduce((sum, l) => sum + l.gross, 0);
  const selected = lines.find((l) => l.id === selectedId) ?? null;
  const canReprint = permission('ticket.reprint') !== 'NotAllowed';
  const canCancel = permission('order.cancel_unprinted') !== 'NotAllowed';
  const canSettle = permission('ticket.settle') !== 'NotAllowed';

  const tapItem = (item: PosItem) => {
    if (disabled) return;
    if (needsChoice(item)) setChoosing(item);
    else order.add(item, [], 1);
  };

  const act = (run: () => Promise<unknown>) => run().catch(order.fail);
  const print = () => order.write('/print', 'POST');
  const send = () => order.write('/send', 'POST');
  const printBill = () => order.write('/print-bill', 'POST');
  const reprint = () => order.write('/reprint-bill', 'POST');
  const setQuantity = (line: ShownLine, quantity: number) =>
    order.write(`/lines/${line.id}`, 'PATCH', { quantity });
  const remove = (line: ShownLine) => order.write(`/lines/${line.id}`, 'DELETE');

  /** Leaving with lines not yet printed asks first (Tables, Move table). */
  const leaveTo = (go: () => void) => {
    if (newCount > 0 && !readOnly) setLeaving(() => go);
    else go();
  };
  const toTables = () => leaveTo(() => navigate('/pos/tables'));
  const moveTable = () => {
    if (!order.ticketId) return;
    const ticketId = order.ticketId;
    leaveTo(() => navigate(`/pos/tables?move=${ticketId}`));
  };

  const saveCustomer = () => {
    if (!ticket) return;
    if (customer.trim() !== (ticket.customerName ?? ''))
      void act(() => order.write('', 'PATCH', { customerName: customer.trim() })).finally(() =>
        setCustomerEdit(null),
      );
    else setCustomerEdit(null);
  };

  const where = isTakeaway
    ? t('pos.order.takeaway')
    : tableLabel(ticket?.tableName ?? draftTable.data ?? '');
  const which = ticket
    ? t('pos.order.ticketNumber', { number: ticket.number })
    : t('pos.order.newTicket');
  const role = me.data?.user.role ?? session?.user.role ?? '';
  const printLabel = sendAndBill
    ? t('pos.order.send', { count: newCount })
    : newCount > 0
      ? t('pos.order.printNew', { count: newCount })
      : canReprint
        ? t('pos.order.print')
        : t('pos.order.reprintNeedsSupervisor');
  const helper = sendAndBill
    ? t('pos.order.helperSend', { count: newCount, to: printing?.dispenseName ?? '' })
    : t('pos.order.helper', { count: newCount, to: printing?.dispenseName ?? '' });

  const failed = useMemo(() => ticket?.failedJobs ?? [], [ticket]);

  return (
    <div className="pos-screen order-screen">
      <TopBar
        start={
          <Button variant="ghost-dark" onClick={toTables}>
            {t('pos.order.tables')}
          </Button>
        }
        title={
          isTakeaway ? (
            <span className="order-title">
              {t('pos.order.takeaway')} ·{' '}
              <label className="order-title__customer">
                <span className="visually-hidden">{t('pos.order.customerName')}</span>
                <input
                  value={customer}
                  placeholder={t('pos.order.customerPlaceholder')}
                  maxLength={60}
                  disabled={readOnly || closed}
                  onChange={(e) => setCustomerEdit(e.target.value)}
                  onBlur={saveCustomer}
                />
              </label>
              {ticket && <span className="order-title__number"> · {which}</span>}
            </span>
          ) : (
            `${where} · ${which}`
          )
        }
        end={
          <span className="order-who">
            {t('pos.whoWhere', {
              terminal: session?.user.terminalName ?? '',
              name: session?.user.name ?? '',
              role,
            })}
          </span>
        }
      />
      <OfflineBanner />
      {lock && !lock.editable && lock.message && <Banner tone="info">{lock.message}</Banner>}
      {closed && <Banner>{t('pos.ticket.notOpen')}</Banner>}
      {failed.map((job) => (
        <Banner
          key={job.id}
          action={{
            label: t('pos.order.retry'),
            onClick: () =>
              void act(async () => {
                await api(`/api/pos/tickets/${order.ticketId}/print-jobs/${job.id}/retry`, {
                  method: 'POST',
                });
                await queries.invalidateQueries({ queryKey: ['ticket', order.ticketId] });
              }),
          }}
        >
          {t(job.kind === 'Bill' ? 'pos.order.billNotPrinted' : 'pos.order.slipNotPrinted', {
            printer: job.printerName,
          })}
        </Banner>
      ))}

      <div className="order-body">
        <section className="order-ticket" aria-label={t('pos.order.ticket')}>
          <ul className="order-lines">
            {lines.length === 0 && <li className="order-lines__empty">{t('pos.order.empty')}</li>}
            {lines.map((line) => (
              <li key={line.id}>
                <button
                  type="button"
                  className={`order-line${line.id === selectedId ? ' order-line--selected' : ''}${line.pending ? ' order-line--pending' : ''}`}
                  aria-pressed={line.id === selectedId}
                  disabled={line.pending}
                  onClick={() => setSelectedId(line.id === selectedId ? null : line.id)}
                >
                  <span className="order-line__qty">{line.quantity}</span>
                  <span className="order-line__main">
                    <span className="order-line__name">{line.name}</span>
                    <span className="order-line__meta">
                      {[line.options, line.note, line.addedBy, time(line.addedAt)]
                        .filter(Boolean)
                        .join(' · ')}
                    </span>
                  </span>
                  <span className="order-line__right">
                    <span className="order-line__amount">{formatAmount(line.gross)}</span>
                    <span className={`order-badge order-badge--${line.status.toLowerCase()}`}>
                      {line.pending
                        ? t('pos.order.saving')
                        : line.status === 'New'
                          ? t('pos.order.new')
                          : t('pos.order.printed')}
                    </span>
                  </span>
                </button>
              </li>
            ))}
          </ul>

          {selected && !selected.pending && (
            <div className="order-selected">
              <span className="order-selected__label">
                {t('pos.order.selected', {
                  name: selected.name,
                  state:
                    selected.status === 'New'
                      ? t('pos.order.stateNew')
                      : t('pos.order.statePrinted'),
                })}
              </span>
              {selected.status === 'New' ? (
                <div className="order-selected__actions">
                  <Button
                    size="lg"
                    aria-label={t('pos.order.less')}
                    disabled={disabled || (selected.quantity <= 1 && !canCancel)}
                    onClick={() => void act(() => setQuantity(selected, selected.quantity - 1))}
                  >
                    −
                  </Button>
                  <Button
                    size="lg"
                    aria-label={t('pos.order.more')}
                    disabled={disabled}
                    onClick={() => void act(() => setQuantity(selected, selected.quantity + 1))}
                  >
                    +
                  </Button>
                  <Button
                    size="lg"
                    disabled={disabled || !canCancel}
                    onClick={() => void act(() => remove(selected))}
                  >
                    {t('pos.order.remove')}
                  </Button>
                </div>
              ) : (
                <div className="order-selected__actions">
                  <Button size="lg" disabled title={t('pos.order.comesWithVoids')}>
                    {t('pos.order.void')}
                  </Button>
                  <Button size="lg" disabled title={t('pos.order.comesWithVoids')}>
                    {t('pos.order.gift')}
                  </Button>
                </div>
              )}
            </div>
          )}

          <div className="order-total">
            <span>{t('pos.order.total')}</span>
            <span className="order-total__amount">{formatMoney(total, currency)}</span>
          </div>
          <div className="order-buttons">
            {sendAndBill ? (
              <>
                <Button
                  variant="primary"
                  size="lg"
                  disabled={disabled || order.pending > 0 || newCount === 0 || !ticket}
                  onClick={() => void act(send)}
                >
                  {printLabel}
                </Button>
                <Button
                  size="lg"
                  disabled={disabled || order.pending > 0 || !ticket}
                  onClick={() => void act(printBill)}
                >
                  {t('pos.order.printBill')}
                </Button>
              </>
            ) : (
              <Button
                variant="primary"
                size="lg"
                disabled={
                  disabled || order.pending > 0 || !ticket || (newCount === 0 && !canReprint)
                }
                onClick={() => void act(newCount > 0 ? print : reprint)}
              >
                {printLabel}
              </Button>
            )}
            <Button
              size="lg"
              disabled={
                disabled || order.pending > 0 || !ticket || !canReprint || ticket.billNumber === 0
              }
              onClick={() => void act(reprint)}
            >
              {canReprint ? t('pos.order.reprintBill') : t('pos.order.reprintNeedsSupervisor')}
            </Button>
            <Button
              size="lg"
              disabled={!canSettle || !ticket || disabled}
              onClick={() => navigate(`/pos/settle/${order.ticketId}`)}
            >
              {canSettle ? t('pos.order.settle') : t('pos.order.settleCashierOnly')}
            </Button>
          </div>
          {printing && <p className="order-helper">{helper}</p>}
        </section>

        <nav className="order-categories" aria-label={t('pos.order.categories')}>
          {categories.map((c) => (
            <button
              key={c.id}
              type="button"
              className={`order-category${c.id === category?.id && !query.trim() ? ' order-category--on' : ''}`}
              aria-pressed={c.id === category?.id && !query.trim()}
              onClick={() => {
                setCategoryId(c.id);
                setQuery('');
              }}
            >
              {c.name}
            </button>
          ))}
          <span className="order-categories__spacer" />
          <Button size="lg" block disabled={disabled || !ticket} onClick={() => setNoteOpen(true)}>
            {t('pos.order.ticketNote')}
          </Button>
          <Button size="lg" block disabled={disabled || !ticket || isTakeaway} onClick={moveTable}>
            {t('pos.order.moveTable')}
          </Button>
        </nav>

        <section className="order-menu" aria-label={t('pos.order.menu')}>
          <label className="order-search">
            <span className="visually-hidden">{t('pos.order.search')}</span>
            <input
              type="search"
              inputMode="search"
              enterKeyHint="search"
              placeholder={t('pos.order.searchPlaceholder')}
              value={query}
              onChange={(e) => setQuery(e.target.value)}
            />
          </label>
          <div className="order-items">
            {items.map((item) => (
              <button
                key={item.id}
                type="button"
                className="order-item"
                disabled={disabled}
                onClick={() => tapItem(item)}
              >
                <span className="order-item__name">{item.name}</span>
                <span className="order-item__price">{formatMoney(item.price, currency)}</span>
              </button>
            ))}
            {query.trim() && items.length === 0 && (
              <p className="order-empty">{t('pos.order.noMatch')}</p>
            )}
          </div>
          <p className="order-hint">{t('pos.order.optionsHint')}</p>
        </section>
      </div>

      {choosing && (
        <OptionsDialog
          item={choosing}
          currency={currency}
          onCancel={() => setChoosing(null)}
          onAdd={(optionIds, quantity) => {
            order.add(choosing, optionIds, quantity);
            setChoosing(null);
          }}
        />
      )}
      {leaving && (
        <LeaveDialog
          count={newCount}
          printLabel={
            sendAndBill ? t('pos.order.send', { count: newCount }) : t('pos.order.printNow')
          }
          busy={order.busy}
          onStay={() => setLeaving(null)}
          onLeave={() => {
            const go = leaving;
            setLeaving(null);
            go();
          }}
          onPrint={() => {
            const go = leaving;
            void (sendAndBill ? send() : print())
              .then(() => {
                setLeaving(null);
                go();
              })
              .catch((error: unknown) => {
                setLeaving(null);
                order.fail(error);
              });
          }}
        />
      )}
      {noteOpen && (
        <NoteDialog
          note={ticket?.note ?? ''}
          onClose={() => setNoteOpen(false)}
          onSave={(note) => {
            setNoteOpen(false);
            void act(() => order.write('', 'PATCH', { note }));
          }}
        />
      )}
    </div>
  );
}

/** For the Settle button until Task 8. */
export function SettlePlaceholder() {
  const navigate = useNavigate();
  const { id } = useParams();
  return (
    <div className="pos-screen">
      <TopBar
        start={
          <Button variant="ghost-dark" onClick={() => navigate(`/pos/ticket/${id}`)}>
            {t('pos.order.backToTicket')}
          </Button>
        }
        title={t('pos.order.settle')}
      />
      <main className="pos-center">
        <p className="pos-center__text">{t('pos.order.settleComing')}</p>
      </main>
    </div>
  );
}
