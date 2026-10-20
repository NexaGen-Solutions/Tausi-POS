import { useQuery } from '@tanstack/react-query';
import { api } from '../shared/api/client';
import { formatAmount } from '../shared/format/money';
import { t } from '../shared/i18n/i18n';

interface PosMenu {
  menuVersion: string;
  priceListName: string;
  categories: {
    id: string;
    name: string;
    items: {
      id: string;
      name: string;
      shortName: string;
      price: number;
      groups: { name: string }[];
    }[];
  }[];
}

/**
 * TEMPORARY: a read-only view of this terminal's menu, to check prices before the order screen
 * exists. Remove it in the order-screen task. Refreshes on menu.changed, and every minute in case
 * an event was missed.
 */
export function TestMenu() {
  const menu = useQuery({
    queryKey: ['pos-menu'],
    queryFn: () => api<PosMenu>('/api/pos/menu'),
    refetchInterval: 60_000,
    staleTime: 0,
  });
  if (!menu.data) return null;
  return (
    <section className="test-menu" aria-label={t('pos.testMenu')}>
      <h2>{t('pos.testMenu')}</h2>
      <p className="test-menu__note">
        {t('pos.testMenuNote')} {t('pos.testMenuList', { list: menu.data.priceListName })}
      </p>
      <div className="test-menu__columns">
        {menu.data.categories.map((c) => (
          <div key={c.id}>
            <h3>{c.name}</h3>
            <ul>
              {c.items.map((i) => (
                <li key={i.id}>
                  <span>{i.shortName}</span>
                  <span className="mono" data-testid={`price-${i.name}`}>
                    {formatAmount(i.price)}
                  </span>
                </li>
              ))}
            </ul>
          </div>
        ))}
      </div>
    </section>
  );
}
