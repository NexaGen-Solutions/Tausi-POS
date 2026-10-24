import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useEffect, useMemo, useState } from 'react';
import { useNavigate, useParams } from 'react-router';
import { ApiError, api } from '../../shared/api/client';
import type { Brand, DeviceInfo } from '../../shared/api/types';
import { OfflineBanner } from '../../shared/connection/OfflineBanner';
import { onPosEvent } from '../../shared/connection/hub';
import { isOffline } from '../../shared/connection/status';
import { useConnection } from '../../shared/connection/useConnection';
import { usePosRules } from '../../shared/device/usePosRules';
import { formatAmount, formatMoney } from '../../shared/format/money';
import { t } from '../../shared/i18n/i18n';
import { endPinSession } from '../../shared/session/session';
import { Banner, Button, Keypad, Modal, TopBar, useToast } from '../../shared/ui';
import { tableLabel } from '../tables/tableState';
import type { OrderLine, Payment, TableEvent, TicketDetail } from '../tables/types';
import { CloseVoidedButton } from '../ticket/CloseVoidedButton';
import { isFullyVoided } from '../ticket/closeVoided';
import { useTicketLock } from '../ticket/useTicketLock';
import { usePinSession } from '../usePinSession';
import { StartShift } from '../cash/StartShift';
import { currentShiftKey, type CurrentShift } from '../cash/types';
import { quickCash } from './quickCash';
import './settle.css';

type PayType = 'Cash' | 'Mpesa' | 'Card';

interface BroughtBy {
  userId: string;
  name: string;
  role: string;
  isDefault: boolean;
}

interface SettleResult {
  ticketId: string;
  receiptNumber: number;
  total: number;
  change: number;
  logOut: boolean;
}

/** The bill as printed: identical lines added up, voids left off, gifts at 0. */
function billLines(lines: OrderLine[]) {
  const shown = new Map<
    string,
    { quantity: number; name: string; amount: number; gift: boolean }
  >();
  for (const l of lines) {
    if (l.status === 'Voided' || l.status === 'Cancelled') continue;
    const name = l.options ? `${l.name} (${l.options})` : l.name;
    const key = `${name}|${l.unitPrice}|${l.isGift ? 1 : 0}`;
    const row = shown.get(key) ?? { quantity: 0, name, amount: 0, gift: !!l.isGift };
    row.quantity += l.quantity;
    row.amount += l.gross;
    shown.set(key, row);
  }
  return [...shown.values()];
}

const typeName = (type: PayType) => t(`pos.settle.type.${type}`);

