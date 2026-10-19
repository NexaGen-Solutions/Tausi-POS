import { useEffect, useState } from 'react';
import { useNavigate } from 'react-router';
import { ApiError, api, setApiBase } from '../shared/api/client';
import {
  isBundledTerminalPage,
  loadDevice,
  openServer,
  saveDevice,
  serverUrl,
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

/**
 * First run of a terminal. The terminal app first asks for the server address; then this screen
 * shows a pairing code until an admin registers the terminal, and stores the device token.
 */
export function SetupPage() {
  const bundled = isBundledTerminalPage();
  const [server, setServer] = useState<string | null>(bundled ? serverUrl() : '');

  return (
    <div className="pos-screen">
      <TopBar title={t('setup.title')} subtitle={t('common.appName')} />
      <main className="setup">
        {server === null ? (
          <ServerStep onConnected={setServer} />
        ) : (
          <PairingStep server={server} bundled={bundled} />
        )}
      </main>
    </div>
  );
}

function ServerStep({ onConnected }: { onConnected: (url: string) => void }) {
  const [address, setAddress] = useState('http://');
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const connect = async () => {
    setBusy(true);
    setError(null);
    const url = address.trim().replace(/\/+$/, '');
    try {
      const response = await fetch(`${url}/api/health`);
      if (!response.ok) throw new Error();
      setApiBase(url);
      onConnected(url);
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

function PairingStep({ server, bundled }: { server: string; bundled: boolean }) {
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
          await saveDevice({ serverUrl: server || null, deviceToken: claim.deviceToken });
          await loadDevice();
          setNote(t('setup.paired', { terminal: claim.terminalName ?? '' }));
          if (bundled) await openServer();
          else navigate('/pos', { replace: true });
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
  }, [attempt, bundled, navigate, server]);

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
      {note && <p className="setup__note">{note}</p>}
    </section>
  );
}
