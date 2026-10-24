import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { ApiError, api } from '../../shared/api/client';
import { formatAmount } from '../../shared/format/money';
import { t } from '../../shared/i18n/i18n';
import { Button, Field, Modal, Table, Toggle, useToast, type Column } from '../../shared/ui';
import { AwaitingApproval } from './AwaitingApproval';
import { DeliveryForm } from './DeliveryForm';
import type { Delivery, StockOverview, Supplier } from './types';
import './stock.css';

const paymentText = (d: Delivery) =>
  t(`admin.deliveries.paid.${d.payment}`) + (d.paymentReference ? ` · ${d.paymentReference}` : '');

const statusText = (d: Delivery) =>
  d.reversalStatus === 'Posted'
    ? t('admin.deliveries.reversed')
    : d.reversalStatus === 'Pending'
      ? t('admin.deliveries.reversalPending')
      : t(`admin.stock.status.${d.status}`);

/** Deliveries: the list, receiving a new one (wireframe 12), a delivery's details with Reverse, and suppliers. */
export function DeliveriesPage() {
  const queries = useQueryClient();
  const toast = useToast();
  const deliveries = useQuery({
    queryKey: ['deliveries'],
    queryFn: () => api<Delivery[]>('/api/admin/deliveries'),
  });
  const suppliers = useQuery({
    queryKey: ['stock', 'suppliers'],
    queryFn: () => api<Supplier[]>('/api/admin/suppliers'),
  });
  const stock = useQuery({
    queryKey: ['stock', 'overview', ''],
    queryFn: () => api<StockOverview>('/api/admin/stock'),
  });
  const [view, setView] = useState<'list' | 'new'>('list');
  const [open, setOpen] = useState<Delivery | null>(null);
  const [managing, setManaging] = useState(false);

  const refresh = () => {
    void queries.invalidateQueries({ queryKey: ['deliveries'] });
    void queries.invalidateQueries({ queryKey: ['stock'] });
  };

  if (view === 'new' && stock.data && suppliers.data)
    return (
      <main className="stock stock--single">
        <DeliveryForm
          items={stock.data.items}
          locations={stock.data.locations}
          suppliers={suppliers.data}
          onDone={(saved) => {
            setView('list');
            if (!saved) return;
            refresh();
            toast(
              saved.status === 'Pending'
                ? t('admin.stock.savedPending')
                : t('admin.deliveries.saved', { total: formatAmount(saved.total) }),
            );
          }}
        />
      </main>
    );

  const columns: Column<Delivery>[] = [
    {
      key: 'date',
      header: t('admin.deliveries.date'),
      render: (d) =>
        new Date(d.date).toLocaleDateString('en-GB', {
          day: 'numeric',
          month: 'short',
          year: 'numeric',
        }),
    },
    {
      key: 'supplier',
      header: t('admin.deliveries.supplier'),
      render: (d) => (
        <button
          type="button"
          className="stock-link"
          onClick={() => setOpen(d)}
          aria-label={t('admin.deliveries.open', {
            supplier: d.supplier,
            invoice: d.invoiceNumber,
          })}
        >
          {d.supplier}
        </button>
      ),
    },
    {
      key: 'invoice',
      header: t('admin.deliveries.invoice'),
      mono: true,
      render: (d) => d.invoiceNumber,
    },
    { key: 'location', header: t('admin.deliveries.into'), render: (d) => d.location },
    {
      key: 'total',
      header: t('admin.deliveries.total'),
      align: 'right',
      mono: true,
      render: (d) => formatAmount(d.total),
    },
    { key: 'payment', header: t('admin.deliveries.payment'), render: paymentText },
    { key: 'status', header: t('admin.stock.statusHeader'), render: statusText },
    { key: 'by', header: t('admin.deliveries.recordedBy'), render: (d) => d.recordedBy },
  ];

  return (
    <main className="stock stock--single">
      <div className="stock__main">
        <div className="stock__header">
          <h1 className="admin__title">{t('admin.deliveries.title')}</h1>
          <div className="stock__buttons">
            <Button onClick={() => setManaging(true)}>
              {t('admin.deliveries.manageSuppliers')}
            </Button>
            <Button
              variant="primary"
              disabled={!stock.data || !suppliers.data}
              onClick={() => setView('new')}
            >
              {t('admin.deliveries.receive')}
            </Button>
          </div>
        </div>
        <AwaitingApproval />
        {deliveries.data && (
          <Table
            caption={t('admin.deliveries.title')}
            columns={columns}
            rows={deliveries.data}
            rowKey={(d) => d.id}
            empty={t('admin.deliveries.none')}
          />
        )}
        {suppliers.data && suppliers.data.some((s) => s.owed > 0) && (
          <p className="stock-note">
            {t('admin.deliveries.owed')}{' '}
            {suppliers.data
              .filter((s) => s.owed > 0)
              .map((s) => `${s.name} KSh ${formatAmount(s.owed)}`)
              .join(' · ')}
          </p>
        )}
      </div>
      {open && (
        <DeliveryDialog
          delivery={open}
          onClose={(changed) => {
            setOpen(null);
            if (changed) refresh();
          }}
        />
      )}
      {managing && suppliers.data && (
        <SuppliersDialog suppliers={suppliers.data} onClose={() => setManaging(false)} />
      )}
    </main>
  );
}

