import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { ApiError, api } from '../../shared/api/client';
import { t } from '../../shared/i18n/i18n';
import { Button } from '../../shared/ui';
import { LocationsPanel } from '../../admin/stock/LocationsPanel';
import { OpeningBalancesDialog } from '../../admin/money/MoneyForms';
import { NextButton, type StepProps } from '../SetupWizard';
import type { LocationRow, StockSuggestion } from '../types';

/**
 * Step 5, Opening stock count: the locations, stock items made from the menu (1:1 for bottles, the
 * owner ticks which), the opening count per location (on the Task 11 count screen, approved by the
 * owner during setup) and the opening balances.
 */
export function StockStep({ onNext }: StepProps) {
  const queries = useQueryClient();
  const suggestions = useQuery({
    queryKey: ['setup', 'stock-suggestions'],
    queryFn: () => api<StockSuggestion[]>('/api/admin/stock/from-menu'),
  });
  const locations = useQuery({
    queryKey: ['stock', 'locations', 'all'],
    queryFn: () => api<LocationRow[]>('/api/admin/stock/locations/all'),
  });
  const [chosen, setChosen] = useState<Record<string, StockSuggestion>>({});
  const [created, setCreated] = useState<number | null>(null);
  const [balances, setBalances] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const list = suggestions.data ?? [];
  const picked = (s: StockSuggestion) => chosen[s.menuItemId] ?? (s.suggested ? s : null);
  const toggle = (s: StockSuggestion, on: boolean) =>
    setChosen((c) => ({
      ...c,
      [s.menuItemId]: on ? { ...s, suggested: true } : { ...s, suggested: false },
    }));
  const edit = (s: StockSuggestion, change: Partial<StockSuggestion>) =>
    setChosen((c) => ({ ...c, [s.menuItemId]: { ...(picked(s) ?? s), ...change } }));

  const create = async () => {
    setError(null);
    try {
      const items = list
        .map((s) => picked(s))
        .filter((s): s is StockSuggestion => !!s && s.suggested);
      const r = await api<{ created: number }>('/api/admin/stock/from-menu', {
        method: 'POST',
        body: { items },
      });
      setCreated(r.created);
      await queries.invalidateQueries({ queryKey: ['setup', 'stock-suggestions'] });
    } catch (e) {
      setError(e instanceof ApiError ? e.message : t('common.somethingWrong'));
    }
  };
  const startCount = async (location: LocationRow) => {
    setError(null);
    try {
      const count = await api<{ id: string }>('/api/admin/stock-counts', {
        method: 'POST',
        body: { locationId: location.id, kind: 'Full', isOpening: true, itemIds: null },
      });
      window.open(`/admin/stock-counts/${count.id}`, '_blank', 'noopener');
    } catch (e) {
      setError(e instanceof ApiError ? e.message : t('common.somethingWrong'));
    }
  };
  const done = async () => {
    await api('/api/setup/steps/stock/done', { method: 'POST', body: { summary: null } });
    onNext();
  };

  return (
    <div className="wizard__form">
      <section className="wizard__panel" aria-labelledby="stock-locations">
        <h3 id="stock-locations" className="wizard__subtitle">
          {t('wizard.stock.locations')}
        </h3>
        <LocationsPanel />
      </section>
      <section className="wizard__panel" aria-labelledby="stock-items">
        <h3 id="stock-items" className="wizard__subtitle">
          {t('wizard.stock.items')}
        </h3>
        <p className="wizard__note">{t('wizard.stock.itemsHelp')}</p>
        {list.length === 0 ? (
          <p className="wizard__note">{t('wizard.stock.noSuggestions')}</p>
        ) : (
          <div className="wizard__scroll">
            <table className="wizard__table" aria-label={t('wizard.stock.items')}>
              <thead>
                <tr>
                  <th scope="col">{t('wizard.stock.make')}</th>
                  <th scope="col">{t('wizard.stock.menuItem')}</th>
                  <th scope="col">{t('wizard.stock.stockItem')}</th>
                  <th scope="col">{t('wizard.stock.pack')}</th>
                </tr>
              </thead>
              <tbody>
                {list.map((s) => {
                  const p = picked(s);
                  return (
                    <tr key={s.menuItemId}>
                      <td>
                        <input
                          type="checkbox"
                          aria-label={t('wizard.stock.makeFor', { name: s.menuItem })}
                          checked={!!p?.suggested}
                          onChange={(e) => toggle(s, e.target.checked)}
                        />
                      </td>
                      <td>
                        {s.menuItem}
                        {s.hint && <span className="wizard__note wizard__block">{s.hint}</span>}
                      </td>
                      <td>
                        <input
                          className="nx-field__input"
                          aria-label={t('wizard.stock.stockItemFor', { name: s.menuItem })}
                          value={p?.stockItemName ?? s.stockItemName}
                          onChange={(e) => edit(s, { stockItemName: e.target.value })}
                        />
                      </td>
                      <td className="wizard__pack">
                        <input
                          className="nx-field__input"
                          aria-label={t('wizard.stock.packName', { name: s.menuItem })}
                          value={p?.packName ?? s.packName ?? ''}
                          onChange={(e) => edit(s, { packName: e.target.value })}
                        />
                        <span>=</span>
                        <input
                          className="nx-field__input mono"
                          inputMode="numeric"
                          aria-label={t('wizard.stock.packSize', { name: s.menuItem })}
                          value={p?.packFactor ?? s.packFactor ?? ''}
                          onChange={(e) =>
                            edit(s, {
                              packFactor: Number(e.target.value.replace(/\D/g, '')) || null,
                            })
                          }
                        />
                      </td>
                    </tr>
                  );
                })}
              </tbody>
            </table>
          </div>
        )}
        <div className="wizard__row">
          <Button
            variant="primary"
            disabled={!list.some((s) => picked(s)?.suggested)}
            onClick={() => void create()}
          >
            {t('wizard.stock.create')}
          </Button>
          {created !== null && (
            <span className="wizard__ok" role="status">
              {t('wizard.stock.created', { count: created })}
            </span>
          )}
        </div>
      </section>
      <section className="wizard__panel" aria-labelledby="opening-count">
        <h3 id="opening-count" className="wizard__subtitle">
          {t('wizard.stock.count')}
        </h3>
        <p className="wizard__note">{t('wizard.stock.countHelp')}</p>
        <div className="wizard__row">
          {(locations.data ?? [])
            .filter((l) => l.active)
            .map((l) => (
              <Button key={l.id} onClick={() => void startCount(l)}>
                {t('wizard.stock.countAt', { name: l.name })}
              </Button>
            ))}
        </div>
      </section>
      <section className="wizard__panel" aria-labelledby="opening-balances">
        <h3 id="opening-balances" className="wizard__subtitle">
          {t('wizard.stock.balances')}
        </h3>
        <p className="wizard__note">{t('wizard.stock.balancesHelp')}</p>
        <Button onClick={() => setBalances(true)}>{t('wizard.stock.enterBalances')}</Button>
      </section>
      {error && (
        <p className="nx-field__error" role="alert">
          {error}
        </p>
      )}
      {balances && <OpeningBalancesDialog onDone={() => setBalances(false)} />}
      <NextButton next="licence" onClick={() => void done()} />
    </div>
  );
}
