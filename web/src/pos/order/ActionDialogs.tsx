import { useState, type ReactNode } from 'react';
import { ApiError, RequestSentError, type ApprovalKind } from '../../shared/api/client';
import { ApprovalChoices } from '../../shared/approval/ApprovalChoices';
import { usePosRules } from '../../shared/device/usePosRules';
import { permissionFor } from '../../shared/approval/types';
import { formatMoney } from '../../shared/format/money';
import { t } from '../../shared/i18n/i18n';
import { Button, Keypad, Modal, useToast } from '../../shared/ui';
import type { OrderLine } from '../tables/types';

export type PermissionValue = 'Allowed' | 'NeedsApproval' | 'NotAllowed';

/** How the action is done: now (with a PIN token when one was typed), or as a request. */
export interface ActionHandlers {
  onDo: (body: unknown, approvalToken?: string) => Promise<void>;
  onSend: (body: unknown, preferredApproverId: string | null) => Promise<void>;
  onCancel: () => void;
}

/**
 * Wireframe 5's layout, shared by void, gift, discount and price change: the form on the left,
 * and on the right a single button for users who may do it, or the two approval options for
 * users who need a supervisor.
 */
function ActionDialog({
  title,
  subtitle,
  kind,
  permission,
  doLabel,
  sendLabel,
  body,
  info,
  children,
  handlers,
}: {
  title: string;
  subtitle?: string;
  kind: ApprovalKind;
  permission: PermissionValue;
  doLabel: string;
  sendLabel: string;
  /** The request body, or null while the form isn't complete. */
  body: unknown;
  info?: ReactNode;
  children: ReactNode;
  handlers: ActionHandlers;
}) {
  const [busy, setBusy] = useState(false);
  const toast = useToast();
  const run = async (work: () => Promise<void>) => {
    setBusy(true);
    try {
      await work();
    } catch (error) {
      if (!(error instanceof RequestSentError))
        toast(error instanceof ApiError ? error.message : t('common.somethingWrong'), {
          tone: 'warning',
        });
    } finally {
      setBusy(false);
    }
  };
  const ready = body !== null;

  return (
    <Modal
      title={title}
      subtitle={subtitle}
      onClose={handlers.onCancel}
      width={permission === 'Allowed' ? 640 : 1040}
    >
      <div className={`action-dialog${permission === 'Allowed' ? '' : ' action-dialog--two'}`}>
        <div className="action-dialog__form">
          {children}
          {info && <div className="action-dialog__info">{info}</div>}
          <Button size="lg" block onClick={handlers.onCancel}>
            {t('common.cancel')}
          </Button>
        </div>
        <div className="action-dialog__side">
          {permission === 'Allowed' ? (
            <Button
              variant="primary"
              size="lg"
              block
              disabled={!ready || busy}
              onClick={() => void run(() => handlers.onDo(body))}
            >
              {doLabel}
            </Button>
          ) : (
            <ApprovalChoices
              permission={permissionFor[kind]}
              kind={kind}
              sendLabel={sendLabel}
              disabled={!ready}
              onToken={(token) => handlers.onDo(body, token)}
              onSend={(to) => handlers.onSend(body, to)}
            />
          )}
        </div>
      </div>
    </Modal>
  );
}

function Quantity({
  value,
  max,
  onChange,
}: {
  value: number;
  max: number;
  onChange: (q: number) => void;
}) {
  return (
    <div className="order-options__quantity" role="group" aria-label={t('pos.actions.quantity')}>
      <span>{t('pos.actions.quantity')}</span>
      <Button
        size="lg"
        aria-label={t('pos.order.less')}
        disabled={value <= 1}
        onClick={() => onChange(value - 1)}
      >
        −
      </Button>
      <output className="order-options__count" aria-live="polite">
        {value}
      </output>
      <Button
        size="lg"
        aria-label={t('pos.order.more')}
        disabled={value >= max}
        onClick={() => onChange(value + 1)}
      >
        +
      </Button>
    </div>
  );
}

function Reasons({
  reasons,
  value,
  onChange,
  note,
  onNote,
}: {
  reasons: { value: string; label: string }[];
  value: string;
  onChange: (reason: string) => void;
  note: string;
  onNote: (note: string) => void;
}) {
  return (
    <fieldset className="action-dialog__reasons">
      <legend>{t('pos.actions.reason')}</legend>
      <div className="action-dialog__reason-grid">
        {reasons.map((r) => (
          <button
            key={r.value}
            type="button"
            className={`order-options__choice action-dialog__reason${value === r.value ? ' order-options__choice--on' : ''}`}
            aria-pressed={value === r.value}
            onClick={() => onChange(r.value)}
          >
            {r.label}
          </button>
        ))}
      </div>
      {value === 'Other' && (
        <label className="action-dialog__field">
          <span>{t('pos.actions.note')}</span>
          <input value={note} maxLength={200} onChange={(e) => onNote(e.target.value)} />
        </label>
      )}
    </fieldset>
  );
}

