import { useQuery } from '@tanstack/react-query';
import { useState, type ReactNode } from 'react';
import { ApiError, api } from '../../shared/api/client';
import { formatAmount } from '../../shared/format/money';
import { t } from '../../shared/i18n/i18n';
import { Button, Field, Modal, SelectField } from '../../shared/ui';
import type { Delivery, Supplier } from '../stock/types';
import { todayIso } from './dates';
import {
  moneyAccounts,
  type ExpenseCategory,
  type MoneyAccount,
  type MoneyDocument,
} from './types';
import '../stock/stock.css';
import './money.css';

/** Money in each account at the end of the day shown, for the "goes below 0" warning. */
export type Balances = Record<MoneyAccount, number>;

type Done = (document: MoneyDocument | null) => void;

const receiptTypes = ['image/jpeg', 'image/png', 'application/pdf'];
const receiptMaxBytes = 5 * 1024 * 1024;

const amountOf = (text: string) => (text.trim() === '' ? 0 : Number(text));
const decimalOnly = (text: string) => text.replace(/[^\d.]/g, '');

/** Saves a money action; the server's date and amount errors go next to their fields. */
function useSave(onDone: Done) {
  const [error, setError] = useState<string | null>(null);
  const [fieldErrors, setFieldErrors] = useState<{ date?: string; amount?: string }>({});
  const [busy, setBusy] = useState(false);
  const run = async (save: () => Promise<MoneyDocument>) => {
    setBusy(true);
    setError(null);
    setFieldErrors({});
    try {
      onDone(await save());
    } catch (e) {
      if (!(e instanceof ApiError)) setError(t('common.somethingWrong'));
      else if (e.problem.code === 'date_future' || e.problem.code === 'date_too_old')
        setFieldErrors({ date: e.message });
      else if (e.problem.code === 'overpaid') setFieldErrors({ amount: e.message });
      else setError(e.message);
    } finally {
      setBusy(false);
    }
  };
  const post = (path: string, body: unknown) =>
    run(() => api<MoneyDocument>(path, { method: 'POST', body }));
  return { error, fieldErrors, busy, run, post };
}

function DateField({
  value,
  onChange,
  error,
}: {
  value: string;
  onChange: (value: string) => void;
  error?: string;
}) {
  const future = value > todayIso();
  return (
    <Field
      label={t('admin.cashbook.form.date')}
      type="date"
      value={value}
      max={todayIso()}
      onChange={(e) => onChange(e.target.value)}
      error={future ? t('admin.cashbook.form.dateFuture') : error}
    />
  );
}

function AmountField({
  label = t('admin.cashbook.form.amount'),
  value,
  onChange,
  error,
  hint,
}: {
  label?: string;
  value: string;
  onChange: (value: string) => void;
  error?: string | null;
  hint?: string;
}) {
  return (
    <Field
      label={label}
      className="money-amount"
      value={value}
      inputMode="decimal"
      hint={hint}
      error={error}
      onChange={(e) => onChange(decimalOnly(e.target.value))}
    />
  );
}

function AccountChoice({
  label,
  value,
  onChange,
  except,
}: {
  label: string;
  value: MoneyAccount;
  onChange: (value: MoneyAccount) => void;
  except?: MoneyAccount;
}) {
  return (
    <div className="nx-field">
      <span className="nx-field__label" id={`acct-${label}`}>
        {label}
      </span>
      <div className="stock-reasons" role="group" aria-labelledby={`acct-${label}`}>
        {moneyAccounts
          .filter((a) => a !== except)
          .map((a) => (
            <button
              key={a}
              type="button"
              className={`stock-choice${value === a ? ' stock-choice--on' : ''}`}
              aria-pressed={value === a}
              onClick={() => onChange(a)}
            >
              {t(`admin.cashbook.account.${a}`)}
            </button>
          ))}
      </div>
    </div>
  );
}

function FormShell({
  title,
  subtitle,
  onClose,
  error,
  ready,
  busy,
  saveLabel,
  onSave,
  children,
}: {
  title: string;
  subtitle?: string;
  onClose: () => void;
  error: string | null;
  ready: boolean;
  busy: boolean;
  saveLabel: string;
  onSave: () => void;
  children: ReactNode;
}) {
  return (
    <Modal title={title} subtitle={subtitle} onClose={onClose} width={560}>
      <form
        className="stock-form"
        onSubmit={(e) => {
          e.preventDefault();
          if (ready && !busy) onSave();
        }}
      >
        {children}
        {error && (
          <p className="nx-field__error" role="alert">
            {error}
          </p>
        )}
        <div className="stock-dialog__buttons">
          <Button onClick={onClose}>{t('common.cancel')}</Button>
          <Button type="submit" variant="primary" disabled={!ready || busy}>
            {saveLabel}
          </Button>
        </div>
      </form>
    </Modal>
  );
}

