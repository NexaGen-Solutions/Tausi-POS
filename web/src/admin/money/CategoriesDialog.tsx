import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { ApiError, api } from '../../shared/api/client';
import { t } from '../../shared/i18n/i18n';
import { Button, Field, Modal, Toggle } from '../../shared/ui';
import type { ExpenseCategory } from './types';

/** Expense categories: rename, deactivate, add. A category in use can't be deleted, only deactivated. */
export function CategoriesDialog({ onClose }: { onClose: () => void }) {
  const queries = useQueryClient();
  const categories = useQuery({
    queryKey: ['cashbook', 'categories'],
    queryFn: () => api<ExpenseCategory[]>('/api/admin/money/categories'),
  });
  const [names, setNames] = useState<Record<string, string>>({});
  const [adding, setAdding] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const run = async (call: () => Promise<unknown>) => {
    setBusy(true);
    setError(null);
    try {
      await call();
      await queries.invalidateQueries({ queryKey: ['cashbook', 'categories'] });
      return true;
    } catch (e) {
      setError(e instanceof ApiError ? e.message : t('common.somethingWrong'));
      return false;
    } finally {
      setBusy(false);
    }
  };
  const save = (c: ExpenseCategory, change: Partial<Pick<ExpenseCategory, 'name' | 'active'>>) =>
    run(() =>
      api(`/api/admin/money/categories/${c.id}`, {
        method: 'PUT',
        body: {
          name: change.name ?? c.name,
          active: change.active ?? c.active,
          rowVersion: c.rowVersion,
        },
      }),
    );

  return (
    <Modal title={t('admin.cashbook.manageCategories')} onClose={onClose} width={620}>
      <ul className="money-categories">
        {(categories.data ?? []).map((c) => {
          const name = names[c.id] ?? c.name;
          return (
            <li key={c.id} className="money-categories__row">
              <Field
                label={t('admin.cashbook.categories.name')}
                value={name}
                maxLength={60}
                onChange={(e) => setNames((n) => ({ ...n, [c.id]: e.target.value }))}
              />
              <Toggle
                label={t('admin.cashbook.categories.active')}
                checked={c.active}
                disabled={busy}
                onChange={(active) => void save(c, { active })}
              />
              <Button
                disabled={busy || name.trim() === '' || name === c.name}
                onClick={() => void save(c, { name: name.trim() })}
              >
                {t('common.save')}
              </Button>
              <Button
                variant="link"
                disabled={busy || c.inUse}
                title={c.inUse ? t('admin.cashbook.categories.inUse') : undefined}
                onClick={() =>
                  void run(() => api(`/api/admin/money/categories/${c.id}`, { method: 'DELETE' }))
                }
              >
                {t('admin.cashbook.categories.delete')}
              </Button>
            </li>
          );
        })}
      </ul>
      <p className="stock-note">{t('admin.cashbook.categories.inUseNote')}</p>
      <form
        className="money-categories__row"
        onSubmit={(e) => {
          e.preventDefault();
          if (!adding.trim()) return;
          void run(() =>
            api('/api/admin/money/categories', {
              method: 'POST',
              body: { name: adding.trim(), active: true, rowVersion: null },
            }),
          ).then((ok) => ok && setAdding(''));
        }}
      >
        <Field
          label={t('admin.cashbook.categories.new')}
          value={adding}
          maxLength={60}
          onChange={(e) => setAdding(e.target.value)}
        />
        <Button type="submit" variant="primary" disabled={busy || !adding.trim()}>
          {t('common.add')}
        </Button>
      </form>
      {error && (
        <p className="nx-field__error" role="alert">
          {error}
        </p>
      )}
      <div className="stock-dialog__buttons">
        <Button onClick={onClose}>{t('common.close')}</Button>
      </div>
    </Modal>
  );
}
