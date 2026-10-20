import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { ApiError, api } from '../../shared/api/client';
import { t } from '../../shared/i18n/i18n';
import {
  Banner,
  Button,
  Field,
  Modal,
  SelectField,
  Table,
  Toggle,
  useToast,
  type Column,
} from '../../shared/ui';
import './tables-admin.css';

interface AdminArea {
  id: string;
  name: string;
  sortOrder: number;
  active: boolean;
  tables: number;
  rowVersion: number;
}

interface AdminTable {
  id: string;
  name: string;
  areaId: string;
  areaName: string;
  sortOrder: number;
  active: boolean;
  openTickets: number[];
  hasHistory: boolean;
  rowVersion: number;
}

interface TablesAdmin {
  areas: AdminArea[];
  tables: AdminTable[];
}

type Panel = { kind: 'area'; area?: AdminArea } | { kind: 'table'; table?: AdminTable };

const key = ['admin-tables'];

/** Areas and tables for the table map (/admin/tables, settings.manage). */
export function TablesAdminPage() {
  const queries = useQueryClient();
  const toast = useToast();
  const data = useQuery({ queryKey: key, queryFn: () => api<TablesAdmin>('/api/admin/tables') });
  const [panel, setPanel] = useState<Panel | null>(null);
  const [stale, setStale] = useState(false);
  const areas = data.data?.areas ?? [];
  const tables = data.data?.tables ?? [];

  const refresh = () => {
    setPanel(null);
    setStale(false);
    void queries.invalidateQueries({ queryKey: key });
  };

  const areaColumns: Column<AdminArea>[] = [
    { key: 'name', header: t('admin.tables.colName'), render: (a) => <strong>{a.name}</strong> },
    { key: 'order', header: t('admin.tables.colOrder'), mono: true, render: (a) => a.sortOrder },
    { key: 'tables', header: t('admin.tables.colTables'), mono: true, render: (a) => a.tables },
    {
      key: 'status',
      header: t('admin.tables.colStatus'),
      render: (a) => (a.active ? t('admin.tables.active') : t('admin.tables.inactive')),
    },
    {
      key: 'edit',
      header: '',
      render: (a) => (
        <Button
          variant="link"
          aria-label={`${t('common.edit')} ${a.name}`}
          onClick={() => setPanel({ kind: 'area', area: a })}
        >
          {t('common.edit')}
        </Button>
      ),
    },
  ];

  const tableColumns: Column<AdminTable>[] = [
    { key: 'name', header: t('admin.tables.colName'), render: (x) => <strong>{x.name}</strong> },
    { key: 'area', header: t('admin.tables.colArea'), render: (x) => x.areaName },
    { key: 'order', header: t('admin.tables.colOrder'), mono: true, render: (x) => x.sortOrder },
    {
      key: 'status',
      header: t('admin.tables.colStatus'),
      render: (x) => (x.active ? t('admin.tables.active') : t('admin.tables.inactive')),
    },
    {
      key: 'open',
      header: t('admin.tables.colOpen'),
      mono: true,
      render: (x) => (x.openTickets.length ? x.openTickets.map((n) => `#${n}`).join(', ') : '—'),
    },
    {
      key: 'edit',
      header: '',
      render: (x) => (
        <Button
          variant="link"
          aria-label={`${t('common.edit')} ${x.name}`}
          onClick={() => setPanel({ kind: 'table', table: x })}
        >
          {t('common.edit')}
        </Button>
      ),
    },
  ];

  const title = !panel
    ? ''
    : panel.kind === 'area'
      ? panel.area
        ? t('admin.tables.editArea', { name: panel.area.name })
        : t('admin.tables.newArea')
      : panel.table
        ? t('admin.tables.editTable', { name: panel.table.name })
        : t('admin.tables.newTable');

  return (
    <main className={`tables-admin${panel ? ' tables-admin--panel' : ''}`}>
      <div className="tables-admin__main">
        <div className="tables-admin__header">
          <h1 className="admin__title">{t('admin.tables.title')}</h1>
          <div className="tables-admin__buttons">
            <Button onClick={() => setPanel({ kind: 'area' })}>{t('admin.tables.addArea')}</Button>
            <Button
              variant="primary"
              disabled={areas.length === 0}
              onClick={() => setPanel({ kind: 'table' })}
            >
              {t('admin.tables.addTable')}
            </Button>
          </div>
        </div>
        {stale && (
          <Banner action={{ label: t('common.reload'), onClick: refresh }}>
            {t('admin.tables.stale')}
          </Banner>
        )}
        <h2 className="tables-admin__section">{t('admin.tables.areas')}</h2>
        <Table
          caption={t('admin.tables.areas')}
          columns={areaColumns}
          rows={areas}
          rowKey={(a) => a.id}
          rowClassName={(a) => (a.active ? undefined : 'tables-admin__row--inactive')}
        />
        {areas.length > 0 && (
          <QuickAdd
            areas={areas}
            onDone={(message) => {
              toast(message);
              void queries.invalidateQueries({ queryKey: key });
            }}
          />
        )}
        <h2 className="tables-admin__section">{t('admin.tables.tables')}</h2>
        <Table
          caption={t('admin.tables.tables')}
          columns={tableColumns}
          rows={tables}
          rowKey={(x) => x.id}
          rowClassName={(x) => (x.active ? undefined : 'tables-admin__row--inactive')}
        />
      </div>
      {panel && (
        <aside className="tables-admin__panel" aria-label={title}>
          <div className="tables-admin__panel-head">
            <h2 className="tables-admin__panel-title">{title}</h2>
            <Button variant="link" onClick={() => setPanel(null)}>
              {t('common.close')}
            </Button>
          </div>
          {panel.kind === 'area' ? (
            <AreaForm
              key={panel.area?.id ?? 'new'}
              area={panel.area}
              onSaved={refresh}
              onStale={() => setStale(true)}
            />
          ) : (
            <TableForm
              key={panel.table?.id ?? 'new'}
              table={panel.table}
              areas={areas}
              onSaved={refresh}
              onStale={() => setStale(true)}
            />
          )}
        </aside>
      )}
    </main>
  );
}

