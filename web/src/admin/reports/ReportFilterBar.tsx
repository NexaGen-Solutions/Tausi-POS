import { useState } from 'react';
import { t } from '../../shared/i18n/i18n';
import { Field, SelectField } from '../../shared/ui';
import { formatBusinessDate, todayIso } from '../money/dates';
import type {
  QuickRange,
  ReportDefinition,
  ReportFilters,
  ReportOption,
  ReportOptions,
} from './types';

const quickRanges: QuickRange[] = [
  'Today',
  'Yesterday',
  'ThisWeek',
  'LastWeek',
  'ThisMonth',
  'LastMonth',
];
const statuses = ['Settled', 'Open', 'Cancelled', 'Merged'];
const paymentTypes = ['Cash', 'Mpesa', 'Card'] as const;
const stockCategories = ['Beer', 'Spirits', 'SoftDrinks', 'Food', 'Other'];

interface Props {
  definition: ReportDefinition;
  options: ReportOptions | undefined;
  filters: ReportFilters;
  onChange: (filters: ReportFilters) => void;
}

/** "All …" first, then the choices. */
function choices(all: string, list: ReportOption[] | undefined) {
  return [{ value: '', label: all }, ...(list ?? []).map((o) => ({ value: o.id, label: o.name }))];
}

/** A text filter applied on Enter or when it loses focus, so typing doesn't rerun the report each key. */
function TextFilter({
  label,
  value,
  onApply,
  type = 'text',
}: {
  label: string;
  value: string;
  onApply: (value: string) => void;
  type?: string;
}) {
  const [text, setText] = useState(value);
  return (
    <Field
      label={label}
      type={type}
      value={text}
      inputMode={type === 'number' ? 'decimal' : undefined}
      onChange={(e) => setText(e.target.value)}
      onBlur={() => text !== value && onApply(text)}
      onKeyDown={(e) => {
        if (e.key === 'Enter') onApply(text);
      }}
    />
  );
}

function Check({
  label,
  checked,
  onChange,
}: {
  label: string;
  checked: boolean;
  onChange: (checked: boolean) => void;
}) {
  return (
    <label className="report-check">
      <input type="checkbox" checked={checked} onChange={(e) => onChange(e.target.checked)} />
      {label}
    </label>
  );
}

