import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useState, type ReactNode } from 'react';
import { ApiError, api } from '../shared/api/client';
import { TausiMark } from '../shared/brand';
import { t } from '../shared/i18n/i18n';
import { Button, Field } from '../shared/ui';
import { AdminStep } from './steps/AdminStep';
import { BusinessStep } from './steps/BusinessStep';
import { DevicesStep } from './steps/DevicesStep';
import { FinishStep } from './steps/FinishStep';
import { LicenceStep } from './steps/LicenceStep';
import { MenuStep } from './steps/MenuStep';
import { StockStep } from './steps/StockStep';
import { skippable, stepKeys, type SetupStatus, type StepKey } from './types';
import './setup.css';

type Page = StepKey | 'finish';

export interface StepProps {
  status: SetupStatus;
  /** Saved: go on to the next step. */
  onNext: () => void;
}

/** Each step's form; the shell around it is the same for all (wireframe 21). */
const steps: Record<StepKey, (props: StepProps) => ReactNode> = {
  business: (p) => <BusinessStep {...p} />,
  admin: (p) => <AdminStep {...p} />,
  devices: (p) => <DevicesStep {...p} />,
  menu: (p) => <MenuStep {...p} />,
  stock: (p) => <StockStep {...p} />,
  licence: (p) => <LicenceStep {...p} />,
};

/**
 * The first-run setup wizard (/setup): the setup token, then six steps in a dark panel on the
 * left (the current one highlighted, finished ones ticked, skipped ones marked) and the step's form
 * on the right with Back, Next and, for steps 3 to 5, Skip. It resumes where it stopped.
 */
export function SetupWizard() {
  const queries = useQueryClient();
  const status = useQuery({
    queryKey: ['setup', 'status'],
    queryFn: () => api<SetupStatus>('/api/setup/status'),
  });
  const [page, setPage] = useState<Page | null>(null);
  const [error, setError] = useState<string | null>(null);

  const data = status.data;
  if (!data) return <main className="wizard wizard--plain">{t('common.loading')}</main>;
  if (data.complete) return <AlreadyComplete />;
  if (!data.unlocked)
    return (
      <TokenPage
        status={data}
        onUnlocked={() => void queries.invalidateQueries({ queryKey: ['setup'] })}
      />
    );

  const allDone = data.steps.every((s) => s.status !== 'Todo');
  const current: Page = page ?? data.current ?? (allDone ? 'finish' : 'licence');
  const index = current === 'finish' ? stepKeys.length : stepKeys.indexOf(current);
  const refresh = () => queries.invalidateQueries({ queryKey: ['setup'] });
  const next = async () => {
    await refresh();
    setError(null);
    setPage(index + 1 < stepKeys.length ? stepKeys[index + 1] : 'finish');
  };
  const skip = async () => {
    try {
      await api(`/api/setup/steps/${current}/skip`, { method: 'POST' });
      await next();
    } catch (e) {
      setError(e instanceof ApiError ? e.message : t('common.somethingWrong'));
    }
  };

  return (
    <div className="wizard">
      <aside className="wizard__side">
        <p className="wizard__brand">
          <TausiMark size={36} label={null} />
          {t('wizard.brand')}
        </p>
        <h1 className="wizard__heading">{t('wizard.heading')}</h1>
        <ol className="wizard__steps">
          {stepKeys.map((key, i) => {
            const step = data.steps.find((s) => s.key === key)!;
            const on = key === current;
            return (
              <li key={key}>
                <button
                  type="button"
                  className={`wizard__step${on ? ' wizard__step--on' : ''}`}
                  aria-current={on ? 'step' : undefined}
                  onClick={() => setPage(key)}
                >
                  <span
                    className={`wizard__number wizard__number--${step.status.toLowerCase()}`}
                    aria-hidden="true"
                  >
                    {step.status === 'Done' ? '✓' : i + 1}
                  </span>
                  <span>
                    {t(`wizard.steps.${key}`)}
                    {step.status === 'Skipped' && (
                      <span className="wizard__skipped"> · {t('wizard.skipped')}</span>
                    )}
                    <span className="visually-hidden"> ({t(`wizard.state.${step.status}`)})</span>
                  </span>
                </button>
              </li>
            );
          })}
        </ol>
        <p className="wizard__later">{t('wizard.later')}</p>
      </aside>
      <main className="wizard__main">
        {current === 'finish' ? (
          <FinishStep onBack={() => setPage('licence')} />
        ) : (
          <>
            <p className="wizard__count">{t('wizard.stepOf', { n: index + 1 })}</p>
            <h2 className="wizard__title">{t(`wizard.steps.${current}`)}</h2>
            {error && (
              <p className="nx-field__error" role="alert">
                {error}
              </p>
            )}
            <div className="wizard__body">
              {steps[current]({ status: data, onNext: () => void next() })}
            </div>
            <div className="wizard__footer">
              {index > 0 && (
                <Button onClick={() => setPage(stepKeys[index - 1])}>{t('common.back')}</Button>
              )}
              {skippable.includes(current) && (
                <Button variant="link" onClick={() => void skip()}>
                  {t('wizard.skip')}
                </Button>
              )}
            </div>
          </>
        )}
      </main>
    </div>
  );
}

