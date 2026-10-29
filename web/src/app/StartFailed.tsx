import { t } from '../shared/i18n/i18n';

/**
 * Shown instead of a blank screen when the app can't start (the terminal app refused to give this page
 * its settings, for example). It names the error, for support.
 */
export function StartFailed({ error }: { error: unknown }) {
  const detail = error instanceof Error ? error.message : String(error);
  return (
    <main className="start-failed" role="alert">
      <h1>{t('startFailed.title')}</h1>
      <p>{t('startFailed.help')}</p>
      <pre>{detail}</pre>
      <button
        type="button"
        className="nx-btn nx-btn--primary nx-btn--lg"
        onClick={() => location.reload()}
      >
        {t('startFailed.retry')}
      </button>
    </main>
  );
}