/** Wireframe 7: the cashier settles a ticket. Payments are saved as they're added. */
export function SettlePage() {
  const { id = '' } = useParams();
  const navigate = useNavigate();
  const queries = useQueryClient();
  const toast = useToast();
  const session = usePinSession();
  const offline = isOffline(useConnection());
  const lock = useTicketLock(id);
  const brand = useQuery({ queryKey: ['brand'], queryFn: () => api<Brand>('/api/brand') });
  const currency = brand.data?.currency ?? 'KES';
  const ticketQuery = useQuery({
    queryKey: ['ticket', id],
    queryFn: () => api<TicketDetail>(`/api/pos/tickets/${id}`),
    staleTime: 0,
  });
  const ticket = ticketQuery.data ?? null;
  // Settling needs the cashier's shift on this terminal: without one, "Start your shift" comes first.
  const shiftQuery = useQuery({
    queryKey: currentShiftKey,
    queryFn: () => api<CurrentShift>('/api/pos/shifts/current'),
    staleTime: 0,
  });
  const needsShift = shiftQuery.data !== undefined && shiftQuery.data.shift === null;
  const rules = usePosRules();
  const device = useQuery({ queryKey: ['device'], queryFn: () => api<DeviceInfo>('/api/device') });
  const sendAndBill = device.data?.printing?.mode === 'SendAndBill';
  const closable = rules.closeVoidedWithoutReceipt && isFullyVoided(ticketQuery.data);
  const broughtBy = useQuery({
    queryKey: ['brought-by', id],
    queryFn: () => api<BroughtBy[]>(`/api/pos/tickets/${id}/brought-by`),
  });

  useEffect(
    () =>
      onPosEvent('ticket.changed', (payload) => {
        if ((payload as TableEvent).ticketId === id)
          void queries.invalidateQueries({ queryKey: ['ticket', id] });
      }),
    [id, queries],
  );

  const [type, setType] = useState<PayType>('Cash');
  const [received, setReceived] = useState('');
  const [code, setCode] = useState('');
  const [amount, setAmount] = useState('');
  const [reference, setReference] = useState('');
  const [by, setBy] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [confirming, setConfirming] = useState(false);
  const [unprinted, setUnprinted] = useState<string | null>(null);

  const remaining = ticket?.remaining ?? 0;
  const payments: Payment[] = ticket?.payments ?? [];
  const readOnly = (lock !== null && !lock.editable) || ticket?.status !== 'Open';
  const disabled = readOnly || offline || busy;
  const broughtById = by ?? ticket?.defaultBroughtById ?? broughtBy.data?.[0]?.userId ?? null;
  const receivedValue = Number(received || 0);
  const change = Math.max(0, receivedValue - remaining);
  const totalChange = payments.reduce((sum, p) => sum + (p.change ?? 0), 0);
  const newLines = ticket?.newLines ?? 0;
  const lines = useMemo(() => billLines(ticket?.lines ?? []), [ticket]);
  const discount = ticket?.discountTotal ?? 0;

  const fail = (error: unknown) =>
    toast(error instanceof ApiError ? error.message : t('common.somethingWrong'), {
      tone: 'warning',
    });

  const run = async (work: () => Promise<void>) => {
    setBusy(true);
    try {
      await work();
    } catch (error) {
      fail(error);
    } finally {
      setBusy(false);
    }
  };

  const save = (detail: TicketDetail) => queries.setQueryData(['ticket', id], detail);

  const addPayment = (body: Record<string, unknown>, reset: () => void) =>
    run(async () => {
      save(
        await api<TicketDetail>(`/api/pos/tickets/${id}/payments`, {
          method: 'POST',
          // Not recorded: the server fills in the waiter who opened the ticket.
          body: { ...body, broughtByUserId: rules.recordBroughtBy ? broughtById : null },
        }),
      );
      reset();
    });

  const addCash = () =>
    addPayment({ type: 'Cash', tendered: receivedValue }, () => setReceived(''));
  const addMpesa = () =>
    addPayment(
      { type: 'Mpesa', mpesaCode: code || null, amount: amount ? Number(amount) : remaining },
      () => {
        setCode('');
        setAmount('');
      },
    );
  const addCard = () =>
    addPayment(
      {
        type: 'Card',
        cardReference: reference || null,
        amount: amount ? Number(amount) : remaining,
      },
      () => {
        setReference('');
        setAmount('');
      },
    );
  const remove = (payment: Payment) =>
    run(async () => {
      save(
        await api<TicketDetail>(`/api/pos/tickets/${id}/payments/${payment.id}`, {
          method: 'DELETE',
        }),
      );
    });

  const printFirst = () =>
    run(async () => {
      // Send and Print bill mode: only the new items go to the bar, as the Send button does.
      save(
        await api<TicketDetail>(`/api/pos/tickets/${id}/${sendAndBill ? 'send' : 'print'}`, {
          method: 'POST',
        }),
      );
      setUnprinted(null);
    });

  const complete = () =>
    run(async () => {
      setConfirming(false);
      try {
        const result = await api<SettleResult>(`/api/pos/tickets/${id}/settle`, { method: 'POST' });
        toast(t('pos.settle.done', { number: result.receiptNumber }));
        void queries.invalidateQueries({ queryKey: ['table-map'] });
        void queries.invalidateQueries({ queryKey: ['settled'] });
        if (result.logOut) endPinSession('logout');
        else navigate('/pos/tables');
      } catch (error) {
        if (error instanceof ApiError && error.problem.code === 'unprinted') {
          setUnprinted(error.message);
          return;
        }
        throw error;
      }
    });

  const where = ticket
    ? ticket.tableId
      ? tableLabel(ticket.tableName ?? '')
      : ticket.customerName
        ? `${t('pos.order.takeaway')} · ${ticket.customerName}`
        : t('pos.order.takeaway')
    : '';
  const user = session?.user;
  const people = broughtBy.data ?? [];
  const byLabel = (p: BroughtBy) =>
    p.isDefault && ticket?.tableId
      ? t('pos.settle.waiterOn', { name: p.name, where })
      : `${p.name} (${p.role})`;
  const broughtBySelect = (label: string) =>
    rules.recordBroughtBy && (
      <label className="settle-field">
        <span>{label}</span>
        <select
          value={broughtById ?? ''}
          onChange={(e) => setBy(e.target.value)}
          disabled={disabled}
        >
          {people.map((p) => (
            <option key={p.userId} value={p.userId}>
              {byLabel(p)}
            </option>
          ))}
        </select>
      </label>
    );
  const amountField = (
    <label className="settle-field">
      <span>{t('pos.settle.amount')}</span>
      <input
        className="settle-money-input"
        inputMode="decimal"
        value={amount}
        placeholder={formatAmount(remaining)}
        onChange={(e) => setAmount(e.target.value.replace(/[^\d.]/g, ''))}
        disabled={disabled}
      />
    </label>
  );

  return (
    <div className="pos-screen settle-screen">
      <TopBar
        start={
          <Button variant="ghost-dark" onClick={() => navigate(`/pos/ticket/${id}`)}>
            {t('pos.order.backToTicket')}
          </Button>
        }
        title={
          ticket ? t('pos.settle.title', { where, number: ticket.number }) : t('pos.order.settle')
        }
        end={
          user && (
            <span className="order-who">
              {t('pos.whoWhere', {
                terminal: user.terminalName ?? '',
                name: user.name,
                role: user.role,
              })}
            </span>
          )
        }
      />
      <OfflineBanner />
      {lock && !lock.editable && lock.message && <Banner tone="info">{lock.message}</Banner>}
      {ticket && ticket.status !== 'Open' && <Banner>{t('pos.ticket.notOpen')}</Banner>}
      {(unprinted || newLines > 0) && ticket?.status === 'Open' && (
        <Banner
          action={{ label: t('pos.settle.printThenSettle'), onClick: () => void printFirst() }}
        >
          {unprinted ??
            (newLines === 1
              ? t('pos.order.unprintedOne')
              : t('pos.order.unprinted', { count: newLines }))}
        </Banner>
      )}

      <div className="settle-body">
        <section className="settle-bill" aria-label={t('pos.settle.bill')}>
          <h2 className="settle-heading">{t('pos.settle.bill')}</h2>
          <ul className="settle-lines">
            {lines.map((l) => (
              <li key={`${l.name}-${l.gift}`} className="settle-line">
                <span>
                  {l.quantity} × {l.name}
                  {l.gift && <span className="settle-gift"> {t('pos.settle.gift')}</span>}
                </span>
                <span className="settle-amount">{formatAmount(l.amount)}</span>
              </li>
            ))}
          </ul>
          <div className="settle-totals">
            {discount > 0 && (
              <>
                <div className="settle-row">
                  <span>{t('pos.order.subtotal')}</span>
                  <span className="settle-amount">{formatAmount(ticket?.subtotal ?? 0)}</span>
                </div>
                <div className="settle-row">
                  <span>{ticket?.discountLabel ?? t('pos.order.discount')}</span>
                  <span className="settle-amount">−{formatAmount(discount)}</span>
                </div>
              </>
            )}
            {(ticket?.vatLines ?? []).map((v) => (
              <div key={v.label} className="settle-row settle-row--small">
                <span>{v.label}</span>
                <span className="settle-amount">{formatAmount(v.amount)}</span>
              </div>
            ))}
            <div className="settle-row settle-row--total">
              <span>{t('pos.order.total')}</span>
              <span className="settle-total">{formatMoney(ticket?.total ?? 0, currency)}</span>
            </div>
            {(ticket?.rounding ?? 0) !== 0 && (
              <div className="settle-row settle-row--small">
                <span>{t('pos.settle.rounding')}</span>
                <span className="settle-amount">
                  {(ticket!.rounding ?? 0) > 0 ? '+' : '−'}
                  {Math.abs(ticket!.rounding ?? 0).toFixed(2)}
                </span>
              </div>
            )}
            <ul className="settle-payments" aria-label={t('pos.settle.payments')}>
              {payments.map((p) => (
                <li key={p.id} className="settle-row">
                  <span>
                    {p.type === 'Mpesa'
                      ? `${typeName('Mpesa')} ${p.mpesaCode || t('pos.settle.noCode')}`
                      : p.type === 'Card'
                        ? `${typeName('Card')}${p.cardReference ? ` · ${p.cardReference}` : ''}`
                        : rules.recordBroughtBy
                          ? `${typeName('Cash')} (${p.broughtBy})`
                          : typeName('Cash')}
                  </span>
                  <span className="settle-amount">
                    − {formatAmount(p.amount)}
                    {p.status === 'Pending' && !readOnly && (
                      <button
                        type="button"
                        className="settle-remove"
                        aria-label={t('pos.settle.remove', {
                          what: typeName(p.type),
                          amount: formatAmount(p.amount),
                        })}
                        disabled={disabled}
                        onClick={() => void remove(p)}
                      >
                        ×
                      </button>
                    )}
                  </span>
                </li>
              ))}
            </ul>
            <div
              className={`settle-row settle-remaining${remaining === 0 ? ' settle-remaining--paid' : ''}`}
              role="status"
            >
              <span>{remaining === 0 ? t('pos.settle.paid') : t('pos.settle.remaining')}</span>
              {remaining !== 0 && (
                <span className="settle-amount">{formatMoney(remaining, currency)}</span>
              )}
            </div>
          </div>
        </section>

        {closable ? (
          <section className="settle-pay" aria-label={t('pos.settle.closeTicket')}>
            <h3 className="settle-form__title">{t('pos.settle.allVoided')}</h3>
            <p className="settle-note">{t('pos.settle.allVoidedNote')}</p>
            <CloseVoidedButton ticketId={id} disabled={readOnly || offline} />
          </section>
        ) : needsShift && shiftQuery.data ? (
          <section className="settle-pay" aria-label={t('pos.cash.startYourShift')}>
            <StartShift
              title={t('pos.cash.startYourShift')}
              defaultFloat={shiftQuery.data.start.defaultFloat}
              denominations={shiftQuery.data.start.denominations}
            />
          </section>
        ) : (
          <section className="settle-pay" aria-label={t('pos.settle.payment')}>
            <div className="settle-tabs" role="group" aria-label={t('pos.settle.paymentType')}>
              {(['Cash', 'Mpesa', 'Card'] as const).map((k) => (
                <button
                  key={k}
                  type="button"
                  className={`settle-tab${type === k ? ' settle-tab--on' : ''}`}
                  aria-pressed={type === k}
                  onClick={() => setType(k)}
                >
                  {typeName(k)}
                </button>
              ))}
            </div>

            <div className="settle-form">
              {type === 'Cash' && (
                <div className="settle-cash">
                  <div className="settle-cash__main">
                    <h3 className="settle-form__title">
                      {t('pos.settle.cashFor', { amount: formatMoney(remaining, currency) })}
                    </h3>
                    <div className="settle-quick">
                      {quickCash(remaining).map((v) => (
                        <button
                          key={v}
                          type="button"
                          className="settle-quick__button"
                          disabled={disabled}
                          onClick={() => setReceived(String(v))}
                        >
                          {formatAmount(v)}
                        </button>
                      ))}
                    </div>
                    {broughtBySelect(t('pos.settle.cashBroughtBy'))}
                    <label className="settle-field">
                      <span>{t('pos.settle.cashReceived')}</span>
                      <input
                        className="settle-money-input"
                        inputMode="decimal"
                        value={received}
                        onChange={(e) => setReceived(e.target.value.replace(/[^\d.]/g, ''))}
                        disabled={disabled}
                      />
                    </label>
                    <div className="settle-change">
                      <span>{t('pos.settle.change')}</span>
                      <span className="settle-change__amount" aria-live="polite">
                        {formatAmount(change)}
                      </span>
                    </div>
                    <Button
                      variant="dark"
                      size="lg"
                      block
                      disabled={disabled || receivedValue <= 0 || remaining <= 0}
                      onClick={() => void addCash()}
                    >
                      {t('pos.settle.addCash')}
                    </Button>
                  </div>
                  <div className="settle-cash__pad">
                    <Keypad
                      variant="compact"
                      onDigit={(d) =>
                        setReceived((r) => (r.length < 8 ? (r === '0' ? d : r + d) : r))
                      }
                      onClear={() => setReceived('')}
                      onEnter={() => setReceived((r) => r.slice(0, -1))}
                      enterLabel={t('pos.actions.backspace')}
                      enterDisabled={received === '' || disabled}
                      disabled={disabled}
                    />
                  </div>
                </div>
              )}
              {type === 'Mpesa' && (
                <>
                  <label className="settle-field">
                    <span>
                      {rules.mpesaCodeRequired
                        ? t('pos.settle.mpesaCode')
                        : t('pos.settle.mpesaCodeOptional')}
                    </span>
                    <input
                      className="settle-money-input"
                      value={code}
                      maxLength={10}
                      autoCapitalize="characters"
                      onChange={(e) =>
                        setCode(e.target.value.toUpperCase().replace(/[^A-Z0-9]/g, ''))
                      }
                      disabled={disabled}
                    />
                  </label>
                  {amountField}
                  {broughtBySelect(t('pos.settle.broughtBy'))}
                  <Button
                    variant="dark"
                    size="lg"
                    block
                    disabled={
                      disabled ||
                      (rules.mpesaCodeRequired
                        ? code.length !== 10
                        : ![0, 10].includes(code.length)) ||
                      remaining <= 0
                    }
                    onClick={() => void addMpesa()}
                  >
                    {t('pos.settle.addMpesa')}
                  </Button>
                  <p className="settle-note">{t('pos.settle.mpesaNote')}</p>
                </>
              )}
              {type === 'Card' && (
                <>
                  {amountField}
                  <label className="settle-field">
                    <span>{t('pos.settle.cardReference')}</span>
                    <input
                      value={reference}
                      maxLength={40}
                      onChange={(e) => setReference(e.target.value)}
                      disabled={disabled}
                    />
                  </label>
                  {broughtBySelect(t('pos.settle.broughtBy'))}
                  <Button
                    variant="dark"
                    size="lg"
                    block
                    disabled={disabled || remaining <= 0}
                    onClick={() => void addCard()}
                  >
                    {t('pos.settle.addCard')}
                  </Button>
                </>
              )}
            </div>

            <div className="settle-footer">
              <p className="settle-note">{t('pos.settle.receiptNote')}</p>
              <Button
                variant="primary"
                size="lg"
                disabled={disabled || !ticket || remaining !== 0 || ticket.status !== 'Open'}
                onClick={() => (totalChange > 0 ? setConfirming(true) : void complete())}
              >
                {t('pos.settle.complete')}
              </Button>
            </div>
          </section>
        )}
      </div>

      {confirming && (
        <Modal title={t('pos.settle.giveChange')} onClose={() => setConfirming(false)} width={480}>
          <p className="settle-give-change">{formatMoney(totalChange, currency)}</p>
          <div className="settle-confirm">
            <Button size="lg" onClick={() => setConfirming(false)}>
              {t('common.cancel')}
            </Button>
            <Button variant="primary" size="lg" disabled={busy} onClick={() => void complete()}>
              {t('pos.settle.complete')}
            </Button>
          </div>
        </Modal>
      )}
    </div>
  );
}
