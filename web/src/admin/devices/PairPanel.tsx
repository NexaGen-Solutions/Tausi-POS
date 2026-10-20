import { useState } from 'react';
import { ApiError, api } from '../../shared/api/client';
import { t } from '../../shared/i18n/i18n';
import { Button, Field, Modal, SelectField } from '../../shared/ui';
import type { TerminalRow } from './types';

/** Pair a terminal: the code it shows, and which terminal it is (existing or new). */
export function PairPanel({
  terminals,
  onPaired,
}: {
  terminals: TerminalRow[];
  onPaired: () => void;
}) {
  const [code, setCode] = useState('');
  const [mode, setMode] = useState<'existing' | 'new'>(
    terminals.some((x) => !x.paired) ? 'existing' : 'new',
  );
  const [terminalId, setTerminalId] = useState(
    terminals.find((x) => !x.paired)?.id ?? terminals[0]?.id ?? '',
  );
  const [newName, setNewName] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [done, setDone] = useState<string | null>(null);
  const [replace, setReplace] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const pair = async (confirmReplace = false) => {
    setBusy(true);
    setError(null);
    setReplace(null);
    try {
      const result = await api<{ terminalName: string }>('/api/admin/terminals/pair', {
        method: 'POST',
        body: {
          code: code.replace(/\D/g, ''),
          terminalId: mode === 'existing' ? terminalId : null,
          newTerminalName: mode === 'new' ? newName.trim() : null,
          confirmReplace,
        },
      });
      setDone(t('admin.devices.paired', { name: result.terminalName }));
      setCode('');
      setNewName('');
      onPaired();
    } catch (e) {
      if (e instanceof ApiError && e.status === 409 && e.problem.code === 'already_paired')
        setReplace(e.message);
      else if (e instanceof ApiError && e.status === 429) setError(t('admin.devices.blocked'));
      else setError(e instanceof ApiError ? e.message : t('common.somethingWrong'));
    } finally {
      setBusy(false);
    }
  };

  const chosen = terminals.find((x) => x.id === terminalId);

  return (
    <form
      className="devices-panel__form"
      noValidate
      onSubmit={(e) => {
        e.preventDefault();
        void pair();
      }}
    >
      <Field
        label={t('admin.devices.code')}
        value={code}
        inputMode="numeric"
        autoComplete="off"
        maxLength={7}
        className="devices-panel__code"
        onChange={(e) => setCode(e.target.value)}
        autoFocus
      />
      <fieldset className="devices-panel__choice">
        <legend>{t('admin.devices.which')}</legend>
        <label className="devices-panel__check">
          <input
            type="radio"
            name="which"
            checked={mode === 'existing'}
            onChange={() => setMode('existing')}
            disabled={terminals.length === 0}
          />
          {t('admin.devices.existing')}
        </label>
        <label className="devices-panel__check">
          <input
            type="radio"
            name="which"
            checked={mode === 'new'}
            onChange={() => setMode('new')}
          />
          {t('admin.devices.newTerminal')}
        </label>
      </fieldset>
      {mode === 'existing' ? (
        <SelectField
          label={t('admin.devices.colTerminal')}
          value={terminalId}
          onChange={(e) => setTerminalId(e.target.value)}
          options={terminals.map((x) => ({
            value: x.id,
            label: x.paired ? `${x.name} ${t('admin.devices.pairedMark')}` : x.name,
          }))}
        />
      ) : (
        <Field
          label={t('admin.devices.newName')}
          value={newName}
          maxLength={40}
          onChange={(e) => setNewName(e.target.value)}
        />
      )}
      {error && (
        <p className="devices-panel__error" role="alert">
          {error}
        </p>
      )}
      {done && (
        <p className="devices-panel__ok" role="status">
          {done}
        </p>
      )}
      <Button
        type="submit"
        variant="primary"
        size="lg"
        block
        disabled={busy || code.replace(/\D/g, '').length !== 6}
      >
        {t('admin.devices.pairButton')}
      </Button>
      {replace && (
        <Modal
          title={t('admin.devices.replaceTitle', { name: chosen?.name ?? '' })}
          onClose={() => setReplace(null)}
        >
          <div className="devices-confirm">
            <p>{replace}</p>
            <Button variant="danger" size="lg" block onClick={() => void pair(true)}>
              {t('admin.devices.replaceConfirm')}
            </Button>
            <Button block onClick={() => setReplace(null)}>
              {t('common.cancel')}
            </Button>
          </div>
        </Modal>
      )}
    </form>
  );
}
