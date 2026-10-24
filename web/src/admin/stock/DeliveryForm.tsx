import { useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { ApiError, api } from '../../shared/api/client';
import { formatAmount } from '../../shared/format/money';
import { t } from '../../shared/i18n/i18n';
import { Button, Field, SelectField } from '../../shared/ui';
import { unitsOf } from './units';
import type { Delivery, DeliveryPayment, StockItemRow, StockLocation, Supplier } from './types';

interface Line {
  key: number;
  itemId: string;
  unit: string;
  quantity: string;
  cost: string;
}

const today = () => {
  const d = new Date();
  return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`;
};

const largest = (item: StockItemRow | undefined) => (item ? unitsOf(item).at(-1)! : '');

/**
 * Wireframe 12: receive a delivery. Each line defaults to the item's largest pack and to the last
 * cost paid this supplier for it; the invoice number is checked per supplier when saved.
 */
export function DeliveryForm({
  items,
  locations,
  suppliers,
  onDone,
}: {
  items: StockItemRow[];
  locations: StockLocation[];
  suppliers: Supplier[];
  onDone: (saved: Delivery | null) => void;
}) {
  const queries = useQueryClient();
  const active = items.filter((i) => i.active);
  const byId = new Map(items.map((i) => [i.id, i]));
  const [supplier, setSupplier] = useState(suppliers.find((s) => s.active)?.id ?? '');
  const [invoice, setInvoice] = useState('');
  const [date, setDate] = useState(today());
  const [into, setInto] = useState(
    locations.find((l) => l.name === 'Store')?.id ?? locations[0]?.id ?? '',
  );
  const [lines, setLines] = useState<Line[]>([]);
  const [payment, setPayment] = useState<'OnCredit' | 'Cash' | 'Transfer'>('OnCredit');
  const [via, setVia] = useState<'Mpesa' | 'Bank'>('Mpesa');
  const [reference, setReference] = useState('');
  const [errors, setErrors] = useState<{ invoice?: string; form?: string }>({});
  const [busy, setBusy] = useState(false);
  const [nextKey, setNextKey] = useState(1);

  const total = lines.reduce(
    (sum, l) => sum + Math.round(Number(l.quantity || 0) * Number(l.cost || 0) * 100) / 100,
    0,
  );
  const paid: DeliveryPayment = payment === 'Transfer' ? via : payment;
  const ready =
    supplier !== '' &&
    invoice.trim() !== '' &&
    into !== '' &&
    lines.length > 0 &&
    lines.every((l) => l.itemId && Number(l.quantity) > 0 && l.cost !== '') &&
    (payment !== 'Transfer' || reference.trim() !== '');

  /** The last cost paid this supplier for this item and pack, filled in when there is one. */
  const suggest = async (key: number, itemId: string, unit: string, forSupplier = supplier) => {
    if (!forSupplier || !itemId) return;
    const last = await queries.fetchQuery({
      queryKey: ['stock', 'last-cost', forSupplier, itemId, unit],
      queryFn: () =>
        api<{ unitCost: number | null }>(
          `/api/admin/deliveries/last-cost?supplierId=${forSupplier}&stockItemId=${itemId}&unit=${encodeURIComponent(unit)}`,
        ),
    });
    if (last.unitCost != null)
      setLines((ls) =>
        ls.map((l) =>
          l.key === key && l.itemId === itemId && l.unit === unit
            ? { ...l, cost: String(last.unitCost) }
            : l,
        ),
      );
  };

  const addLine = () => {
    const item = active[0];
    const line = {
      key: nextKey,
      itemId: item?.id ?? '',
      unit: largest(item),
      quantity: '',
      cost: '',
    };
    setNextKey((k) => k + 1);
    setLines((ls) => [...ls, line]);
    void suggest(line.key, line.itemId, line.unit);
  };
  const change = (key: number, update: Partial<Line>) => {
    setLines((ls) => ls.map((l) => (l.key === key ? { ...l, ...update } : l)));
    const line = lines.find((l) => l.key === key)!;
    const itemId = update.itemId ?? line.itemId;
    const unit = update.unit ?? line.unit;
    if (update.itemId !== undefined || update.unit !== undefined) {
      setLines((ls) => ls.map((l) => (l.key === key ? { ...l, cost: '' } : l)));
      void suggest(key, itemId, unit);
    }
  };

  const save = async () => {
    setBusy(true);
    setErrors({});
    try {
      const saved = await api<Delivery>('/api/admin/deliveries', {
        method: 'POST',
        body: {
          supplierId: supplier,
          invoiceNumber: invoice.trim(),
          date,
          locationId: into,
          payment: paid,
          paymentReference: payment === 'Transfer' ? reference.trim() : null,
          lines: lines.map((l) => ({
            stockItemId: l.itemId,
            unit: l.unit,
            quantity: Number(l.quantity),
            unitCost: Number(l.cost),
          })),
        },
      });
      onDone(saved);
    } catch (e) {
      if (e instanceof ApiError && e.problem.code === 'invoice_used')
        setErrors({ invoice: e.message });
      else setErrors({ form: e instanceof ApiError ? e.message : t('common.somethingWrong') });
    } finally {
      setBusy(false);
    }
  };

  return (
    <section className="stock__main" aria-label={t('admin.deliveries.receive')}>
      <div className="stock__header">
        <h1 className="admin__title">{t('admin.deliveries.receive')}</h1>
        <Button onClick={() => onDone(null)}>{t('common.cancel')}</Button>
      </div>
      <div className="stock-form__row">
        <SelectField
          label={t('admin.deliveries.supplier')}
          value={supplier}
          onChange={(e) => {
            setSupplier(e.target.value);
            lines.forEach((l) => void suggest(l.key, l.itemId, l.unit, e.target.value));
          }}
          options={suppliers.filter((s) => s.active).map((s) => ({ value: s.id, label: s.name }))}
        />
        <Field
          label={t('admin.deliveries.invoice')}
          value={invoice}
          maxLength={40}
          error={errors.invoice}
          onChange={(e) => setInvoice(e.target.value)}
        />
        <Field
          label={t('admin.deliveries.date')}
          type="date"
          value={date}
          max={today()}
          onChange={(e) => setDate(e.target.value)}
        />
        <SelectField
          label={t('admin.deliveries.into')}
          value={into}
          onChange={(e) => setInto(e.target.value)}
          options={locations.map((l) => ({ value: l.id, label: l.name }))}
        />
      </div>

      <div className="nx-table-box">
        <table className="nx-table">
          <caption className="visually-hidden">{t('admin.deliveries.lines')}</caption>
          <thead>
            <tr>
              <th scope="col">{t('admin.stock.item')}</th>
              <th scope="col">{t('admin.stock.unit')}</th>
              <th scope="col" className="nx-table--right">
                {t('admin.stock.quantity')}
              </th>
              <th scope="col" className="nx-table--right">
                {t('admin.deliveries.unitCost')}
              </th>
              <th scope="col" className="nx-table--right">
                {t('admin.deliveries.lineTotal')}
              </th>
              <th scope="col">
                <span className="visually-hidden">{t('admin.stock.removeLine')}</span>
              </th>
            </tr>
          </thead>
          <tbody>
            {lines.map((l, i) => {
              const item = byId.get(l.itemId);
              return (
                <tr key={l.key}>
                  <td>
                    <select
                      className="nx-field__select"
                      aria-label={t('admin.deliveries.lineItem', { n: i + 1 })}
                      value={l.itemId}
                      onChange={(e) =>
                        change(l.key, {
                          itemId: e.target.value,
                          unit: largest(byId.get(e.target.value)),
                        })
                      }
                    >
                      {active.map((x) => (
                        <option key={x.id} value={x.id}>
                          {x.name}
                        </option>
                      ))}
                    </select>
                  </td>
                  <td>
                    <select
                      className="nx-field__select"
                      aria-label={t('admin.deliveries.lineUnit', { n: i + 1 })}
                      value={l.unit}
                      onChange={(e) => change(l.key, { unit: e.target.value })}
                    >
                      {(item ? unitsOf(item) : []).map((u) => (
                        <option key={u} value={u}>
                          {item && u !== item.baseUnit
                            ? `${u} of ${item.packs.find((p) => p.name === u)?.factor}`
                            : u}
                        </option>
                      ))}
                    </select>
                  </td>
                  <td className="nx-table--right">
                    <input
                      className="stock-count-input"
                      aria-label={t('admin.deliveries.lineQuantity', { n: i + 1 })}
                      inputMode="decimal"
                      value={l.quantity}
                      onChange={(e) =>
                        change(l.key, { quantity: e.target.value.replace(/[^\d.]/g, '') })
                      }
                    />
                  </td>
                  <td className="nx-table--right">
                    <input
                      className="stock-count-input stock-cost-input"
                      aria-label={t('admin.deliveries.lineCost', { n: i + 1 })}
                      inputMode="decimal"
                      value={l.cost}
                      onChange={(e) =>
                        change(l.key, { cost: e.target.value.replace(/[^\d.]/g, '') })
                      }
                    />
                  </td>
                  <td className="nx-table--right nx-table--mono">
                    {formatAmount(
                      Math.round(Number(l.quantity || 0) * Number(l.cost || 0) * 100) / 100,
                    )}
                  </td>
                  <td>
                    <Button
                      aria-label={t('admin.deliveries.removeLine', { n: i + 1 })}
                      onClick={() => setLines((ls) => ls.filter((x) => x.key !== l.key))}
                    >
                      ×
                    </Button>
                  </td>
                </tr>
              );
            })}
          </tbody>
        </table>
        <div className="stock-add-line">
          <button type="button" className="stock-recipe__add" onClick={addLine}>
            {t('admin.deliveries.addItem')}
          </button>
        </div>
      </div>

      <div className="stock-bottom">
        <div className="stock-form">
          <span className="nx-field__label">{t('admin.deliveries.payment')}</span>
          <div className="stock-reasons" role="group" aria-label={t('admin.deliveries.payment')}>
            {(['OnCredit', 'Cash', 'Transfer'] as const).map((p) => (
              <button
                key={p}
                type="button"
                className={`stock-choice${payment === p ? ' stock-choice--on' : ''}`}
                aria-pressed={payment === p}
                onClick={() => setPayment(p)}
              >
                {t(`admin.deliveries.pay.${p}`)}
              </button>
            ))}
          </div>
          {payment === 'Transfer' && (
            <div className="stock-form__row">
              <SelectField
                label={t('admin.deliveries.via')}
                value={via}
                onChange={(e) => setVia(e.target.value as 'Mpesa' | 'Bank')}
                options={[
                  { value: 'Mpesa', label: 'M-Pesa' },
                  { value: 'Bank', label: t('admin.deliveries.bank') },
                ]}
              />
              <Field
                label={t('admin.deliveries.reference')}
                value={reference}
                maxLength={60}
                onChange={(e) => setReference(e.target.value)}
              />
            </div>
          )}
          <p className="stock-note">{t(`admin.deliveries.payHelp.${payment}`)}</p>
        </div>
        <div className="stock-panel stock-total-box">
          <div className="stock-total-box__row">
            <span>{t('admin.deliveries.total')}</span>
            <span className="mono" data-testid="delivery-total">
              KSh {formatAmount(total)}
            </span>
          </div>
          {errors.form && (
            <p className="nx-field__error" role="alert">
              {errors.form}
            </p>
          )}
          <Button
            variant="primary"
            size="lg"
            block
            disabled={!ready || busy}
            onClick={() => void save()}
          >
            {t('admin.deliveries.save')}
          </Button>
        </div>
      </div>
    </section>
  );
}
