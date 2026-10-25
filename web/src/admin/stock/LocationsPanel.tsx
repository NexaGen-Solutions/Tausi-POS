import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { ApiError, api } from '../../shared/api/client';
import { t } from '../../shared/i18n/i18n';
import { Button, Field, Toggle } from '../../shared/ui';
import type { LocationRow } from '../../setup/types';
import '../../setup/setup.css';
import './stock.css';

/** Stock locations: rename, switch off, add (stock.adjust). Used on its own page and in the setup wizard. */
export function LocationsPanel() {
  const queries = useQueryClient();
  const locations = useQuery({
    queryKey: ['stock', 'locations', 'all'],
    queryFn: () => api<LocationRow[]>('/api/admin/stock/locations/all'),
  });
  const [names, setNames] = useState<Record<string, string>>({});
  const [adding, setAdding] = useState('');
  const [error, setError] = useState<string | null>(null);
  const save = async (
    id: string | null,
    name: string,
    active: boolean,
    rowVersion: number | null,
  ) => {
    setError(null);
    try {
      await api(id ? `/api/admin/stock/locations/${id}` : '/api/admin/stock/locations', {
        method: id ? 'PUT' : 'POST',
        body: { name, active, rowVersion },
      });
      await queries.invalidateQueries({ queryKey: ['stock'] });
      return true;
    } catch (e) {
      setError(
        e instanceof ApiError
          ? (e.problem.fieldErrors?.name ?? e.message)
          : t('common.somethingWrong'),
      );
      return false;
    }
  };
  return (
    <div className="locations">
      <ul className="locations__list">
        {(locations.data ?? []).map((l) => {
          const name = names[l.id] ?? l.name;
          return (
            <li key={l.id} className="locations__row">
              <Field
                label={t('admin.locations.name')}
                value={name}
                onChange={(e) => setNames((n) => ({ ...n, [l.id]: e.target.value }))}
              />
              <Toggle
                label={t('admin.locations.active')}
                checked={l.active}
                onChange={(v) => void save(l.id, l.name, v, l.rowVersion)}
              />
              <Button
                disabled={!name.trim() || name === l.name}
                onClick={() => void save(l.id, name.trim(), l.active, l.rowVersion)}
              >
                {t('common.save')}
              </Button>
            </li>
          );
        })}
      </ul>
      <form
        className="locations__row"
        onSubmit={(e) => {
          e.preventDefault();
          void save(null, adding.trim(), true, null).then((ok) => ok && setAdding(''));
        }}
      >
        <Field
          label={t('admin.locations.new')}
          value={adding}
          onChange={(e) => setAdding(e.target.value)}
        />
        <Button type="submit" variant="primary" disabled={!adding.trim()}>
          {t('common.add')}
        </Button>
      </form>
      {error && (
        <p className="nx-field__error" role="alert">
          {error}
        </p>
      )}
    </div>
  );
}

/** /admin/stock/locations */
export function LocationsPage() {
  return (
    <main className="stock stock--single">
      <div className="stock__main">
        <h1 className="admin__title">{t('admin.locations.title')}</h1>
        <p className="stock-note">{t('admin.locations.help')}</p>
        <LocationsPanel />
      </div>
    </main>
  );
}
