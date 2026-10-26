import { useEffect, useState } from 'react';
import { useNavigate, useSearchParams } from 'react-router';
import { ApiError, api, apiUrl, setApiBase } from '../shared/api/client';
import {
  isBundledTerminalPage,
  isTauri,
  loadDevice,
  openServer,
  saveDevice,
  serverUrl,
  suggestedServerUrl,
  setServerUrl,
} from '../shared/device/device';
import { t } from '../shared/i18n/i18n';
import { Button, Field, TopBar } from '../shared/ui';
import './pos.css';

interface PairingStarted {
  pairingId: string;
  code: string;
  secret: string;
  expiresAt: string;
}

interface PairingClaim {
  status: 'waiting' | 'paired';
  deviceToken: string | null;
  terminalName: string | null;
}

/** How long "Paired as Counter A" shows before the PIN pad. */
const pairedNoteFor = 1500;

/**
 * First run of a terminal. The terminal app first asks for the server address; then this screen
 * shows a pairing code until an admin pairs it from the admin site, and stores the device token.
 * In the terminal app the server address can be changed here, after an admin signs in.
 */
export function SetupPage() {
  const bundled = isBundledTerminalPage();
  const [params] = useSearchParams();
  const [step, setStep] = useState<'server' | 'admin' | 'pairing'>(
    bundled && !serverUrl() ? 'server' : 'pairing',
  );

  return (
    <div className="pos-screen">
      <TopBar title={t('setup.title')} subtitle={t('common.appName')} />
      <main className="setup">
        {step === 'server' && (
          <ServerStep bundled={bundled} onConnected={() => setStep('pairing')} />
        )}
        {step === 'admin' && <AdminStep onVerified={() => setStep('server')} />}
        {step === 'pairing' && (
          <div className="setup__stack">
            {params.get('revoked') && (
              <p className="setup__note" role="status">
                {t('setup.revoked')}
              </p>
            )}
            <PairingStep bundled={bundled} />
            {isTauri() && (
              <Button variant="link" onClick={() => setStep('admin')}>
                {t('setup.changeServer')}
              </Button>
            )}
          </div>
        )}
      </main>
    </div>
  );
}

function ServerStep({ bundled, onConnected }: { bundled: boolean; onConnected: () => void }) {
  const [address, setAddress] = useState(serverUrl() ?? suggestedServerUrl() ?? 'http://');
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const connect = async () => {
    setBusy(true);
    setError(null);
    const url = address.trim().replace(/\/+$/, '');
    try {
      const response = await fetch(`${url}/api/health`);
      if (!response.ok) throw new Error();
      // The app keeps only this server (and restarts to apply it).
      await setServerUrl(url);
      if (bundled) setApiBase(url);
      await loadDevice();
      onConnected();
    } catch {
      setError(t('setup.serverUnreachable'));
    } finally {
      setBusy(false);
    }
  };

  return (
    <form
      className="setup__panel"
      onSubmit={(event) => {
        event.preventDefault();
        void connect();
      }}
    >
      <Field
        label={t('setup.serverLabel')}
        hint={t('setup.serverHint')}
        value={address}
        onChange={(event) => setAddress(event.target.value)}
        error={error}
        autoFocus
        inputMode="url"
      />
      <Button type="submit" variant="primary" size="lg" disabled={busy}>
        {t('setup.connect')}
      </Button>
    </form>
  );
}

/** An admin signs in (checked by the current server) before the server address can change. */
function AdminStep({ onVerified }: { onVerified: () => void }) {
  const [userName, setUserName] = useState('');
  const [password, setPassword] = useState('');
  const [error, setError] = useState<string | null>(null);

  const verify = async () => {
    setError(null);
    try {
      const response = await fetch(apiUrl('/api/terminals/verify-admin'), {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ userName, password }),
      });
      if (!response.ok) throw new Error();
      onVerified();
    } catch {
      setError(t('admin.loginFailed'));
    }
  };

  return (
    <form
      className="setup__panel"
      onSubmit={(event) => {
        event.preventDefault();
        void verify();
      }}
    >
      <p className="setup__note">{t('setup.adminLogin')}</p>
      <Field
        label={t('admin.userName')}
        value={userName}
        onChange={(e) => setUserName(e.target.value)}
        autoFocus
      />
      <Field
        label={t('admin.password')}
        type="password"
        value={password}
        onChange={(e) => setPassword(e.target.value)}
        error={error}
      />
      <Button type="submit" variant="primary" size="lg" disabled={!userName || !password}>
        {t('setup.verify')}
      </Button>
    </form>
  );
}

function PairingStep({ bundled }: { bundled: boolean }) {
  const navigate = useNavigate();
  const [pairing, setPairing] = useState<PairingStarted | null>(null);
  const [note, setNote] = useState<string | null>(null);
  const [attempt, setAttempt] = useState(0);

  useEffect(() => {
    let stopped = false;
    let timer: number | undefined;

    const poll = async (started: PairingStarted) => {
      try {
        const claim = await api<PairingClaim>('/api/terminals/pairing/claim', {
          method: 'POST',
          body: { pairingId: started.pairingId, secret: started.secret },
        });
        if (stopped) return;
        if (claim.status === 'paired' && claim.deviceToken) {
          await saveDevice({ serverUrl: serverUrl(), deviceToken: claim.deviceToken });
          await loadDevice();
          setNote(t('setup.paired', { terminal: claim.terminalName ?? '' }));
          timer = window.setTimeout(() => {
            if (bundled) void openServer();
            else navigate('/pos', { replace: true });
          }, pairedNoteFor);
          return;
        }
      } catch (error) {
        if (stopped) return;
        if (error instanceof ApiError && error.status === 404) {
          setNote(t('setup.pairingExpired'));
          setAttempt((a) => a + 1);
          return;
        }
      }
      timer = window.setTimeout(() => void poll(started), 2000);
    };

    void (async () => {
      try {
        const started = await api<PairingStarted>('/api/terminals/pairing', { method: 'POST' });
        if (stopped) return;
        setPairing(started);
        void poll(started);
      } catch {
        if (!stopped) timer = window.setTimeout(() => setAttempt((a) => a + 1), 3000);
      }
    })();

    return () => {
      stopped = true;
      window.clearTimeout(timer);
    };
  }, [attempt, bundled, navigate]);

  const code = pairing ? `${pairing.code.slice(0, 3)} ${pairing.code.slice(3)}` : '··· ···';
  const expires = pairing
    ? new Date(pairing.expiresAt).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })
    : '';

  return (
    <section className="setup__panel" aria-live="polite">
      <h2>{t('setup.pairingTitle')}</h2>
      <p className="setup__code" aria-label={pairing?.code}>
        {code}
      </p>
      <p className="setup__note">{t('setup.pairingHelp')}</p>
      {pairing && <p className="setup__note">{t('setup.pairingExpires', { time: expires })}</p>}
      {note && (
        <p className="setup__note setup__note--strong" role="status">
          {note}
        </p>
      )}
    </section>
  );
}