/** Add expense: paid from the Safe, Bank or M-Pesa, with an optional receipt photo (cash.expense). */
export function ExpenseDialog({ onDone }: { onDone: Done }) {
  const categories = useQuery({
    queryKey: ['cashbook', 'categories'],
    queryFn: () => api<ExpenseCategory[]>('/api/admin/money/categories'),
  });
  const active = (categories.data ?? []).filter((c) => c.active);
  const [date, setDate] = useState(todayIso());
  const [amount, setAmount] = useState('');
  const [categoryId, setCategoryId] = useState('');
  const [paidFrom, setPaidFrom] = useState<MoneyAccount>('Safe');
  const [payee, setPayee] = useState('');
  const [reference, setReference] = useState('');
  const [note, setNote] = useState('');
  const [file, setFile] = useState<File | null>(null);
  const [fileError, setFileError] = useState<string | null>(null);
  const { error, fieldErrors, busy, run } = useSave(onDone);
  const category = categoryId || active[0]?.id || '';
  const ready = amountOf(amount) > 0 && category !== '' && date <= todayIso() && !fileError;

  const pick = (chosen: File | undefined) => {
    setFile(null);
    setFileError(null);
    if (!chosen) return;
    if (!receiptTypes.includes(chosen.type))
      return setFileError(t('admin.cashbook.form.receiptType'));
    if (chosen.size > receiptMaxBytes) return setFileError(t('admin.cashbook.form.receiptSize'));
    setFile(chosen);
  };

  const save = () =>
    run(async () => {
      let attachmentFileId: string | null = null;
      if (file) {
        const form = new FormData();
        form.append('file', file);
        attachmentFileId = (
          await api<{ fileId: string }>('/api/admin/money/attachments', {
            method: 'POST',
            body: form,
          })
        ).fileId;
      }
      return api<MoneyDocument>('/api/admin/money/expenses', {
        method: 'POST',
        body: {
          date,
          amount: amountOf(amount),
          categoryId: category,
          paidFrom,
          payee: payee.trim() || null,
          reference: reference.trim() || null,
          note: note.trim() || null,
          attachmentFileId,
        },
      });
    });

  return (
    <FormShell
      title={t('admin.cashbook.addExpense')}
      subtitle={t('admin.cashbook.form.drawerHelp')}
      onClose={() => onDone(null)}
      error={error}
      ready={ready}
      busy={busy}
      saveLabel={t('admin.cashbook.form.saveExpense')}
      onSave={() => void save()}
    >
      <div className="stock-form__row">
        <DateField value={date} onChange={setDate} error={fieldErrors.date} />
        <AmountField value={amount} onChange={setAmount} error={fieldErrors.amount} />
      </div>
      <SelectField
        label={t('admin.cashbook.form.category')}
        value={category}
        onChange={(e) => setCategoryId(e.target.value)}
        options={active.map((c) => ({ value: c.id, label: c.name }))}
      />
      <AccountChoice
        label={t('admin.cashbook.form.paidFrom')}
        value={paidFrom}
        onChange={setPaidFrom}
      />
      <div className="stock-form__row">
        <Field
          label={t('admin.cashbook.form.payee')}
          value={payee}
          maxLength={120}
          onChange={(e) => setPayee(e.target.value)}
        />
        <Field
          label={t('admin.cashbook.form.reference')}
          value={reference}
          maxLength={60}
          onChange={(e) => setReference(e.target.value)}
        />
      </div>
      <Field
        label={t('admin.cashbook.form.note')}
        value={note}
        maxLength={200}
        onChange={(e) => setNote(e.target.value)}
      />
      <Field
        label={t('admin.cashbook.form.receipt')}
        type="file"
        accept=".jpg,.jpeg,.png,.pdf,image/jpeg,image/png,application/pdf"
        hint={t('admin.cashbook.form.receiptHint')}
        error={fileError}
        onChange={(e) => pick(e.target.files?.[0])}
      />
    </FormShell>
  );
}