function errorText(e: unknown) {
  return e instanceof ApiError ? e.message : t('common.somethingWrong');
}

/** "Add tables from [1] to [16] in [Main]", with an optional name before the number. */
function QuickAdd({ areas, onDone }: { areas: AdminArea[]; onDone: (message: string) => void }) {
  const [from, setFrom] = useState('1');
  const [to, setTo] = useState('16');
  const [areaId, setAreaId] = useState(areas[0].id);
  const [prefix, setPrefix] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const add = async () => {
    setBusy(true);
    setError(null);
    try {
      const result = await api<{ added: string[]; skipped: string[] }>(
        '/api/admin/tables/quick-add',
        {
          method: 'POST',
          body: { from: Number(from), to: Number(to), areaId, prefix: prefix.trim() || null },
        },
      );
      const parts = [
        result.added.length
          ? t('admin.tables.added', { added: result.added.join(', ') })
          : t('admin.tables.addedNone'),
      ];
      if (result.skipped.length)
        parts.push(t('admin.tables.skipped', { skipped: result.skipped.join(', ') }));
      onDone(parts.join(' '));
    } catch (e) {
      setError(errorText(e));
    } finally {
      setBusy(false);
    }
  };

  return (
    <form
      className="tables-admin__quick"
      aria-label={t('admin.tables.quickAdd')}
      noValidate
      onSubmit={(e) => {
        e.preventDefault();
        void add();
      }}
    >
      <strong className="tables-admin__quick-title">{t('admin.tables.quickAdd')}</strong>
      <Field
        label={t('admin.tables.from')}
        value={from}
        inputMode="numeric"
        onChange={(e) => setFrom(e.target.value)}
      />
      <Field
        label={t('admin.tables.to')}
        value={to}
        inputMode="numeric"
        onChange={(e) => setTo(e.target.value)}
      />
      <SelectField
        label={t('admin.tables.in')}
        value={areaId}
        onChange={(e) => setAreaId(e.target.value)}
        options={areas.map((a) => ({ value: a.id, label: a.name }))}
      />
      <Field
        label={t('admin.tables.prefix')}
        value={prefix}
        maxLength={30}
        hint={t('admin.tables.prefixHint')}
        onChange={(e) => setPrefix(e.target.value)}
      />
      <Button type="submit" variant="primary" disabled={busy}>
        {t('admin.tables.add')}
      </Button>
      {error && (
        <p className="tables-admin__error" role="alert">
          {error}
        </p>
      )}
    </form>
  );
}

