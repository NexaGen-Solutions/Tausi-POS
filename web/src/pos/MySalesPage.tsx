import { useNavigate } from 'react-router';
import { t } from '../shared/i18n/i18n';
import { Button } from '../shared/ui';
import { PosTopBar } from './PosTopBar';

/** Placeholder until My sales (Task 9). */
export function MySalesPage() {
  const navigate = useNavigate();
  return (
    <div className="pos-screen">
      <PosTopBar />
      <main className="pos-center">
        <p className="pos-center__title">{t('pos.mySales')}</p>
        <p className="pos-center__text">{t('pos.mySalesComing')}</p>
        <Button size="lg" onClick={() => navigate('/pos/tables')}>
          {t('pos.backToTables')}
        </Button>
      </main>
    </div>
  );
}
