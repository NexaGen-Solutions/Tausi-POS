import { useRef, useState } from 'react';
import { ApiError, api } from '../../shared/api/client';
import { t } from '../../shared/i18n/i18n';
import { Button, Field, Modal } from '../../shared/ui';
import type { AdminMenu, MenuCategory } from './types';

/** Add, rename, reorder (drag, or the arrows), (de)activate categories, and set items per page. */
export function CategoriesPanel({
  menu,
  onChanged,
  onStale,
}: {
  menu: AdminMenu;
  onChanged: () => void;
  onStale: () => void;
}) {
  const [order, setOrder] = useState(menu.categories.map((c) => c.id));
  const [newName, setNewName] = useState('');
  const [confirming, setConfirming] = useState<MenuCategory | null>(null);
  const [paging, setPaging] = useState<{ category: MenuCategory; value: string } | null>(null);
  const [error, setError] = useState<string | null>(null);
  const dragged = useRef<string | null>(null);
  const byId = new Map(menu.categories.map((c) => [c.id, c]));
  const categories = order.map((id) => byId.get(id)).filter((c): c is MenuCategory => !!c);

  const run = async (action: () => Promise<unknown>) => {
    setError(null);
    try {
      await action();
      onChanged();
    } catch (e) {
      if (e instanceof ApiError && e.status === 409) onStale();
      setError(e instanceof ApiError ? e.message : t('common.somethingWrong'));
    }
  };

  const saveOrder = (ids: string[]) => {
    setOrder(ids);
    void run(() => api('/api/admin/menu/categories/reorder', { method: 'POST', body: { ids } }));
  };
  const move = (index: number, by: number) => {
    const ids = [...order];
    const [moved] = ids.splice(index, 1);
    ids.splice(index + by, 0, moved);
    saveOrder(ids);
  };

  const deactivate = (category: MenuCategory, includeItems: boolean) => {
    setConfirming(null);
    void run(() =>
      api(`/api/admin/menu/categories/${category.id}/deactivate`, {
        method: 'POST',
        body: { includeItems, rowVersion: category.rowVersion },
      }),
    );
  };

  return (
    <div className="menu-panel__form">
      {menu.canEdit && (
        <form
          className="menu-list__rename"
          onSubmit={(e) => {
            e.preventDefault();
            if (!newName.trim()) return;
            void run(() =>
              api('/api/admin/menu/categories', {
                method: 'POST',
                body: { name: newName.trim(), active: true },
              }),
            ).then(() => setNewName(''));
          }}
        >
          <Field
            label={t('admin.menu.categories.newName')}
            value={newName}
            onChange={(e) => setNewName(e.target.value)}
            autoFocus
          />
          <Button type="submit" variant="primary">
            {t('common.add')}
          </Button>
        </form>
      )}
      <p className="menu-panel__note">{t('admin.menu.categories.dragHint')}</p>
      <ol className="menu-list menu-list--ordered">
        {categories.map((c, i) => (
          <li
            key={c.id}
            className={c.active ? undefined : 'menu-list__inactive'}
            draggable={menu.canEdit}
            onDragStart={() => (dragged.current = c.id)}
            onDragOver={(e) => e.preventDefault()}
            onDrop={() => {
              const from = dragged.current;
              dragged.current = null;
              if (!from || from === c.id) return;
              const ids = order.filter((id) => id !== from);
              ids.splice(ids.indexOf(c.id), 0, from);
              saveOrder(ids);
            }}
          >
            <span>
              <strong>{c.name}</strong> <small>({c.activeItems})</small>
              {c.itemsPerPage && (
                <small>
                  {' '}
                  · {t('admin.menu.categories.perPageShort', { count: c.itemsPerPage })}
                </small>
              )}
            </span>
            {menu.canEdit && (
              <span className="menu-list__actions">
                <Button
                  variant="link"
                  aria-label={`${t('admin.menu.categories.perPage')}: ${c.name}`}
                  onClick={() =>
                    setPaging({ category: c, value: c.itemsPerPage?.toString() ?? '' })
                  }
                >
                  {t('admin.menu.categories.perPage')}
                </Button>
                <Button
                  aria-label={`${t('common.up')}: ${c.name}`}
                  disabled={i === 0}
                  onClick={() => move(i, -1)}
                >
                  ↑
                </Button>
                <Button
                  aria-label={`${t('common.down')}: ${c.name}`}
                  disabled={i === categories.length - 1}
                  onClick={() => move(i, 1)}
                >
                  ↓
                </Button>
                {c.active ? (
                  <Button
                    variant="link"
                    aria-label={`${t('admin.menu.categories.deactivate')} ${c.name}`}
                    onClick={() => setConfirming(c)}
                  >
                    {t('admin.menu.categories.deactivate')}
                  </Button>
                ) : (
                  <Button
                    variant="link"
                    aria-label={`${t('admin.menu.categories.reactivate')} ${c.name}`}
                    onClick={() =>
                      void run(() =>
                        api(`/api/admin/menu/categories/${c.id}`, {
                          method: 'PUT',
                          body: {
                            name: c.name,
                            active: true,
                            rowVersion: c.rowVersion,
                            itemsPerPage: c.itemsPerPage,
                          },
                        }),
                      )
                    }
                  >
                    {t('admin.menu.categories.reactivate')}
                  </Button>
                )}
              </span>
            )}
          </li>
        ))}
      </ol>
      {error && (
        <p className="menu-panel__error" role="alert">
          {error}
        </p>
      )}
      {paging && (
        <Modal
          title={t('admin.menu.categories.perPageTitle', { name: paging.category.name })}
          onClose={() => setPaging(null)}
        >
          <form
            className="menu-confirm"
            onSubmit={(e) => {
              e.preventDefault();
              const { category, value } = paging;
              setPaging(null);
              void run(() =>
                api(`/api/admin/menu/categories/${category.id}`, {
                  method: 'PUT',
                  body: {
                    name: category.name,
                    active: category.active,
                    rowVersion: category.rowVersion,
                    itemsPerPage: value.trim() ? Number(value) : null,
                  },
                }),
              );
            }}
          >
            <Field
              label={t('admin.menu.categories.perPage')}
              hint={t('admin.menu.categories.perPageHint')}
              inputMode="numeric"
              value={paging.value}
              onChange={(e) => setPaging({ ...paging, value: e.target.value.replace(/\D/g, '') })}
              autoFocus
            />
            <Button type="submit" variant="primary" size="lg" block>
              {t('common.save')}
            </Button>
            <Button block onClick={() => setPaging(null)}>
              {t('common.cancel')}
            </Button>
          </form>
        </Modal>
      )}
      {confirming && (
        <Modal
          title={t('admin.menu.categories.confirmTitle', { name: confirming.name })}
          onClose={() => setConfirming(null)}
        >
          <div className="menu-confirm">
            {confirming.activeItems > 0 ? (
              <>
                <p>
                  {t('admin.menu.categories.confirmItems', {
                    name: confirming.name,
                    count: confirming.activeItems,
                  })}
                </p>
                <Button
                  variant="danger"
                  size="lg"
                  block
                  onClick={() => deactivate(confirming, true)}
                >
                  {t('admin.menu.categories.withItems')}
                </Button>
                <Button block onClick={() => deactivate(confirming, false)}>
                  {t('admin.menu.categories.onlyCategory')}
                </Button>
              </>
            ) : (
              <>
                <p>{t('admin.menu.categories.confirmPlain', { name: confirming.name })}</p>
                <Button
                  variant="danger"
                  size="lg"
                  block
                  onClick={() => deactivate(confirming, false)}
                >
                  {t('admin.menu.categories.deactivate')}
                </Button>
              </>
            )}
            <Button block onClick={() => setConfirming(null)}>
              {t('common.cancel')}
            </Button>
          </div>
        </Modal>
      )}
    </div>
  );
}
