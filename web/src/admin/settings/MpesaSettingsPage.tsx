import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { Link } from 'react-router';
import { ApiError, api } from '../../shared/api/client';
import { t } from '../../shared/i18n/i18n';
import { Button, Field, Toggle, useToast } from '../../shared/ui';
import type { Settings } from './types';
import './settings.css';

interface MpesaAdmin {
  secrets: {
    consumerKey: boolean;
    consumerSecret: boolean;
    passkey: boolean;
    relayApiKey: boolean;
    unreadable: boolean;
  };
  relay: {
    state: 'NotSetUp' | 'Idle' | 'Connected' | 'Unreachable';
    reason: string | null;
    lastMessageAt: string | null;
  };
  c2BRegisteredAt: string | null;
  setUp: boolean;
}

interface Check {
  name: string;
  ok: boolean;
  message: string;
}

type Secret = 'consumerKey' | 'consumerSecret' | 'passkey' | 'relayApiKey';
const SECRETS: Secret[] = ['consumerKey', 'consumerSecret', 'passkey', 'relayApiKey'];
const RULES = [
  'mpesa.qrOnBill',
  'mpesa.stkEnabled',
  'mpesa.waiterPrompts',
  'mpesa.allowTypedCodes',
];

const time = (iso: string) =>
  new Date(iso).toLocaleTimeString('en-GB', { hour: '2-digit', minute: '2-digit' });

/**
 * Board 26: Settings → M-Pesa (settings.manage). The account (Buy Goods or Paybill, the numbers,
 * the Daraja credentials and the relay key, shown as •••• with Change), Sandbox or Live, Test
 * connection, the relay connection and the M-Pesa rules.
 */