/** The period (a work period, or business dates with quick picks) and the filters this report takes. */
export function ReportFilterBar({ definition, options, filters, onChange }: Props) {
  const uses = (filter: string) => definition.filters.includes(filter);
  const set = (change: Partial<ReportFilters>) => onChange({ ...filters, ...change });
  const id = (value: string) => value || undefined;
  const ranged = filters.from !== undefined || filters.quick !== undefined;
  const periods = options?.periods ?? [];
  const period =
    periods.find((p) => p.id === filters.workPeriodId) ?? periods.find((p) => p.open) ?? periods[0];
  const openShown = ranged || !!period?.open;

  return (
    <div className="report-filters">
      {definition.usesPeriod && (
        <>
          <SelectField
            label={t('admin.reports.period')}
            value={ranged ? 'range' : (filters.workPeriodId ?? '')}
            onChange={(e) =>
              e.target.value === 'range'
                ? set({ workPeriodId: undefined, quick: 'Today', from: undefined, to: undefined })
                : set({
                    workPeriodId: id(e.target.value),
                    quick: undefined,
                    from: undefined,
                    to: undefined,
                  })
            }
            options={[
              { value: '', label: t('admin.reports.currentPeriod') },
              ...periods.map((p) => ({
                value: p.id,
                label: `${formatBusinessDate(p.date)}${p.open ? ` ${t('admin.reports.openSuffix')}` : ''}`,
              })),
              { value: 'range', label: t('admin.reports.dateRange') },
            ]}
          />
          {ranged && (
            <>
              <SelectField
                label={t('admin.reports.dates')}
                value={filters.quick ?? ''}
                onChange={(e) =>
                  e.target.value
                    ? set({ quick: e.target.value as QuickRange, from: undefined, to: undefined })
                    : set({ quick: undefined, from: todayIso(), to: todayIso() })
                }
                options={[
                  ...quickRanges.map((q) => ({ value: q, label: t(`admin.reports.quick.${q}`) })),
                  { value: '', label: t('admin.reports.chooseDates') },
                ]}
              />
              {filters.quick === undefined && (
                <>
                  <Field
                    label={t('admin.reports.from')}
                    type="date"
                    value={filters.from ?? ''}
                    max={todayIso()}
                    onChange={(e) => e.target.value && set({ from: e.target.value })}
                  />
                  <Field
                    label={t('admin.reports.to')}
                    type="date"
                    value={filters.to ?? ''}
                    max={todayIso()}
                    onChange={(e) => e.target.value && set({ to: e.target.value })}
                  />
                </>
              )}
            </>
          )}
        </>
      )}
      {uses('asAt') && (
        <Field
          label={t('admin.reports.asAt')}
          type="date"
          value={filters.asAt ?? todayIso()}
          max={todayIso()}
          onChange={(e) => set({ asAt: e.target.value || undefined })}
        />
      )}
      {uses('user') && (
        <SelectField
          label={t('admin.reports.user')}
          value={filters.userId ?? ''}
          onChange={(e) => set({ userId: id(e.target.value) })}
          options={choices(t('admin.reports.allUsers'), options?.users)}
        />
      )}
      {uses('cashier') && (
        <SelectField
          label={t('admin.reports.cashier')}
          value={filters.cashierId ?? ''}
          onChange={(e) => set({ cashierId: id(e.target.value) })}
          options={choices(t('admin.reports.allUsers'), options?.users)}
        />
      )}
      {uses('waiter') && (
        <SelectField
          label={t('admin.reports.waiter')}
          value={filters.waiterId ?? ''}
          onChange={(e) => set({ waiterId: id(e.target.value) })}
          options={choices(t('admin.reports.allUsers'), options?.users)}
        />
      )}
      {uses('terminal') && (
        <SelectField
          label={t('admin.reports.terminal')}
          value={filters.terminalId ?? ''}
          onChange={(e) => set({ terminalId: id(e.target.value) })}
          options={choices(t('admin.reports.allTerminals'), options?.terminals)}
        />
      )}
      {uses('creditTo') && (
        <SelectField
          label={t('admin.reports.creditTo')}
          value={filters.creditTo ?? 'AddedBy'}
          onChange={(e) => set({ creditTo: e.target.value as ReportFilters['creditTo'] })}
          options={[
            { value: 'AddedBy', label: t('admin.reports.addedBy') },
            { value: 'OpenedBy', label: t('admin.reports.openedBy') },
          ]}
        />
      )}
      {uses('category') && (
        <SelectField
          label={t('admin.reports.category')}
          value={filters.categoryId ?? ''}
          onChange={(e) => set({ categoryId: id(e.target.value) })}
          options={choices(t('admin.reports.allCategories'), options?.categories)}
        />
      )}
      {uses('stockCategory') && (
        <SelectField
          label={t('admin.reports.category')}
          value={filters.stockCategory ?? ''}
          onChange={(e) => set({ stockCategory: id(e.target.value) })}
          options={[
            { value: '', label: t('admin.reports.allCategories') },
            ...stockCategories.map((c) => ({
              value: c,
              label: t(`admin.reports.stockCategory.${c}`),
            })),
          ]}
        />
      )}
      {uses('location') && (
        <SelectField
          label={t('admin.reports.location')}
          value={filters.locationId ?? ''}
          onChange={(e) => set({ locationId: id(e.target.value) })}
          options={choices(t('admin.reports.allLocations'), options?.locations)}
        />
      )}
      {uses('supplier') && (
        <SelectField
          label={t('admin.reports.supplier')}
          value={filters.supplierId ?? ''}
          onChange={(e) => set({ supplierId: id(e.target.value) })}
          options={choices(t('admin.reports.allSuppliers'), options?.suppliers)}
        />
      )}
      {uses('paymentType') && (
        <SelectField
          label={t('admin.reports.paymentType')}
          value={filters.paymentType ?? ''}
          onChange={(e) =>
            set({ paymentType: (e.target.value || undefined) as ReportFilters['paymentType'] })
          }
          options={[
            { value: '', label: t('admin.reports.allTypes') },
            ...paymentTypes.map((p) => ({ value: p, label: t(`admin.reports.payment.${p}`) })),
          ]}
        />
      )}
      {uses('kind') && (
        <SelectField
          label={t('admin.reports.kind')}
          value={filters.kind ?? ''}
          onChange={(e) => set({ kind: id(e.target.value) })}
          options={[
            { value: '', label: t('admin.reports.allKinds') },
            ...(definition.kinds ?? []).map((k) => ({ value: k, label: k })),
          ]}
        />
      )}
      {uses('requester') && (
        <SelectField
          label={t('admin.reports.requester')}
          value={filters.requesterId ?? ''}
          onChange={(e) => set({ requesterId: id(e.target.value) })}
          options={choices(t('admin.reports.anyone'), options?.users)}
        />
      )}
      {uses('approver') && (
        <SelectField
          label={t('admin.reports.approver')}
          value={filters.approverId ?? ''}
          onChange={(e) => set({ approverId: id(e.target.value) })}
          options={choices(t('admin.reports.anyone'), options?.users)}
        />
      )}
      {uses('count') && (
        <SelectField
          label={t('admin.reports.count')}
          value={filters.countId ?? ''}
          onChange={(e) => set({ countId: id(e.target.value), stockItemId: undefined })}
          options={choices(t('admin.reports.lastCount'), options?.counts)}
        />
      )}
      {uses('stockItem') && (
        <SelectField
          label={t('admin.reports.trend')}
          value={filters.stockItemId ?? ''}
          onChange={(e) => set({ stockItemId: id(e.target.value) })}
          options={choices(t('admin.reports.noTrend'), options?.stockItems)}
        />
      )}
      {uses('status') && (
        <SelectField
          label={t('admin.reports.status')}
          value={filters.status ?? ''}
          onChange={(e) => set({ status: id(e.target.value) })}
          options={[
            { value: '', label: t('admin.reports.anyStatus') },
            ...statuses.map((s) => ({ value: s, label: t(`admin.reports.ticketStatus.${s}`) })),
          ]}
        />
      )}
      {uses('table') && (
        <SelectField
          label={t('admin.reports.table')}
          value={filters.tableId ?? ''}
          onChange={(e) => set({ tableId: id(e.target.value) })}
          options={choices(t('admin.reports.allTables'), options?.tables)}
        />
      )}
      {uses('role') && (
        <SelectField
          label={t('admin.reports.role')}
          value={filters.roleId ?? ''}
          onChange={(e) => set({ roleId: id(e.target.value) })}
          options={choices(t('admin.reports.allRoles'), options?.roles)}
        />
      )}
      {uses('amount') && (
        <>
          <TextFilter
            label={t('admin.reports.minAmount')}
            type="number"
            value={filters.minAmount?.toString() ?? ''}
            onApply={(v) => set({ minAmount: v === '' ? undefined : Number(v) })}
          />
          <TextFilter
            label={t('admin.reports.maxAmount')}
            type="number"
            value={filters.maxAmount?.toString() ?? ''}
            onApply={(v) => set({ maxAmount: v === '' ? undefined : Number(v) })}
          />
        </>
      )}
      {uses('action') && (
        <TextFilter
          label={t('admin.reports.action')}
          value={filters.action ?? ''}
          onApply={(v) => set({ action: v || undefined })}
        />
      )}
      {uses('entity') && (
        <TextFilter
          label={t('admin.reports.entity')}
          value={filters.entity ?? ''}
          onApply={(v) => set({ entity: v || undefined })}
        />
      )}
      {uses('search') && (
        <TextFilter
          label={t('admin.reports.search')}
          value={filters.search ?? ''}
          onApply={(v) => set({ search: v || undefined })}
        />
      )}
      <div className="report-checks">
        {uses('flags') && (
          <>
            <Check
              label={t('admin.reports.hasVoids')}
              checked={!!filters.hasVoids}
              onChange={(v) => set({ hasVoids: v || undefined })}
            />
            <Check
              label={t('admin.reports.hasReprints')}
              checked={!!filters.hasReprints}
              onChange={(v) => set({ hasReprints: v || undefined })}
            />
            <Check
              label={t('admin.reports.hasDiscount')}
              checked={!!filters.hasDiscount}
              onChange={(v) => set({ hasDiscount: v || undefined })}
            />
          </>
        )}
        {uses('onlyBelowReorder') && (
          <Check
            label={t('admin.reports.onlyBelowReorder')}
            checked={!!filters.onlyBelowReorder}
            onChange={(v) => set({ onlyBelowReorder: v || undefined })}
          />
        )}
        {uses('includeOpen') && openShown && (
          <Check
            label={t('admin.reports.includeOpen')}
            checked={filters.includeOpen ?? true}
            onChange={(v) => set({ includeOpen: v })}
          />
        )}
      </div>
    </div>
  );
}
