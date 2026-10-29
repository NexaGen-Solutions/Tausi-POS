import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useMemo, useState } from 'react';
import { useNavigate, useParams, useSearchParams } from 'react-router';
import { api, type ApprovalKind } from '../../shared/api/client';
import type { Brand, DeviceInfo, Me } from '../../shared/api/types';
import { OfflineBanner } from '../../shared/connection/OfflineBanner';
import { isOffline } from '../../shared/connection/status';
import { useConnection } from '../../shared/connection/useConnection';
import { useLogout } from '../useLogout';
import { usePosRules } from '../../shared/device/usePosRules';
import { formatAmount, formatMoney } from '../../shared/format/money';
import { t } from '../../shared/i18n/i18n';
import { Banner, Button, TopBar } from '../../shared/ui';
import { ApprovalsButton } from '../approvals/ApprovalsButton';
import { tableLabel } from '../tables/tableState';
import type { OrderLine, PendingRequest, TableMap } from '../tables/types';
import { tableMapKey } from '../tables/useTableMap';
import { CloseVoidedButton } from '../ticket/CloseVoidedButton';
import { isFullyVoided } from '../ticket/closeVoided';
import { useTicketLock } from '../ticket/useTicketLock';
import { usePinSession } from '../usePinSession';
import {
  DiscountDialog,
  GiftDialog,
  PriceDialog,
  VoidDialog,
  type ActionHandlers,
  type PermissionValue,
} from './ActionDialogs';
import { LeaveDialog } from './LeaveDialog';
import { needsChoice, search, type PosItem, type PosMenu } from './menu';
import { NoteDialog } from './NoteDialog';
import { OptionsDialog } from './OptionsDialog';
import { useOrder, type ShownLine } from './useOrder';
import { useToast } from '../../shared/ui';
import './order.css';

const time = (iso: string) =>
  new Date(iso).toLocaleTimeString('en-GB', { hour: '2-digit', minute: '2-digit' });

/** What a line's badge says: Saving, New, Printed, Voided, Gift, or "Void pending". */
function badge(line: ShownLine): { text: string; tone: string } {
  if (line.saving) return { text: t('pos.order.saving'), tone: 'new' };
  if (line.pending) return { text: t(`pos.actions.pending.${line.pending.kind}`), tone: 'pending' };
  if (line.status === 'Voided') return { text: t('pos.order.voided'), tone: 'voided' };
  if (line.isGift) return { text: t('pos.order.giftBadge'), tone: 'gift' };
  return line.status === 'New'
    ? { text: t('pos.order.new'), tone: 'new' }
    : { text: t('pos.order.printed'), tone: 'printed' };
}