function AreaForm({
  area,
  onSaved,
  onStale,
}: {
  area?: AdminArea;
  onSaved: () => void;
  onStale: () => void;
}) {
  const [name, setName] = useState(area?.name ?? '');
  const [sortOrder, setSortOrder] = useState(String(area?.sortOrder ?? 0));
  const [active, setActive] = useState(area?.active ?? true);
  const [error, setError] = useState<string | null>(null);
  const [confirm, setConfirm] = useState(false);
  const [busy, setBusy] = useState(false);

  const save = async () => {
    setBusy(true);
    setError(null);
    try {
      const body = {
        name: name.trim(),
        sortOrder: Number(sortOrder) || 0,
        active,
        rowVersion: area?.rowVersion ?? 0,
      };
      await api(area ? `/api/admin/tables/areas/${area.id}` : '/api/admin/tables/areas', {
        method: area ? 'PUT' : 'POST',
        body,
      });
      onSaved();
    } catch (e) {
      if (e instanceof ApiError && e.status === 409) onStale();
      setError(errorText(e));
    } finally {
      setBusy(false);
    }
  };

  const remove = async () => {
    setConfirm(false);
    try {
      await api(`/api/admin/tables/areas/${area!.id}`, { method: 'DELETE' });
      onSaved();
    } catch (e) {
      setError(errorText(e));
    }
  };

  return (
    <form
      className="tables-admin__form"
      noValidate
      onSubmit={(e) => {
        e.preventDefault();
        void save();
      }}
    >
      <Field
        label={t('admin.tables.name')}
        value={name}
        maxLength={40}
        onChange={(e) => setName(e.target.value)}
      />
      <Field
        label={t('admin.tables.sortOrder')}
        value={sortOrder}
        inputMode="numeric"
        onChange={(e) => setSortOrder(e.target.value)}
      />
      <Toggle label={t('admin.tables.activeToggle')} checked={active} onChange={setActive} />
      {error && (
        <p className="tables-admin__error" role="alert">
          {error}
        </p>
      )}
      <Button type="submit" variant="primary" size="lg" block disabled={busy}>
        {t('admin.tables.save')}
      </Button>
      {area && area.tables === 0 && (
        <Button variant="danger" block onClick={() => setConfirm(true)}>
          {t('admin.tables.deleteArea')}
        </Button>
      )}
      {confirm && area && (
        <Modal
          title={t('admin.tables.deleteConfirm', { name: area.name })}
          onClose={() => setConfirm(false)}
        >
          <div className="tables-admin__confirm">
            <Button variant="danger" size="lg" block onClick={() => void remove()}>
              {t('admin.tables.delete')}
            </Button>
            <Button block onClick={() => setConfirm(false)}>
              {t('common.cancel')}
            </Button>
          </div>
        </Modal>
      )}
    </form>
  );
}

function TableForm({
  table,
  areas,
  onSaved,
  onStale,
}: {
  table?: AdminTable;
  areas: AdminArea[];
  onSaved: () => void;
  onStale: () => void;
}) {
  const [name, setName] = useState(table?.name ?? '');
  const [areaId, setAreaId] = useState(table?.areaId ?? areas[0]?.id ?? '');
  const [sortOrder, setSortOrder] = useState(String(table?.sortOrder ?? 0));
  const [active, setActive] = useState(table?.active ?? true);
  const [error, setError] = useState<string | null>(null);
  const [confirm, setConfirm] = useState(false);
  const [busy, setBusy] = useState(false);

  const save = async () => {
    setBusy(true);
    setError(null);
    try {
      const body = {
        name: name.trim(),
        areaId,
        sortOrder: Number(sortOrder) || 0,
        active,
        rowVersion: table?.rowVersion ?? 0,
      };
      await api(table ? `/api/admin/tables/${table.id}` : '/api/admin/tables', {
        method: table ? 'PUT' : 'POST',
        body,
      });
      onSaved();
    } catch (e) {
      if (e instanceof ApiError && e.status === 409) onStale();
      setError(errorText(e));
    } finally {
      setBusy(false);
    }
  };

  const remove = async () => {
    setConfirm(false);
    try {
      await api(`/api/admin/tables/${table!.id}`, { method: 'DELETE' });
      onSaved();
    } catch (e) {
      setError(errorText(e));
    }
  };

  return (
    <form
      className="tables-admin__form"
      noValidate
      onSubmit={(e) => {
        e.preventDefault();
        void save();
      }}
    >
      <Field
        label={t('admin.tables.name')}
        value={name}
        maxLength={40}
        onChange={(e) => setName(e.target.value)}
      />
      <SelectField
        label={t('admin.tables.area')}
        value={areaId}
        onChange={(e) => setAreaId(e.target.value)}
        options={areas.map((a) => ({ value: a.id, label: a.name }))}
      />
      <Field
        label={t('admin.tables.sortOrder')}
        value={sortOrder}
        inputMode="numeric"
        onChange={(e) => setSortOrder(e.target.value)}
      />
      <Toggle label={t('admin.tables.activeToggle')} checked={active} onChange={setActive} />
      {error && (
        <p className="tables-admin__error" role="alert">
          {error}
        </p>
      )}
      <Button type="submit" variant="primary" size="lg" block disabled={busy}>
        {t('admin.tables.save')}
      </Button>
      {table && !table.hasHistory && table.openTickets.length === 0 && (
        <Button variant="danger" block onClick={() => setConfirm(true)}>
          {t('admin.tables.delete')}
        </Button>
      )}
      {confirm && table && (
        <Modal
          title={t('admin.tables.deleteConfirm', { name: table.name })}
          onClose={() => setConfirm(false)}
        >
          <div className="tables-admin__confirm">
            <Button variant="danger" size="lg" block onClick={() => void remove()}>
              {t('admin.tables.delete')}
            </Button>
            <Button block onClick={() => setConfirm(false)}>
              {t('common.cancel')}
            </Button>
          </div>
        </Modal>
      )}
    </form>
  );
}