/** A delivery read-only, with Reverse delivery (a reason; stock.adjust). */
function DeliveryDialog({
  delivery,
  onClose,
}: {
  delivery: Delivery;
  onClose: (changed: boolean) => void;
}) {
  const [reversing, setReversing] = useState(false);
  const [reason, setReason] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const reverse = async () => {
    setBusy(true);
    setError(null);
    try {
      await api(`/api/admin/deliveries/${delivery.id}/reverse`, {
        method: 'POST',
        body: { reason },
      });
      onClose(true);
    } catch (e) {
      setError(e instanceof ApiError ? e.message : t('common.somethingWrong'));
    } finally {
      setBusy(false);
    }
  };
  const canReverse =
    delivery.status === 'Posted' &&
    delivery.reversalStatus !== 'Posted' &&
    delivery.reversalStatus !== 'Pending';
  const columns: Column<Delivery['lines'][number]>[] = [
    {
      key: 'item',
      header: t('admin.stock.item'),
      render: (l) => `${l.stockItem} (${l.unit}${l.factor > 1 ? ` of ${l.factor}` : ''})`,
    },
    {
      key: 'qty',
      header: t('admin.stock.quantity'),
      align: 'right',
      mono: true,
      render: (l) => formatAmount(l.quantity),
    },
    {
      key: 'cost',
      header: t('admin.deliveries.unitCost'),
      align: 'right',
      mono: true,
      render: (l) => formatAmount(l.unitCost),
    },
    {
      key: 'total',
      header: t('admin.deliveries.lineTotal'),
      align: 'right',
      mono: true,
      render: (l) => formatAmount(l.lineTotal),
    },
  ];
  return (
    <Modal
      title={t('admin.deliveries.detailTitle', {
        supplier: delivery.supplier,
        invoice: delivery.invoiceNumber,
      })}
      subtitle={`${delivery.location} · ${paymentText(delivery)} · ${statusText(delivery)} · ${t('admin.deliveries.recordedByName', { name: delivery.recordedBy })}`}
      onClose={() => onClose(false)}
      width={720}
    >
      <Table
        caption={t('admin.deliveries.lines')}
        columns={columns}
        rows={delivery.lines}
        rowKey={(l) => `${l.stockItemId}-${l.unit}`}
      />
      <p className="stock-total">
        {t('admin.deliveries.total')}{' '}
        <span className="mono">KSh {formatAmount(delivery.total)}</span>
      </p>
      {delivery.reversalReason && (
        <p className="stock-note">
          {t('admin.deliveries.reversalReason', { reason: delivery.reversalReason })}
        </p>
      )}
      {delivery.rejectNote && (
        <p className="stock-note">
          {t('admin.deliveries.rejectNote', { note: delivery.rejectNote })}
        </p>
      )}
      {reversing && (
        <Field
          label={t('admin.deliveries.reasonLabel')}
          value={reason}
          maxLength={300}
          onChange={(e) => setReason(e.target.value)}
          autoFocus
        />
      )}
      {error && (
        <p className="nx-field__error" role="alert">
          {error}
        </p>
      )}
      <div className="stock-dialog__buttons">
        <Button onClick={() => onClose(false)}>{t('common.close')}</Button>
        {canReverse &&
          (reversing ? (
            <Button
              variant="danger"
              disabled={busy || !reason.trim()}
              onClick={() => void reverse()}
            >
              {t('admin.deliveries.confirmReverse')}
            </Button>
          ) : (
            <Button variant="danger" onClick={() => setReversing(true)}>
              {t('admin.deliveries.reverse')}
            </Button>
          ))}
      </div>
    </Modal>
  );
}