type Action = { type: 'void' | 'gift' | 'price'; line: OrderLine } | { type: 'discount' };

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
  const [action, setAction] = useState<Action | null>(null);
  const toast = useToast();

  const currency = brand.data?.currency ?? 'KES';
  const printing = device.data?.printing ?? null;
  const sendAndBill = printing?.mode === 'SendAndBill';
  const permission = (key: string): PermissionValue => me.data?.permissions[key] ?? 'NotAllowed';
  const readOnly = lock !== null && !lock.editable;
  const closed = ticket !== null && ticket.status !== 'Open';
  const disabled = readOnly || closed || offline;
  const isTakeaway = ticket ? ticket.tableId === null : params.get('takeaway') === '1';

  const categories = menu.data?.categories ?? [];
  const category = categories.find((c) => c.id === categoryId) ?? categories[0] ?? null;
  const items = query.trim() && menu.data ? search(menu.data, query) : (category?.items ?? []);
  const lines = order.lines;
  const newCount = lines.filter((l) => l.status === 'New').length;
  const counted = lines.filter((l) => l.status !== 'Voided');
  const subtotal = counted.reduce((sum, l) => sum + l.gross, 0);
  const discount = counted.reduce((sum, l) => sum + (l.discount ?? 0), 0);
  const total = subtotal - discount;
  const selected = lines.find((l) => l.id === selectedId && l.status !== 'Voided') ?? null;
  const ticketPending = ticket?.pending ?? [];
  const canReprint = permission('ticket.reprint') !== 'NotAllowed';
  const canCancel = permission('order.cancel_unprinted') !== 'NotAllowed';
  // Settling can't be approved for someone else: only Allowed counts.
  const canSettle = permission('ticket.settle') === 'Allowed';
  const rules = usePosRules();
  const logout = useLogout();
  const canVoid = permission('order.void') !== 'NotAllowed';
  const canGift = permission('order.gift') !== 'NotAllowed';
  const canDiscount = permission('order.discount') !== 'NotAllowed';
  const canChangePrice = permission('order.change_price') !== 'NotAllowed';

  const tapItem = (item: PosItem) => {
    if (disabled) return;
    if (needsChoice(item)) setChoosing(item);
    else order.add(item, [], 1);
  };

  const act = (run: () => Promise<unknown>) => run().catch(order.fail);
  // pos.logoutAfterPrint: once the slips are queued, back to the PIN pad.
  const printed = async (path: string) => {
    const detail = await order.write(path, 'POST');
    if (detail && rules.logoutAfterPrint) await logout();
  };
  const print = () => printed('/print');
  const send = () => printed('/send');
  const printBill = () => printed('/print-bill');
  // With a supervisor elsewhere, the approval pad can send a reprint request instead.
  const reprint = () =>
    order.write('/reprint-bill', 'POST', undefined, {
      approval: { kind: 'Reprint', ticketId: order.ticketId!, payload: { reason: null } },
    });

  /** Void, gift, price change and discount: done now (maybe with a PIN token), or sent as a request. */
  const handlers = (kind: ApprovalKind, path: string, lineId: string | null): ActionHandlers => ({
    onDo: async (body, approvalToken) => {
      await order.write(path, 'POST', body, { approvalToken });
      setAction(null);
    },
    onSend: async (body, preferredApproverId) => {
      const sent = await api<{ summary: string }>('/api/pos/approvals', {
        method: 'POST',
        body: { kind, ticketId: order.ticketId, lineId, payload: body, preferredApproverId },
      });
      toast(t('approval.requestSent', { what: sent.summary }));
      setAction(null);
      await queries.invalidateQueries({ queryKey: ['ticket', order.ticketId] });
      await queries.invalidateQueries({ queryKey: ['approvals'] });
    },
    onCancel: () => setAction(null),
  });
  const cancelRequest = (request: PendingRequest) =>
    act(async () => {
      await api(`/api/pos/approvals/${request.id}/cancel`, { method: 'POST' });
      await queries.invalidateQueries({ queryKey: ['ticket', order.ticketId] });
      await queries.invalidateQueries({ queryKey: ['approvals'] });
    });
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
          <>
            <ApprovalsButton />
            <span className="order-who">
              {t('pos.whoWhere', {
                terminal: session?.user.terminalName ?? '',
                name: session?.user.name ?? '',
                role,
              })}
            </span>
          </>
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
            {lines.map((line) => {
              const voided = line.status === 'Voided';
              const shown = badge(line);
              return (
                <li key={line.id}>
                  <button
                    type="button"
                    className={`order-line${line.id === selectedId ? ' order-line--selected' : ''}${line.saving ? ' order-line--saving' : ''}${voided ? ' order-line--voided' : ''}`}
                    aria-pressed={line.id === selectedId}
                    disabled={line.saving || voided}
                    onClick={() => setSelectedId(line.id === selectedId ? null : line.id)}
                  >
                    <span className="order-line__qty">{line.quantity}</span>
                    <span className="order-line__main">
                      <span className="order-line__name">{line.name}</span>
                      <span className="order-line__meta">
                        {[
                          line.options,
                          line.note,
                          voided ? line.voidReason : null,
                          line.addedBy,
                          time(line.addedAt),
                        ]
                          .filter(Boolean)
                          .join(' · ')}
                      </span>
                      {line.originalUnitPrice != null && !voided && (
                        <span className="order-line__changed">{t('pos.order.priceChanged')}</span>
                      )}
                    </span>
                    <span className="order-line__right">
                      <span className="order-line__amount">
                        {line.isGift && (
                          <s className="order-line__was">
                            {formatAmount(line.unitPrice * line.quantity)}
                          </s>
                        )}
                        {formatAmount(line.gross)}
                      </span>
                      <span className={`order-badge order-badge--${shown.tone}`}>{shown.text}</span>
                    </span>
                  </button>
                  {line.pending?.mine && (
                    <button
                      type="button"
                      className="order-line__cancel"
                      onClick={() => void cancelRequest(line.pending!)}
                    >
                      {t('approval.cancelRequest')}
                    </button>
                  )}
                </li>
              );
            })}
          </ul>

          {selected && !selected.saving && (
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
              {selected.pending ? (
                <p className="order-selected__waiting">{t('pos.actions.waiting')}</p>
              ) : selected.status === 'New' ? (
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
                  {canGift && !selected.isGift && (
                    <Button
                      size="lg"
                      disabled={disabled}
                      onClick={() => setAction({ type: 'gift', line: selected })}
                    >
                      {t('pos.order.gift')}
                    </Button>
                  )}
                  {canChangePrice && !selected.isGift && (
                    <Button
                      size="lg"
                      disabled={disabled}
                      onClick={() => setAction({ type: 'price', line: selected })}
                    >
                      {t('pos.order.changePrice')}
                    </Button>
                  )}
                </div>
              ) : (
                <div className="order-selected__actions">
                  {canVoid && (
                    <Button
                      size="lg"
                      disabled={disabled}
                      onClick={() => setAction({ type: 'void', line: selected })}
                    >
                      {t('pos.order.void')}
                    </Button>
                  )}
                  {canGift && !selected.isGift && (
                    <Button
                      size="lg"
                      disabled={disabled}
                      onClick={() => setAction({ type: 'gift', line: selected })}
                    >
                      {t('pos.order.gift')}
                    </Button>
                  )}
                  {canChangePrice && !selected.isGift && (
                    <Button
                      size="lg"
                      disabled={disabled}
                      onClick={() => setAction({ type: 'price', line: selected })}
                    >
                      {t('pos.order.changePrice')}
                    </Button>
                  )}
                </div>
              )}
            </div>
          )}

          {ticketPending.map((p) => (
            <div key={p.id} className="order-pending" role="status">
              <span>
                {t('pos.actions.ticketPending', { what: p.summary, name: p.requestedBy })}
              </span>
              {p.mine && (
                <button
                  type="button"
                  className="order-line__cancel"
                  onClick={() => void cancelRequest(p)}
                >
                  {t('approval.cancelRequest')}
                </button>
              )}
            </div>
          ))}
          {discount > 0 && (
            <div className="order-subtotals">
              <span>{t('pos.order.subtotal')}</span>
              <span className="order-subtotals__amount">{formatMoney(subtotal, currency)}</span>
              <span>{ticket?.discountLabel ?? t('pos.order.discount')}</span>
              <span className="order-subtotals__amount">−{formatMoney(discount, currency)}</span>
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
                disabled ||
                order.pending > 0 ||
                !ticket ||
                !canReprint ||
                ticket.billNumber === 0 ||
                newCount > 0
              }
              onClick={() => void act(reprint)}
            >
              {canReprint ? t('pos.order.reprintBill') : t('pos.order.reprintNeedsSupervisor')}
            </Button>
            {canSettle && rules.closeVoidedWithoutReceipt && isFullyVoided(ticket) ? (
              <CloseVoidedButton ticketId={order.ticketId!} disabled={disabled} />
            ) : (
              <Button
                size="lg"
                disabled={!canSettle || !ticket || disabled}
                onClick={() => navigate(`/pos/ticket/${order.ticketId}/settle`)}
              >
                {canSettle ? t('pos.order.settle') : t('pos.order.settleCashierOnly')}
              </Button>
            )}
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
          {canDiscount && (
            <Button
              size="lg"
              block
              disabled={disabled || !ticket || ticketPending.some((p) => p.kind === 'Discount')}
              onClick={() => setAction({ type: 'discount' })}
            >
              {t('pos.order.discount')}
            </Button>
          )}
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
      {action?.type === 'void' && (
        <VoidDialog
          line={action.line}
          where={where}
          dispenseName={printing?.dispenseName ?? ''}
          permission={permission('order.void')}
          handlers={handlers('Void', `/lines/${action.line.id}/void`, action.line.id)}
        />
      )}
      {action?.type === 'gift' && (
        <GiftDialog
          line={action.line}
          permission={permission('order.gift')}
          handlers={handlers('Gift', `/lines/${action.line.id}/gift`, action.line.id)}
        />
      )}
      {action?.type === 'price' && (
        <PriceDialog
          line={action.line}
          currency={currency}
          permission={permission('order.change_price')}
          handlers={handlers('PriceChange', `/lines/${action.line.id}/price`, action.line.id)}
        />
      )}
      {action?.type === 'discount' && ticket && (
        <DiscountDialog
          subtotal={subtotal - counted.filter((l) => l.isGift).reduce((s, l) => s + l.gross, 0)}
          currency={currency}
          hasDiscount={discount > 0}
          permission={permission('order.discount')}
          handlers={handlers('Discount', '/discount', null)}
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