/** The step's primary button, bottom right: "Next: first admin". */
export function NextButton({
  next,
  busy,
  disabled,
  onClick,
}: {
  next: StepKey | 'finish';
  busy?: boolean;
  disabled?: boolean;
  onClick: () => void;
}) {
  return (
    <div className="wizard__next">
      <Button variant="primary" size="lg" disabled={busy || disabled} onClick={onClick}>
        {t('wizard.next', { step: t(`wizard.nextName.${next}`) })}
      </Button>
    </div>
  );
}

function TokenPage({ status, onUnlocked }: { status: SetupStatus; onUnlocked: () => void }) {
  const [token, setToken] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [lockedUntil, setLockedUntil] = useState<string | null>(status.lockedUntil);
  const [busy, setBusy] = useState(false);
  const unlock = async () => {
    setBusy(true);
    setError(null);
    try {
      await api('/api/setup/unlock', { method: 'POST', body: { token } });
      onUnlocked();
    } catch (e) {
      if (e instanceof ApiError && e.status === 429) {
        setLockedUntil(String(e.problem.lockedUntil ?? ''));
        setError(t('wizard.token.locked'));
      } else setError(e instanceof ApiError ? e.message : t('common.somethingWrong'));
    } finally {
      setBusy(false);
    }
  };
  return (
    <main className="wizard wizard--plain">
      <form
        className="wizard__token"
        onSubmit={(e) => {
          e.preventDefault();
          void unlock();
        }}
      >
        <p className="wizard__brand wizard__brand--dark">{t('wizard.brand')}</p>
        <h1 className="wizard__title">{t('wizard.token.title')}</h1>
        <p>{t('wizard.token.help')}</p>
        <Field
          label={t('wizard.token.label')}
          className="wizard__token-field"
          value={token}
          autoComplete="off"
          autoCapitalize="characters"
          maxLength={9}
          onChange={(e) => setToken(e.target.value.toUpperCase())}
          error={error}
        />
        {lockedUntil && (
          <p className="wizard__note">
            {t('wizard.token.lockedUntil', {
              time: new Date(lockedUntil).toLocaleTimeString('en-GB', {
                hour: '2-digit',
                minute: '2-digit',
              }),
            })}
          </p>
        )}
        <Button
          type="submit"
          variant="primary"
          size="lg"
          disabled={busy || token.replace(/[^A-Z0-9]/g, '').length !== 8}
        >
          {t('wizard.token.unlock')}
        </Button>
      </form>
    </main>
  );
}

function AlreadyComplete() {
  return (
    <main className="wizard wizard--plain">
      <div className="wizard__token">
        <p className="wizard__brand wizard__brand--dark">{t('wizard.brand')}</p>
        <h1 className="wizard__title">{t('wizard.complete.title')}</h1>
        <p>{t('wizard.complete.text')}</p>
        <div className="wizard__links">
          <a className="nx-btn nx-btn--primary nx-btn--lg" href="/admin">
            {t('wizard.complete.admin')}
          </a>
          <a className="nx-btn nx-btn--secondary nx-btn--lg" href="/connect">
            {t('wizard.complete.terminal')}
          </a>
        </div>
      </div>
    </main>
  );
}