/** Pay supplier: never more than what's owed; optionally against specific deliveries. */
export function PaySupplierDialog({ onDone }: { onDone: Done }) {
  const suppliers = useQuery({
    queryKey: ['stock', 'suppliers'],
    queryFn: () => api<Supplier[]>('/api/admin/suppliers'),
  });
  const deliveries = useQuery({
    queryKey: ['deliveries'],
    queryFn: () => api<Delivery[]>('/api/admin/deliveries'),
  });
  const list = (suppliers.data ?? []).filter((s) => s.active || s.owed > 0);
  const [supplierId, setSupplierId] = useState('');
  const supplier = list.find((s) => s.id === supplierId) ?? list.find((s) => s.owed > 0) ?? list[0];
  const [date, setDate] = useState(todayIso());
  const [amount, setAmount] = useState('');
  const [paidFrom, setPaidFrom] = useState<MoneyAccount>('Bank');
  const [reference, setReference] = useState('');
  const [linked, setLinked] = useState<string[]>([]);
  const { error, fieldErrors, busy, post } = useSave(onDone);
  const owed = supplier?.owed ?? 0;
  const overpaid =
    supplier && amountOf(amount) > owed
      ? t('admin.cashbook.form.overpaid', { name: supplier.name, owed: formatAmount(owed) })
      : null;
  const credit = (deliveries.data ?? []).filter(
    (d) =>
      d.supplierId === supplier?.id &&
      d.payment === 'OnCredit' &&
      d.status === 'Posted' &&
      d.reversalStatus !== 'Posted',
  );
  const ready = !!supplier && amountOf(amount) > 0 && !overpaid && date <= todayIso();

  return (
    <FormShell
      title={t('admin.cashbook.paySupplier')}
      onClose={() => onDone(null)}
      error={error}
      ready={ready}
      busy={busy}
      saveLabel={t('admin.cashbook.form.savePayment')}
      onSave={() =>
        void post('/api/admin/money/supplier-payments', {
          supplierId: supplier!.id,
          amount: amountOf(amount),
          paidFrom,
          reference: reference.trim() || null,
          date,
          deliveryIds: linked.length ? linked : null,
        })
      }
    >
      <SelectField
        label={t('admin.cashbook.form.supplier')}
        value={supplier?.id ?? ''}
        onChange={(e) => {
          setSupplierId(e.target.value);
          setLinked([]);
        }}
        options={list.map((s) => ({ value: s.id, label: s.name }))}
      />
      <p className="money-owed">
        {t('admin.cashbook.form.owed')} <span className="mono">KSh {formatAmount(owed)}</span>
      </p>
      <div className="stock-form__row">
        <AmountField value={amount} onChange={setAmount} error={overpaid ?? fieldErrors.amount} />
        <DateField value={date} onChange={setDate} error={fieldErrors.date} />
      </div>
      <AccountChoice
        label={t('admin.cashbook.form.paidFrom')}
        value={paidFrom}
        onChange={setPaidFrom}
      />
      <Field
        label={t('admin.cashbook.form.reference')}
        value={reference}
        maxLength={60}
        onChange={(e) => setReference(e.target.value)}
      />
      {credit.length > 0 && (
        <fieldset className="money-deliveries">
          <legend className="nx-field__label">{t('admin.cashbook.form.forDeliveries')}</legend>
          {credit.map((d) => (
            <label key={d.id} className="money-check">
              <input
                type="checkbox"
                checked={linked.includes(d.id)}
                onChange={(e) =>
                  setLinked((ids) =>
                    e.target.checked ? [...ids, d.id] : ids.filter((i) => i !== d.id),
                  )
                }
              />
              {t('admin.cashbook.form.deliveryLine', {
                invoice: d.invoiceNumber,
                date: d.date,
                total: formatAmount(d.total),
              })}
            </label>
          ))}
        </fieldset>
      )}
    </FormShell>
  );
}