/** Suppliers: name, phone, KRA PIN, notes, active; what each is owed. */
function SuppliersDialog({ suppliers, onClose }: { suppliers: Supplier[]; onClose: () => void }) {
  const queries = useQueryClient();
  const [editing, setEditing] = useState<Supplier | 'new' | null>(null);
  const [form, setForm] = useState({ name: '', phone: '', kraPin: '', notes: '', active: true });
  const [error, setError] = useState<string | null>(null);
  const start = (s: Supplier | 'new') => {
    setEditing(s);
    setError(null);
    setForm(
      s === 'new'
        ? { name: '', phone: '', kraPin: '', notes: '', active: true }
        : { name: s.name, phone: s.phone, kraPin: s.kraPin, notes: s.notes, active: s.active },
    );
  };
  const save = async () => {
    try {
      await api(
        editing === 'new'
          ? '/api/admin/suppliers'
          : `/api/admin/suppliers/${(editing as Supplier).id}`,
        {
          method: editing === 'new' ? 'POST' : 'PUT',
          body: {
            ...form,
            rowVersion: editing === 'new' ? null : (editing as Supplier).rowVersion,
          },
        },
      );
      await queries.invalidateQueries({ queryKey: ['stock', 'suppliers'] });
      setEditing(null);
    } catch (e) {
      setError(e instanceof ApiError ? e.message : t('common.somethingWrong'));
    }
  };
  const columns: Column<Supplier>[] = [
    {
      key: 'name',
      header: t('admin.deliveries.supplier'),
      render: (s) => (s.active ? s.name : `${s.name} · ${t('admin.stock.inactive')}`),
    },
    { key: 'phone', header: t('admin.deliveries.phone'), render: (s) => s.phone || '—' },
    {
      key: 'kra',
      header: t('admin.deliveries.kraPin'),
      mono: true,
      render: (s) => s.kraPin || '—',
    },
    {
      key: 'owed',
      header: t('admin.deliveries.owedHeader'),
      align: 'right',
      mono: true,
      render: (s) => formatAmount(s.owed),
    },
    {
      key: 'edit',
      header: '',
      render: (s) => (
        <Button
          aria-label={t('admin.deliveries.editSupplier', { name: s.name })}
          onClick={() => start(s)}
        >
          {t('common.edit')}
        </Button>
      ),
    },
  ];
  return (
    <Modal title={t('admin.deliveries.suppliers')} onClose={onClose} width={760}>
      {editing ? (
        <div className="stock-form">
          <Field
            label={t('admin.deliveries.supplierName')}
            value={form.name}
            maxLength={80}
            onChange={(e) => setForm({ ...form, name: e.target.value })}
            autoFocus
          />
          <div className="stock-form__row">
            <Field
              label={t('admin.deliveries.phone')}
              value={form.phone}
              maxLength={40}
              onChange={(e) => setForm({ ...form, phone: e.target.value })}
            />
            <Field
              label={t('admin.deliveries.kraPinOptional')}
              value={form.kraPin}
              maxLength={20}
              onChange={(e) => setForm({ ...form, kraPin: e.target.value })}
            />
          </div>
          <Field
            label={t('admin.deliveries.notes')}
            value={form.notes}
            maxLength={500}
            onChange={(e) => setForm({ ...form, notes: e.target.value })}
          />
          <Toggle
            label={t('admin.stock.active')}
            checked={form.active}
            onChange={(active) => setForm({ ...form, active })}
          />
          {error && (
            <p className="nx-field__error" role="alert">
              {error}
            </p>
          )}
          <div className="stock-dialog__buttons">
            <Button onClick={() => setEditing(null)}>{t('common.cancel')}</Button>
            <Button
              variant="primary"
              disabled={form.name.trim().length < 2}
              onClick={() => void save()}
            >
              {t('common.save')}
            </Button>
          </div>
        </div>
      ) : (
        <>
          <Table
            caption={t('admin.deliveries.suppliers')}
            columns={columns}
            rows={suppliers}
            rowKey={(s) => s.id}
          />
          <div className="stock-dialog__buttons">
            <Button onClick={() => start('new')}>{t('admin.deliveries.addSupplier')}</Button>
            <Button onClick={onClose}>{t('common.close')}</Button>
          </div>
        </>
      )}
    </Modal>
  );
}
