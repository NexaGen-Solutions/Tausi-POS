import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { ApiError, api } from '../../shared/api/client';
import { formatAmount } from '../../shared/format/money';
import { t } from '../../shared/i18n/i18n';
import { Button, SelectField, useToast } from '../../shared/ui';
import { factorOf, unitsOf } from './units';
import type { Recipe, RecipeMenuItem, StockItemRow } from './types';

interface Ingredient {
  stockItemId: string;
  quantity: string;
  unit: string;
}

/**
 * Wireframe 11's right panel: what one sale of a menu item uses ("25 ml of Viceroy 375ml"), with
 * the cost per sale, the Standard price and the margin, worked out as the form changes.
 */
export function RecipePanel({
  items,
  canEdit,
  initialMenuItem,
}: {
  items: StockItemRow[];
  canEdit: boolean;
  initialMenuItem?: string;
}) {
  const queries = useQueryClient();
  const toast = useToast();
  const menu = useQuery({
    queryKey: ['stock', 'recipe-items'],
    queryFn: () => api<RecipeMenuItem[]>('/api/admin/stock/recipes'),
  });
  const [menuItem, setMenuItem] = useState(initialMenuItem ?? '');
  const chosen = menuItem || menu.data?.[0]?.id || '';
  const recipe = useQuery({
    queryKey: ['stock', 'recipe', chosen],
    queryFn: () => api<Recipe>(`/api/admin/stock/recipes/${chosen}`),
    enabled: chosen !== '',
  });
  // What was typed, for the menu item it was typed for; otherwise the saved recipe.
  const [edits, setEdits] = useState<{ menuItemId: string; lines: Ingredient[] } | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const saved: Ingredient[] = (recipe.data?.menuItemId === chosen ? recipe.data.lines : []).map(
    (l) => ({
      stockItemId: l.stockItemId,
      quantity: String(l.quantity),
      unit: l.unit,
    }),
  );
  const lines = edits?.menuItemId === chosen ? edits.lines : saved;
  const setLines = (change: (lines: Ingredient[]) => Ingredient[]) =>
    setEdits({ menuItemId: chosen, lines: change(lines) });

  const active = items.filter((i) => i.active);
  const byId = new Map(items.map((i) => [i.id, i]));
  const cost = lines.reduce((sum, l) => {
    const item = byId.get(l.stockItemId);
    return item ? sum + Number(l.quantity || 0) * factorOf(item, l.unit) * item.averageCost : sum;
  }, 0);
  const price = recipe.data?.price ?? null;
  // The margin is on the price without VAT, as on the server; it follows the form as it changes.
  const net = recipe.data?.priceWithoutVat ?? null;
  const margin = net && net > 0 ? Math.round(((net - cost) / net) * 1000) / 10 : null;

  const update = (i: number, change: Partial<Ingredient>) =>
    setLines((ls) => ls.map((l, j) => (j === i ? { ...l, ...change } : l)));
  const save = async () => {
    setBusy(true);
    setError(null);
    try {
      const result = await api<Recipe>(`/api/admin/stock/recipes/${chosen}`, {
        method: 'PUT',
        body: {
          lines: lines
            .filter((l) => l.stockItemId)
            .map((l) => ({
              stockItemId: l.stockItemId,
              quantity: Number(l.quantity),
              unit: l.unit,
            })),
        },
      });
      queries.setQueryData(['stock', 'recipe', chosen], result);
      setEdits(null);
      void queries.invalidateQueries({ queryKey: ['stock', 'overview'] });
      void queries.invalidateQueries({ queryKey: ['stock', 'recipe-items'] });
      toast(t('admin.stock.recipeSaved', { name: result.menuItem }));
    } catch (e) {
      setError(e instanceof ApiError ? e.message : t('common.somethingWrong'));
    } finally {
      setBusy(false);
    }
  };

  return (
    <aside className="stock-recipe" aria-label={t('admin.stock.recipe')}>
      <h2 className="stock-section-title">{t('admin.stock.recipe')}</h2>
      <SelectField
        label={t('admin.stock.menuItem')}
        value={chosen}
        onChange={(e) => {
          setMenuItem(e.target.value);
          setError(null);
        }}
        options={(menu.data ?? []).map((m) => ({
          value: m.id,
          label: m.hasRecipe ? m.name : `${m.name} · ${t('admin.stock.noRecipeShort')}`,
        }))}
      />
      <p className="stock-recipe__label">{t('admin.stock.eachSaleUses')}</p>
      {lines.map((line, i) => {
        const item = byId.get(line.stockItemId);
        return (
          <div key={i} className="stock-recipe__line">
            <input
              className="nx-field__input stock-recipe__qty"
              aria-label={t('admin.stock.ingredientQuantity', { n: i + 1 })}
              inputMode="decimal"
              value={line.quantity}
              disabled={!canEdit}
              onChange={(e) => update(i, { quantity: e.target.value.replace(/[^\d.]/g, '') })}
            />
            <select
              className="nx-field__select"
              aria-label={t('admin.stock.ingredientUnit', { n: i + 1 })}
              value={line.unit}
              disabled={!canEdit}
              onChange={(e) => update(i, { unit: e.target.value })}
            >
              {(item ? unitsOf(item) : [line.unit]).map((u) => (
                <option key={u} value={u}>
                  {t('admin.stock.unitOf', { unit: u })}
                </option>
              ))}
            </select>
            <select
              className="nx-field__select"
              aria-label={t('admin.stock.ingredientItem', { n: i + 1 })}
              value={line.stockItemId}
              disabled={!canEdit}
              onChange={(e) => {
                const next = byId.get(e.target.value);
                update(i, { stockItemId: e.target.value, unit: next ? next.baseUnit : '' });
              }}
            >
              {active.map((x) => (
                <option key={x.id} value={x.id}>
                  {x.name}
                </option>
              ))}
            </select>
            {canEdit && (
              <Button
                aria-label={t('admin.stock.removeIngredient', { n: i + 1 })}
                onClick={() => setLines((ls) => ls.filter((_, j) => j !== i))}
              >
                ×
              </Button>
            )}
          </div>
        );
      })}
      {canEdit && (
        <button
          type="button"
          className="stock-recipe__add"
          onClick={() =>
            setLines((ls) => [
              ...ls,
              { stockItemId: active[0]?.id ?? '', quantity: '1', unit: active[0]?.baseUnit ?? '' },
            ])
          }
        >
          {t('admin.stock.addIngredient')}
        </button>
      )}
      <p className="stock-recipe__sums" data-testid="recipe-sums">
        {t('admin.stock.costLine', {
          cost: formatAmount(Math.round(cost * 100) / 100),
          price: price != null ? formatAmount(price) : '—',
          margin: margin != null ? `${margin}%` : '—',
        })}
      </p>
      {error && (
        <p className="nx-field__error" role="alert">
          {error}
        </p>
      )}
      {canEdit && (
        <Button
          variant="primary"
          size="lg"
          block
          disabled={busy || !chosen}
          onClick={() => void save()}
        >
          {t('admin.stock.saveRecipe')}
        </Button>
      )}
    </aside>
  );
}