const reasonLabels = (values: string[]) =>
  values.map((value) => ({ value, label: t(`pos.actions.reasons.${value}`) }));

const time = (iso: string | null) =>
  iso ? new Date(iso).toLocaleTimeString('en-GB', { hour: '2-digit', minute: '2-digit' }) : '';

const reasonOk = (reason: string, note: string) =>
  reason !== '' && (reason !== 'Other' || note.trim() !== '');

export function VoidDialog({
  line,
  where,
  dispenseName,
  permission,
  handlers,
}: {
  line: OrderLine;
  where: string;
  dispenseName: string;
  permission: PermissionValue;
  handlers: ActionHandlers;
}) {
  const [quantity, setQuantity] = useState(1);
  const [reason, setReason] = useState('');
  const [note, setNote] = useState('');
  const [poured, setPoured] = useState(false);
  // With stock.returnOnVoidIfNotServed off nobody is asked: every void counts as poured.
  const askPoured = usePosRules().returnOnVoidIfNotServed;
  const spilled = reason === 'Spilled or broken';
  const body = reasonOk(reason, note)
    ? { quantity, reason, note: note.trim() || null, wasPoured: !askPoured || spilled || poured }
    : null;

  return (
    <ActionDialog
      title={t('pos.actions.voidTitle', { quantity, name: line.name })}
      subtitle={t('pos.actions.sentBy', {
        where,
        time: time(line.printedAt ?? line.addedAt),
        name: line.addedBy,
      })}
      kind="Void"
      permission={permission}
      doLabel={t('pos.actions.void')}
      sendLabel={t('pos.actions.sendVoid')}
      body={body}
      handlers={handlers}
      info={
        <>
          <strong>{t('pos.actions.onceApproved')}</strong>
          <p>{t('pos.actions.voidInfo1', { to: dispenseName })}</p>
          <p>{t('pos.actions.voidInfo2')}</p>
          <p>{t('pos.actions.voidInfo3')}</p>
        </>
      }
    >
      {line.quantity > 1 && (
        <Quantity value={quantity} max={line.quantity} onChange={setQuantity} />
      )}
      <Reasons
        reasons={reasonLabels([
          'Wrong item',
          'Customer changed mind',
          'Spilled or broken',
          'Other',
        ])}
        value={reason}
        onChange={setReason}
        note={note}
        onNote={setNote}
      />
      {askPoured && !spilled && (
        <div className="action-dialog__poured" role="group" aria-label={t('pos.actions.poured')}>
          <span>
            {t('pos.actions.poured')}
            {permission === 'NeedsApproval' && (
              <span className="action-dialog__hint"> {t('pos.actions.pouredConfirm')}</span>
            )}
          </span>
          {[false, true].map((yes) => (
            <button
              key={String(yes)}
              type="button"
              className={`order-options__choice action-dialog__yesno${poured === yes ? ' order-options__choice--on' : ''}`}
              aria-pressed={poured === yes}
              onClick={() => setPoured(yes)}
            >
              {yes ? t('common.yes') : t('common.no')}
            </button>
          ))}
        </div>
      )}
    </ActionDialog>
  );
}

export function GiftDialog({
  line,
  permission,
  handlers,
}: {
  line: OrderLine;
  permission: PermissionValue;
  handlers: ActionHandlers;
}) {
  const [quantity, setQuantity] = useState(1);
  const [reason, setReason] = useState('');
  const [note, setNote] = useState('');
  const body = reasonOk(reason, note) ? { quantity, reason, note: note.trim() || null } : null;
  return (
    <ActionDialog
      title={t('pos.actions.giftTitle', { quantity, name: line.name })}
      kind="Gift"
      permission={permission}
      doLabel={t('pos.actions.gift')}
      sendLabel={t('pos.actions.sendGift')}
      body={body}
      handlers={handlers}
      info={<p>{t('pos.actions.giftInfo')}</p>}
    >
      {line.quantity > 1 && (
        <Quantity value={quantity} max={line.quantity} onChange={setQuantity} />
      )}
      <Reasons
        reasons={reasonLabels(['Regular customer', 'House', 'Compensation', 'Other'])}
        value={reason}
        onChange={setReason}
        note={note}
        onNote={setNote}
      />
    </ActionDialog>
  );
}

