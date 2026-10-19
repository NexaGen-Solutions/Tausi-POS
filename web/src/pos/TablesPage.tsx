import { OfflineBanner } from '../shared/connection/OfflineBanner';
import { t } from '../shared/i18n/i18n';
import { PosTopBar } from './PosTopBar';

/** Placeholder until the table map (Screen 2). */
export function TablesPage() {
  return (
    <div className="pos-screen">
      <PosTopBar />
      <OfflineBanner />
      <main className="pos-center">
        <p className="pos-center__title">{t('pos.tablesPlaceholder')}</p>
      </main>
    </div>
  );
}
