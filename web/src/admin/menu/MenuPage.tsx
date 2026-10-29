import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useEffect, useMemo, useState } from 'react';
import { api, apiUrl } from '../../shared/api/client';
import { onPosEvent } from '../../shared/connection/hub';
import { formatAmount } from '../../shared/format/money';
import { t } from '../../shared/i18n/i18n';
import { Banner, Button, Field, Table, type Column } from '../../shared/ui';
import { CategoriesPanel } from './CategoriesPanel';
import { GroupsPanel } from './GroupsPanel';
import { ImportDialog } from './ImportDialog';
import { SambaPosDialog } from './SambaPosDialog';
import { ItemPanel } from './ItemPanel';
import { PriceListsPanel } from './PriceListsPanel';
import type { AdminMenu, MenuItem } from './types';
import './menu.css';

type Panel =
  | { kind: 'item'; item?: MenuItem }
  | { kind: 'groups' }
  | { kind: 'lists' }
  | { kind: 'categories' };

/** Wireframe 10: menu and prices, with a right-side panel for items, option groups, lists and categories. */
export function MenuPage() {
  const queries = useQueryClient();
  const menu = useQuery({
    queryKey: ['admin-menu'],
    queryFn: () => api<AdminMenu>('/api/admin/menu'),
  });
  const [category, setCategory] = useState<string | null>(null);
  const [search, setSearch] = useState('');
  const [panel, setPanel] = useState<Panel | null>(null);
  const [importing, setImporting] = useState(false);
  const [sambaPos, setSambaPos] = useState(false);
  const [stale, setStale] = useState(false);

  const refresh = () => queries.invalidateQueries({ queryKey: ['admin-menu'] });
  // Another admin's change: refresh the list (an open form keeps what is typed).
  useEffect(
    () =>
      onPosEvent(
        'menu.changed',
        () => void queries.invalidateQueries({ queryKey: ['admin-menu'] }),
      ),
    [queries],
  );

  const data = menu.data;
  const activeLists = useMemo(() => data?.priceLists.filter((l) => l.active) ?? [], [data]);
  const categoryOrder = useMemo(
    () => new Map(data?.categories.map((c, i) => [c.id, i]) ?? []),
    [data],
  );
  const categoryName = (id: string) => data?.categories.find((c) => c.id === id)?.name ?? '';
  const groupName = (id: string) => data?.groups.find((g) => g.id === id)?.name ?? '';

  const items = useMemo(() => {
    const term = search.trim().toLowerCase();
    return (data?.items ?? [])
      .filter((i) => !category || i.categoryId === category)
      .filter(
        (i) =>
          !term || i.name.toLowerCase().includes(term) || i.shortName.toLowerCase().includes(term),
      )
      .sort(
        (a, b) =>
          (categoryOrder.get(a.categoryId) ?? 0) - (categoryOrder.get(b.categoryId) ?? 0) ||
          a.sortOrder - b.sortOrder ||
          a.name.localeCompare(b.name),
      );
  }, [data, category, search, categoryOrder]);

  if (!data) return <main className="menu" />;

  const columns: Column<MenuItem>[] = [
    {
      key: 'name',
      header: t('admin.menu.colName'),
      render: (i) => (
        <>
          <span className="menu__name">{i.name}</span>
          <span className="menu__short">{i.shortName}</span>
        </>
      ),
    },
    {
      key: 'category',
      header: t('admin.menu.colCategory'),
      render: (i) => categoryName(i.categoryId),
    },
    ...activeLists.map<Column<MenuItem>>((list) => ({
      key: `price-${list.id}`,
      header: list.name,
      align: 'right',
      mono: true,
      render: (i) =>
        i.prices[list.id] !== undefined ? (
          formatAmount(i.prices[list.id])
        ) : list.isDefault ? (
          <span className="menu-tag menu-tag--warning">{t('admin.menu.noPrice')}</span>
        ) : (
          ''
        ),
    })),
    {
      key: 'options',
      header: t('admin.menu.colOptions'),
      render: (i) => [...i.inheritedGroupIds, ...i.ownGroupIds].map(groupName).join(', ') || '—',
    },
    {
      key: 'active',
      header: t('admin.menu.colActive'),
      render: (i) => (i.active ? t('common.yes') : t('common.no')),
    },
    {
      key: 'edit',
      header: '',
      render: (i) => (
        <Button
          variant="link"
          aria-label={`${t('common.edit')} ${i.name}`}
          onClick={() => setPanel({ kind: 'item', item: i })}
        >
          {t('common.edit')}
        </Button>
      ),
    },
  ];

  const panelTitle = !panel
    ? ''
    : panel.kind === 'item'
      ? panel.item
        ? t('admin.menu.editItem', { name: panel.item.name })
        : t('admin.menu.addItem')
      : panel.kind === 'groups'
        ? t('admin.menu.groups.title')
        : panel.kind === 'lists'
          ? t('admin.menu.lists.title')
          : t('admin.menu.categories.title');

  const saved = () => {
    setStale(false);
    setPanel(null);
    void refresh();
    void queries.invalidateQueries({ queryKey: ['price-log'] });
  };
  const changed = () => void refresh();

  return (
    <main className={`menu${panel ? ' menu--panel' : ''}`}>
      <div className="menu__main">
        <div className="menu__header">
          <h1 className="admin__title">{t('admin.menu.title')}</h1>
          <div className="menu__actions">
            {data.canEdit && (
              <>
                <Button onClick={() => setImporting(true)}>{t('admin.menu.importCsv')}</Button>
                <Button onClick={() => setSambaPos(true)}>{t('admin.menu.importSambaPos')}</Button>
              </>
            )}
            <a
              className="nx-btn nx-btn--secondary nx-btn--md"
              href={apiUrl('/api/admin/menu/export.csv')}
              download="menu.csv"
            >
              {t('admin.menu.exportCsv')}
            </a>
            <Button onClick={() => setPanel({ kind: 'groups' })}>
              {t('admin.menu.optionGroups')}
            </Button>
            <Button onClick={() => setPanel({ kind: 'lists' })}>
              {t('admin.menu.priceLists')}
            </Button>
            {data.canEdit && (
              <Button variant="primary" onClick={() => setPanel({ kind: 'item' })}>
                {t('admin.menu.addItem')}
              </Button>
            )}
          </div>
        </div>
        {stale && (
          <Banner
            action={{
              label: t('common.reload'),
              onClick: () => {
                setStale(false);
                setPanel(null);
                void refresh();
              },
            }}
          >
            {t('admin.menu.stale')}
          </Banner>
        )}
        <div className="menu__chips" role="group" aria-label={t('admin.menu.colCategory')}>
          <button
            type="button"
            className="menu-chip"
            aria-pressed={category === null}
            onClick={() => setCategory(null)}
          >
            {t('admin.menu.all')}
          </button>
          {data.categories
            .filter((c) => c.active)
            .map((c) => (
              <button
                key={c.id}
                type="button"
                className="menu-chip"
                aria-pressed={category === c.id}
                onClick={() => setCategory(c.id)}
              >
                {c.name}
              </button>
            ))}
          {data.canEdit && (
            <button
              type="button"
              className="menu-chip menu-chip--add"
              onClick={() => setPanel({ kind: 'categories' })}
            >
              {t('admin.menu.addCategory')}
            </button>
          )}
          {data.canEdit && (
            <Button variant="link" onClick={() => setPanel({ kind: 'categories' })}>
              {t('admin.menu.manageCategories')}
            </Button>
          )}
        </div>
        <Field
          className="menu__search"
          label={t('admin.menu.search')}
          type="search"
          value={search}
          onChange={(e) => setSearch(e.target.value)}
        />
        <Table
          caption={t('admin.menu.title')}
          columns={columns}
          rows={items}
          rowKey={(i) => i.id}
          rowClassName={(i) => (i.active ? undefined : 'menu-row--inactive')}
          empty={t('admin.menu.empty')}
        />
      </div>

      {panel && (
        <aside className="menu-panel" aria-label={panelTitle}>
          <div className="menu-panel__head">
            <h2 className="menu-panel__title">{panelTitle}</h2>
            <Button variant="link" onClick={() => setPanel(null)}>
              {t('common.close')}
            </Button>
          </div>
          {panel.kind === 'item' && (
            <ItemPanel
              key={panel.item?.id ?? 'new'}
              menu={data}
              item={panel.item}
              onSaved={saved}
              onStale={() => setStale(true)}
              onCancel={() => setPanel(null)}
              onPicture={changed}
            />
          )}
          {panel.kind === 'groups' && (
            <GroupsPanel menu={data} onChanged={changed} onStale={() => setStale(true)} />
          )}
          {panel.kind === 'lists' && (
            <PriceListsPanel menu={data} onChanged={changed} onStale={() => setStale(true)} />
          )}
          {panel.kind === 'categories' && (
            <CategoriesPanel
              key={data.categories.map((c) => c.id + c.rowVersion).join()}
              menu={data}
              onChanged={changed}
              onStale={() => setStale(true)}
            />
          )}
        </aside>
      )}
      {importing && (
        <ImportDialog onClose={() => setImporting(false)} onApplied={() => void refresh()} />
      )}
      {sambaPos && (
        <SambaPosDialog onClose={() => setSambaPos(false)} onApplied={() => void refresh()} />
      )}
    </main>
  );
}