export function MpesaSettingsPage() {
  const queries = useQueryClient();
  const toast = useToast();
  const settings = useQuery({
    queryKey: ['settings'],
    queryFn: () => api<Settings>('/api/admin/settings'),
  });
  const mpesa = useQuery({
    queryKey: ['admin-mpesa'],
    queryFn: () => api<MpesaAdmin>('/api/admin/mpesa'),
    refetchInterval: 15_000,
  });
  const section = settings.data?.sections.find((s) => s.key === 'mpesa');
  const [edits, setEdits] = useState<Record<string, unknown>>({});
  const [secrets, setSecrets] = useState<Partial<Record<Secret, string>>>({});
  const [changing, setChanging] = useState<Partial<Record<Secret, boolean>>>({});
  const [checks, setChecks] = useState<Check[] | null>(null);
  const [busy, setBusy] = useState(false);

  if (!section || !mpesa.data) return <main className="settings" aria-busy="true" />;
  const entry = (key: string) => section.entries.find((e) => e.key === key)!;
  const value = (key: string) => (key in edits ? edits[key] : entry(key).value);
  const set = (key: string, v: unknown) => setEdits((all) => ({ ...all, [key]: v }));
  const paybill = value('mpesa.type') === 'Paybill';
  const changed = section.entries.filter((e) => e.key in edits && edits[e.key] !== e.value);
  const dirty = changed.length > 0 || Object.values(secrets).some((s) => s !== undefined);

  const save = async () => {
    setBusy(true);
    try {
      if (changed.length > 0)
        await api('/api/admin/settings', {
          method: 'PUT',
          body: {
            version: settings.data!.version,
            values: Object.fromEntries(changed.map((e) => [e.key, edits[e.key]])),
          },
        });
      if (Object.values(secrets).some((s) => s !== undefined))
        await api('/api/admin/mpesa/secrets', { method: 'PUT', body: secrets });
      setSecrets({});
      setChanging({});
      setEdits({});
      await queries.invalidateQueries({ queryKey: ['settings'] });
      await queries.invalidateQueries({ queryKey: ['admin-mpesa'] });
      toast(t('admin.mpesa.saved'));
    } catch (e) {
      toast(e instanceof ApiError ? e.message : t('common.somethingWrong'), { tone: 'warning' });
    } finally {
      setBusy(false);
    }
  };

  const test = async () => {
    setBusy(true);
    try {
      setChecks(await api<Check[]>('/api/admin/mpesa/test', { method: 'POST' }));
      void queries.invalidateQueries({ queryKey: ['admin-mpesa'] });
    } catch (e) {
      toast(e instanceof ApiError ? e.message : t('common.somethingWrong'), { tone: 'warning' });
    } finally {
      setBusy(false);
    }
  };

  const text = (key: string, label?: string) => (
    <Field
      label={label ?? entry(key).label}
      value={String(value(key) ?? '')}
      maxLength={entry(key).maxLength || undefined}
      onChange={(e) => set(key, e.target.value)}
      inputMode={key === 'mpesa.relayUrl' ? 'url' : 'numeric'}
    />
  );
  const choice = (key: string, options: [string, string][]) => (
    <div className="mpesa-choice" role="group" aria-label={entry(key).label}>
      <span className="nx-field__label">{entry(key).label}</span>
      <div className="mpesa-choice__buttons">
        {options.map(([v, label]) => (
          <button
            key={v}
            type="button"
            className={`mpesa-choice__button${value(key) === v ? ' mpesa-choice__button--on' : ''}`}
            aria-pressed={value(key) === v}
            onClick={() => set(key, v)}
          >
            {label}
          </button>
        ))}
      </div>
    </div>
  );
  const stored = mpesa.data.secrets;
  const secret = (name: Secret) =>
    changing[name] || !stored[name] ? (
      <Field
        key={name}
        label={t(`admin.mpesa.secret.${name}`)}
        type="password"
        autoComplete="off"
        value={secrets[name] ?? ''}
        onChange={(e) => setSecrets((s) => ({ ...s, [name]: e.target.value }))}
      />
    ) : (
      <div key={name} className="mpesa-secret">
        <span className="nx-field__label">{t(`admin.mpesa.secret.${name}`)}</span>
        <span className="mpesa-secret__value">
          <span aria-label={t('admin.mpesa.stored')}>••••••••••••</span>
          <Button variant="link" onClick={() => setChanging((c) => ({ ...c, [name]: true }))}>
            {t('admin.mpesa.change')}
          </Button>
        </span>
      </div>
    );
  const relay = mpesa.data.relay;

  return (
    <main className="settings">
      <div className="settings__main">
        <div className="settings__header">
          <h1 className="admin__title">{t('admin.mpesa.title')}</h1>
          <div className="settings__buttons">
            <Link className="nx-btn nx-btn--link nx-btn--md" to="/admin/settings">
              {t('admin.mpesa.backToSettings')}
            </Link>
            <Button variant="primary" disabled={!dirty || busy} onClick={() => void save()}>
              {t('admin.settings.save')}
            </Button>
          </div>
        </div>
        {stored.unreadable && (
          <p className="nx-field__error" role="alert">
            {t('admin.mpesa.unreadable')}
          </p>
        )}
        <div className="settings__columns">
          <section className="settings-panel" aria-labelledby="mpesa-account">
            <h2 id="mpesa-account" className="settings-panel__title">
              {t('admin.mpesa.account')}
            </h2>
            {choice('mpesa.type', [
              ['BuyGoods', t('admin.mpesa.buyGoods')],
              ['Paybill', t('admin.mpesa.paybill')],
            ])}
            {paybill ? (
              text('mpesa.paybillNumber')
            ) : (
              <>
                {text('mpesa.tillNumber')}
                {text('mpesa.storeNumber')}
                <p className="settings-panel__help">{entry('mpesa.storeNumber').help}</p>
              </>
            )}
            {SECRETS.slice(0, 3).map(secret)}
            {choice('mpesa.environment', [
              ['Sandbox', t('admin.mpesa.sandbox')],
              ['Live', t('admin.mpesa.live')],
            ])}
            {text('mpesa.relayUrl')}
            {secret('relayApiKey')}
            <Button disabled={busy || dirty} onClick={() => void test()}>
              {t('admin.mpesa.test')}
            </Button>
            {dirty && <p className="settings-panel__help">{t('admin.mpesa.saveFirst')}</p>}
            {checks && (
              <ul className="mpesa-checks" aria-label={t('admin.mpesa.test')}>
                {checks.map((c) => (
                  <li key={c.name} className={c.ok ? 'mpesa-check--ok' : 'mpesa-check--fail'}>
                    <span aria-hidden="true">{c.ok ? '✓' : '✗'}</span> <strong>{c.name}</strong> ·{' '}
                    {c.message}
                  </li>
                ))}
              </ul>
            )}
          </section>
          <div className="settings__right">
            <section className="settings-panel" aria-labelledby="mpesa-connection">
              <h2 id="mpesa-connection" className="settings-panel__title">
                {t('admin.mpesa.connection')}
              </h2>
              <p className={`mpesa-relay mpesa-relay--${relay.state}`} role="status">
                <span className="mpesa-relay__dot" aria-hidden="true" />
                {t(`admin.mpesa.relay.${relay.state}`, { reason: relay.reason ?? '' })}
                {relay.lastMessageAt &&
                  ` · ${t('admin.mpesa.lastMessage', { time: time(relay.lastMessageAt) })}`}
              </p>
              <p className="settings-panel__help">{t('admin.mpesa.connectionHelp')}</p>
              <h3 className="settings-panel__title">{t('admin.mpesa.rules')}</h3>
              {RULES.map((key) => (
                <Toggle
                  key={key}
                  label={entry(key).label}
                  checked={value(key) === true}
                  onChange={(on) => set(key, on)}
                />
              ))}
            </section>
          </div>
        </div>
      </div>
    </main>
  );
}