export function DiscountDialog({
  subtotal,
  currency,
  hasDiscount,
  permission,
  handlers,
}: {
  subtotal: number;
  currency: string;
  hasDiscount: boolean;
  permission: PermissionValue;
  handlers: ActionHandlers;
}) {
  const [kind, setKind] = useState<'Percent' | 'Amount'>('Percent');
  const [value, setValue] = useState('');
  const [reason, setReason] = useState('');
  const [note, setNote] = useState('');
  const amount = Number(value);
  const valid =
    value.trim() !== '' &&
    Number.isFinite(amount) &&
    amount > 0 &&
    (kind === 'Percent' ? amount <= 100 : amount <= subtotal);
  const body =
    valid && reasonOk(reason, note)
      ? { kind, value: amount, reason, note: note.trim() || null }
      : null;
  const toast = useToast();
  const removeDiscount = async () => {
    try {
      await handlers.onDo({ kind: null, value: 0, reason: null });
    } catch (error) {
      toast(error instanceof ApiError ? error.message : t('common.somethingWrong'), {
        tone: 'warning',
      });
    }
  };

  return (
    <ActionDialog
      title={t('pos.actions.discountTitle')}
      subtitle={t('pos.actions.discountOf', { amount: formatMoney(subtotal, currency) })}
      kind="Discount"
      permission={permission}
      doLabel={t('pos.actions.applyDiscount')}
      sendLabel={t('pos.actions.sendDiscount')}
      body={body}
      handlers={handlers}
      info={
        hasDiscount ? (
          <div className="action-dialog__remove">
            <p>{t('pos.actions.replaces')}</p>
            {permission === 'Allowed' && (
              <Button onClick={() => void removeDiscount()}>
                {t('pos.actions.removeDiscount')}
              </Button>
            )}
          </div>
        ) : undefined
      }
    >
      <div className="action-dialog__kind" role="group" aria-label={t('pos.actions.discountKind')}>
        {(['Percent', 'Amount'] as const).map((k) => (
          <button
            key={k}
            type="button"
            className={`order-options__choice action-dialog__yesno${kind === k ? ' order-options__choice--on' : ''}`}
            aria-pressed={kind === k}
            onClick={() => setKind(k)}
          >
            {k === 'Percent'
              ? t('pos.actions.percent')
              : t('pos.actions.amount', { currency: currency === 'KES' ? 'KSh' : currency })}
          </button>
        ))}
      </div>
      <label className="action-dialog__field">
        <span>
          {kind === 'Percent' ? t('pos.actions.percentLabel') : t('pos.actions.amountLabel')}
        </span>
        <input
          inputMode="decimal"
          value={value}
          onChange={(e) => setValue(e.target.value.replace(/[^\d.]/g, ''))}
        />
      </label>
      {value !== '' && !valid && (
        <p className="action-dialog__error">
          {kind === 'Percent' ? t('pos.actions.percentLimit') : t('pos.actions.amountLimit')}
        </p>
      )}
      <Reasons
        reasons={reasonLabels(['Regular customer', 'Promotion', 'Compensation', 'Other'])}
        value={reason}
        onChange={setReason}
        note={note}
        onNote={setNote}
      />
    </ActionDialog>
  );
}

export function PriceDialog({
  line,
  currency,
  permission,
  handlers,
}: {
  line: OrderLine;
  currency: string;
  permission: PermissionValue;
  handlers: ActionHandlers;
}) {
  const [quantity, setQuantity] = useState(line.quantity);
  const [price, setPrice] = useState('');
  const [reason, setReason] = useState('');
  const amount = Number(price);
  const valid = price !== '' && Number.isFinite(amount) && amount >= 0 && amount !== line.unitPrice;
  const body =
    valid && reason.trim() !== ''
      ? { quantity, newUnitPrice: amount, reason: reason.trim() }
      : null;

  return (
    <ActionDialog
      title={t('pos.actions.priceTitle', { name: line.name })}
      subtitle={t('pos.actions.currentPrice', { price: formatMoney(line.unitPrice, currency) })}
      kind="PriceChange"
      permission={permission}
      doLabel={t('pos.actions.changePrice')}
      sendLabel={t('pos.actions.sendPrice')}
      body={body}
      handlers={handlers}
    >
      {line.quantity > 1 && (
        <Quantity value={quantity} max={line.quantity} onChange={setQuantity} />
      )}
      <div className="action-dialog__price">
        <span>{t('pos.actions.newPrice')}</span>
        <output className="action-dialog__price-value" aria-label={t('pos.actions.newPrice')}>
          {price === '' ? '—' : formatMoney(amount, currency)}
        </output>
      </div>
      <Keypad
        variant="compact"
        onDigit={(d) => setPrice((p) => (p.length < 8 ? (p === '0' ? d : p + d) : p))}
        onClear={() => setPrice('')}
        onEnter={() => setPrice((p) => p.slice(0, -1))}
        enterLabel={t('pos.actions.backspace')}
        enterDisabled={price === ''}
      />
      <label className="action-dialog__field">
        <span>{t('pos.actions.priceReason')}</span>
        <input value={reason} maxLength={200} onChange={(e) => setReason(e.target.value)} />
      </label>
    </ActionDialog>
  );
}