/** Move money between the Safe, Bank and M-Pesa, with an optional fee. Below 0 warns, never blocks. */
export function MoveMoneyDialog({
  from: initialFrom = 'Safe',
  to: initialTo = 'Bank',
  title,
  balances,
  onDone,
}: {
  from?: MoneyAccount;
  to?: MoneyAccount;
  title: string;
  balances: Balances;
  onDone: Done;
}) {
  const [from, setFrom] = useState<MoneyAccount>(initialFrom);
  const [to, setTo] = useState<MoneyAccount>(initialTo);
  const [date, setDate] = useState(todayIso());
  const [amount, setAmount] = useState('');
  const [fee, setFee] = useState('');
  const [reference, setReference] = useState('');
  const { error, fieldErrors, busy, post } = useSave(onDone);
  const leaves = balances[from] - amountOf(amount) - amountOf(fee);
  const ready = from !== to && amountOf(amount) > 0 && date <= todayIso();

  return (
    <FormShell
      title={title}
      onClose={() => onDone(null)}
      error={error}
      ready={ready}
      busy={busy}
      saveLabel={t('admin.cashbook.form.saveMove')}
      onSave={() =>
        void post('/api/admin/money/moves', {
          from,
          to,
          amount: amountOf(amount),
          fee: amountOf(fee) > 0 ? amountOf(fee) : null,
          reference: reference.trim() || null,
          date,
        })
      }
    >
      <AccountChoice
        label={t('admin.cashbook.form.from')}
        value={from}
        onChange={(a) => {
          setFrom(a);
          if (a === to) setTo(moneyAccounts.find((x) => x !== a)!);
        }}
      />
      <AccountChoice
        label={t('admin.cashbook.form.to')}
        value={to}
        onChange={setTo}
        except={from}
      />
      <div className="stock-form__row">
        <AmountField value={amount} onChange={setAmount} error={fieldErrors.amount} />
        <AmountField
          label={t('admin.cashbook.form.fee')}
          value={fee}
          onChange={setFee}
          hint={t('admin.cashbook.form.feeHint')}
        />
      </div>
      {amountOf(amount) > 0 && leaves < 0 && (
        <p className="money-warning" role="status">
          {t('admin.cashbook.form.belowZero', {
            account: t(`admin.cashbook.account.${from}`),
            amount: formatAmount(-leaves),
          })}
        </p>
      )}
      <div className="stock-form__row">
        <Field
          label={t('admin.cashbook.form.reference')}
          value={reference}
          maxLength={60}
          onChange={(e) => setReference(e.target.value)}
        />
        <DateField value={date} onChange={setDate} error={fieldErrors.date} />
      </div>
    </FormShell>
  );
}

/** The owner took money out (drawings) or put money in (capital). */
export function OwnerMoneyDialog({ onDone }: { onDone: Done }) {
  const [tookOut, setTookOut] = useState(true);
  const [account, setAccount] = useState<MoneyAccount>('Safe');
  const [date, setDate] = useState(todayIso());
  const [amount, setAmount] = useState('');
  const [note, setNote] = useState('');
  const { error, fieldErrors, busy, post } = useSave(onDone);
  const ready = amountOf(amount) > 0 && date <= todayIso();

  return (
    <FormShell
      title={t('admin.cashbook.ownerMoney')}
      onClose={() => onDone(null)}
      error={error}
      ready={ready}
      busy={busy}
      saveLabel={t('common.save')}
      onSave={() =>
        void post('/api/admin/money/owner', {
          tookOut,
          account,
          amount: amountOf(amount),
          note: note.trim() || null,
          date,
        })
      }
    >
      <div className="stock-reasons" role="group" aria-label={t('admin.cashbook.ownerMoney')}>
        {[true, false].map((out) => (
          <button
            key={String(out)}
            type="button"
            className={`stock-choice${tookOut === out ? ' stock-choice--on' : ''}`}
            aria-pressed={tookOut === out}
            onClick={() => setTookOut(out)}
          >
            {out ? t('admin.cashbook.form.ownerTook') : t('admin.cashbook.form.ownerPut')}
          </button>
        ))}
      </div>
      <AccountChoice
        label={tookOut ? t('admin.cashbook.form.from') : t('admin.cashbook.form.into')}
        value={account}
        onChange={setAccount}
      />
      <div className="stock-form__row">
        <AmountField value={amount} onChange={setAmount} error={fieldErrors.amount} />
        <DateField value={date} onChange={setDate} error={fieldErrors.date} />
      </div>
      <Field
        label={t('admin.cashbook.form.note')}
        value={note}
        maxLength={200}
        onChange={(e) => setNote(e.target.value)}
      />
    </FormShell>
  );
}

