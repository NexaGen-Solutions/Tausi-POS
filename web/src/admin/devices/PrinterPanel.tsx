import { useState } from 'react';
import { ApiError, api } from '../../shared/api/client';
import { t } from '../../shared/i18n/i18n';
import { Button, Field, SelectField, Toggle } from '../../shared/ui';
import type { ConnectionMode, PrinterRow, TerminalRow } from './types';

/** Add or edit a printer. The address fields depend on the connection; "Check connection" tries it. */
export function PrinterPanel({
  printer,
  terminals,
  onSaved,
  onStale,
}: {
  printer?: PrinterRow;
  terminals: TerminalRow[];
  onSaved: () => void;
  onStale: () => void;
}) {
  const [name, setName] = useState(printer?.name ?? '');
  const [mode, setMode] = useState<ConnectionMode>(printer?.connectionMode ?? 'Network9100');
  const network = printer?.connectionMode === 'Network9100' ? printer.address.split(':') : [];
  const [ip, setIp] = useState(network[0] ?? '');
  const [port, setPort] = useState(network[1] ?? '9100');
  const [share, setShare] = useState(
    printer?.connectionMode === 'WindowsShare' ? printer.address : '',
  );
  const [owner, setOwner] = useState(printer?.owningTerminalId ?? terminals[0]?.id ?? '');
  const [localName, setLocalName] = useState(
    printer?.connectionMode === 'ViaTerminal' ? printer.address : '',
  );
  const [paper, setPaper] = useState(String(printer?.paperWidthMm ?? 80));
  const [cut, setCut] = useState(printer?.cutAfterSlip ?? true);
  const [drawer, setDrawer] = useState(printer?.openDrawerOnReceipt ?? false);
  const [active, setActive] = useState(printer?.active ?? true);
  const [check, setCheck] = useState<{ reachable: boolean; message: string } | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const address = () =>
    mode === 'Network9100'
      ? `${ip.trim()}:${port.trim() || '9100'}`
      : mode === 'WindowsShare'
        ? share.trim()
        : mode === 'ViaTerminal'
          ? localName.trim()
          : '';
  const ownerTerminal = terminals.find((x) => x.id === owner);
  const reported = ownerTerminal?.localPrinters ?? [];

  const body = () => ({
    name: name.trim(),
    connectionMode: mode,
    address: address(),
    owningTerminalId: mode === 'ViaTerminal' ? owner || null : null,
    paperWidthMm: Number(paper),
    cutAfterSlip: cut,
    openDrawerOnReceipt: drawer,
    active,
    rowVersion: printer?.rowVersion ?? 0,
  });

  const runCheck = async () => {
    setCheck(null);
    try {
      setCheck(
        await api<{ reachable: boolean; message: string }>('/api/admin/printers/check', {
          method: 'POST',
          body: {
            connectionMode: mode,
            address: address(),
            owningTerminalId: mode === 'ViaTerminal' ? owner || null : null,
          },
        }),
      );
    } catch (e) {
      setCheck({
        reachable: false,
        message: e instanceof ApiError ? e.message : t('common.somethingWrong'),
      });
    }
  };

  const save = async () => {
    setBusy(true);
    setError(null);
    try {
      await api(printer ? `/api/admin/printers/${printer.id}` : '/api/admin/printers', {
        method: printer ? 'PUT' : 'POST',
        body: body(),
      });
      onSaved();
    } catch (e) {
      if (e instanceof ApiError && e.status === 409) onStale();
      setError(e instanceof ApiError ? e.message : t('common.somethingWrong'));
    } finally {
      setBusy(false);
    }
  };

  return (
    <form
      className="devices-panel__form"
      noValidate
      onSubmit={(e) => {
        e.preventDefault();
        void save();
      }}
    >
      <Field
        label={t('admin.devices.name')}
        value={name}
        maxLength={40}
        onChange={(e) => setName(e.target.value)}
      />
      <SelectField
        label={t('admin.devices.connectionMode')}
        value={mode}
        onChange={(e) => {
          setMode(e.target.value as ConnectionMode);
          setCheck(null);
        }}
        options={[
          { value: 'Network9100', label: t('admin.devices.modeNetwork') },
          { value: 'WindowsShare', label: t('admin.devices.modeShare') },
          { value: 'ViaTerminal', label: t('admin.devices.modeTerminal') },
          // Development only (the server refuses it elsewhere): offered just for a printer that has it.
          ...(printer?.connectionMode === 'File'
            ? [{ value: 'File' as const, label: t('admin.devices.modeFile') }]
            : []),
        ]}
      />
      {mode === 'Network9100' && (
        <div className="devices-panel__row">
          <Field
            label={t('admin.devices.ip')}
            value={ip}
            inputMode="decimal"
            onChange={(e) => setIp(e.target.value)}
          />
          <Field
            label={t('admin.devices.port')}
            value={port}
            inputMode="numeric"
            onChange={(e) => setPort(e.target.value)}
          />
        </div>
      )}
      {mode === 'WindowsShare' && (
        <Field
          label={t('admin.devices.share')}
          value={share}
          hint={t('admin.devices.shareHint')}
          onChange={(e) => setShare(e.target.value)}
        />
      )}
      {mode === 'ViaTerminal' && (
        <>
          <SelectField
            label={t('admin.devices.owningTerminal')}
            value={owner}
            onChange={(e) => setOwner(e.target.value)}
            options={terminals.map((x) => ({ value: x.id, label: x.name }))}
          />
          {reported.length > 0 ? (
            <SelectField
              label={t('admin.devices.localPrinter')}
              value={localName}
              onChange={(e) => setLocalName(e.target.value)}
              options={[
                { value: '', label: '—' },
                ...reported.map((n) => ({ value: n, label: n })),
              ]}
            />
          ) : (
            <Field
              label={t('admin.devices.localPrinter')}
              value={localName}
              hint={t('admin.devices.localPrinterHint')}
              onChange={(e) => setLocalName(e.target.value)}
            />
          )}
        </>
      )}
      <SelectField
        label={t('admin.devices.paper')}
        value={paper}
        onChange={(e) => setPaper(e.target.value)}
        options={[
          { value: '80', label: '80 mm' },
          { value: '58', label: '58 mm' },
        ]}
      />
      <Toggle label={t('admin.devices.cut')} checked={cut} onChange={setCut} />
      <Toggle label={t('admin.devices.drawer')} checked={drawer} onChange={setDrawer} />
      <Toggle label={t('admin.devices.active')} checked={active} onChange={setActive} />
      <Button onClick={() => void runCheck()}>{t('admin.devices.check')}</Button>
      {check && (
        <p className={check.reachable ? 'devices-panel__ok' : 'devices-panel__error'} role="status">
          {check.reachable ? t('admin.devices.reachable') : check.message}
        </p>
      )}
      {error && (
        <p className="devices-panel__error" role="alert">
          {error}
        </p>
      )}
      <Button type="submit" variant="primary" size="lg" block disabled={busy}>
        {t('common.save')}
      </Button>
    </form>
  );
}
