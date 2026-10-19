import { t } from '../shared/i18n/i18n';

export function ComingSoonPage({ title }: { title: string }) {
  return (
    <main className="admin__content">
      <h1 className="admin__title">{title}</h1>
      <p>{t('admin.comingSoon')}</p>
    </main>
  );
}