/** Money received outside the POS, e.g. rent from a vendor. */
export function OtherIncomeDialog({ onDone }: { onDone: Done }) {
  const [to, setTo] = useState<MoneyAccount>('Safe');
  const [date, setDate] = useState(todayIso());
  const [amount, setAmount] = useState('');
  const [payee, setPayee] = useState('');
  const [note, setNote] = useState('');
  const { error, fieldErrors, busy, post } = useSave(onDone);
  const ready = amountOf(amount) > 0 && note.trim() !== '' && date <= todayIso();

  return (
    <FormShell
      title={t('admin.cashbook.otherIncome')}
      onClose={() => onDone(null)}
      error={error}
      ready={ready}
      busy={busy}
      saveLabel={t('common.save')}
      onSave={() =>
        void post('/api/admin/money/other-income', {
          to,
          amount: amountOf(amount),
          note: note.trim(),
          payee: payee.trim() || null,
          date,
        })
      }
    >
      <AccountChoice label={t('admin.cashbook.form.into')} value={to} onChange={setTo} />
      <div className="stock-form__row">
        <AmountField value={amount} onChange={setAmount} error={fieldErrors.amount} />
        <DateField value={date} onChange={setDate} error={fieldErrors.date} />
      </div>
      <Field
        label={t('admin.cashbook.form.fromWhom')}
        value={payee}
        maxLength={120}
        onChange={(e) => setPayee(e.target.value)}
      />
      <Field
        label={t('admin.cashbook.form.noteRequired')}
        value={note}
        maxLength={200}
        onChange={(e) => setNote(e.target.value)}
      />
    </FormShell>
  );
}

/** Opening balances: once only (settings.manage). After saving, changing them needs a reversal. */
export function OpeningBalancesDialog({ onDone }: { onDone: Done }) {
  const locked = useQuery({
    queryKey: ['cashbook', 'opening'],
    queryFn: () => api<{ locked: boolean }>('/api/admin/money/opening-balances'),
  });
  const suppliers = useQuery({
    queryKey: ['stock', 'suppliers'],
    queryFn: () => api<Supplier[]>('/api/admin/suppliers'),
  });
  const [money, setMoney] = useState<Record<MoneyAccount, string>>({
    Safe: '',
    Bank: '',
    Mpesa: '',
  });
  const [owed, setOwed] = useState<Record<string, string>>({});
  const [date, setDate] = useState(todayIso());
  const { error, fieldErrors, busy, post } = useSave(onDone);
  const total =
    moneyAccounts.reduce((sum, a) => sum + amountOf(money[a]), 0) +
    Object.values(owed).reduce((sum, v) => sum + amountOf(v), 0);
  const isLocked = locked.data?.locked ?? false;

  if (isLocked)
    return (
      <Modal title={t('admin.cashbook.openingBalances')} onClose={() => onDone(null)} width={520}>
        <p>{t('admin.cashbook.form.openingLocked')}</p>
        <div className="stock-dialog__buttons">
          <Button onClick={() => onDone(null)}>{t('common.close')}</Button>
        </div>
      </Modal>
    );

  return (
    <FormShell
      title={t('admin.cashbook.openingBalances')}
      subtitle={t('admin.cashbook.form.openingHelp')}
      onClose={() => onDone(null)}
      error={error}
      ready={!locked.isLoading && total > 0 && date <= todayIso()}
      busy={busy}
      saveLabel={t('admin.cashbook.form.saveOpening')}
      onSave={() =>
        void post('/api/admin/money/opening-balances', {
          safe: amountOf(money.Safe) || null,
          bank: amountOf(money.Bank) || null,
          mpesa: amountOf(money.Mpesa) || null,
          suppliers: Object.entries(owed)
            .filter(([, v]) => amountOf(v) > 0)
            .map(([supplierId, v]) => ({ supplierId, amount: amountOf(v) })),
          date,
        })
      }
    >
      <div className="money-opening">
        {moneyAccounts.map((a) => (
          <AmountField
            key={a}
            label={t(`admin.cashbook.account.${a}`)}
            value={money[a]}
            onChange={(v) => setMoney((m) => ({ ...m, [a]: v }))}
          />
        ))}
      </div>
      {(suppliers.data ?? []).length > 0 && (
        <fieldset className="money-deliveries">
          <legend className="nx-field__label">{t('admin.cashbook.form.openingOwed')}</legend>
          <div className="money-opening">
            {(suppliers.data ?? [])
              .filter((s) => s.active)
              .map((s) => (
                <AmountField
                  key={s.id}
                  label={s.name}
                  value={owed[s.id] ?? ''}
                  onChange={(v) => setOwed((o) => ({ ...o, [s.id]: v }))}
                />
              ))}
          </div>
        </fieldset>
      )}
      <DateField value={date} onChange={setDate} error={fieldErrors.date} />
    </FormShell>
  );
}
