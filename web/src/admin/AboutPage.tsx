import { useQuery } from '@tanstack/react-query';
import { useState } from 'react';
import { Link } from 'react-router';
import { api } from '../shared/api/client';
import { TausiLogo } from '../shared/brand';
import { t } from '../shared/i18n/i18n';
import { Button } from '../shared/ui';
import type { LicenceStatus } from '../setup/types';
import './admin.css';

/**
 * About Tausi POS: the logo, version, publisher and licence summary. Reached from the admin footer
 * (inside the admin site) and from the terminal's setup screen (standalone, before pairing, when
 * there is no session to read the licence with).
 */
export function AboutPage({ standalone = false }: { standalone?: boolean }) {
  const licence = useQuery({
    queryKey: ['licence'],
    queryFn: () => api<LicenceStatus>('/api/licence'),
    retry: false,
  });
  const l = licence.data;
  // Shown in the page, not a new tab: the terminal app in kiosk mode has no tabs or back button.
  const [showNotices, setShowNotices] = useState(false);
  const notices = useQuery({
    queryKey: ['third-party-notices'],
    queryFn: async () => {
      const response = await fetch('/third-party-notices.txt');
      if (!response.ok) throw new Error(String(response.status));
      return response.text();
    },
    enabled: showNotices,
    staleTime: Infinity,
  });

  const about = (
    <section className="about" aria-labelledby="about-title">
      <div className="about__logo">
        <TausiLogo width={360} />
      </div>
      <div className="about__body">
        <h1 id="about-title" className="about__title">
          {t('about.title')}
        </h1>
        <p className="mono">{t('about.version', { version: __APP_VERSION__ })}</p>
        <p>{t('about.copyright')}</p>
        <p>
          <a href="https://nexagensolutions.com" target="_blank" rel="noreferrer">
            {t('about.website')}
          </a>
        </p>
        <h2 className="about__subtitle">{t('about.licence')}</h2>
        <p>
          {l?.edition
            ? t('about.licenceLine', {
                status: t(`admin.licence.status.${l.status}`),
                edition: l.edition ?? '-',
                business: l.business ?? '-',
              })
            : l
              ? t(`admin.licence.status.${l.status}`)
              : t('about.licenceSignedOut')}
        </p>
        <p>
          <Button aria-expanded={showNotices} onClick={() => setShowNotices((on) => !on)}>
            {t('about.notices')}
          </Button>
        </p>
        {showNotices && (
          <pre className="about__notices" tabIndex={0} aria-label={t('about.notices')}>
            {notices.data ?? (notices.error ? t('common.somethingWrong') : '…')}
          </pre>
        )}
        {standalone && (
          <p>
            <Link to="/connect">{t('about.back')}</Link>
          </p>
        )}
      </div>
    </section>
  );

  return standalone ? (
    <main className="about-page">{about}</main>
  ) : (
    <main className="admin__content">{about}</main>
  );
}
