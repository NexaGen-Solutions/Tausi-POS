import { useState } from 'react';
import { ApiError, api } from '../../shared/api/client';
import { t } from '../../shared/i18n/i18n';
import { Button, Field, Modal, SelectField, Toggle } from '../../shared/ui';
import { routingPreview, type PrinterRow, type TerminalRow } from './types';

interface PriceListOption {
  id: string;
  name: string;
  active: boolean;
}

/** Edit a terminal: name, price list, dispense printer and header, receipt printer, active; revoke. */
export function TerminalPanel({
  terminal,
  printers,
  priceLists,
  onSaved,
  onStale,
}: {
  terminal: TerminalRow;
  printers: PrinterRow[];
  priceLists: PriceListOption[];
  onSaved: () => void;
  onStale: () => void;
}) {
  const [name, setName] = useState(terminal.name);
  const [priceListId, setPriceListId] = useState(terminal.priceListId ?? '');
  const [dispenseId, setDispenseId] = useState(terminal.dispensePrinterId ?? '');
  const [header, setHeader] = useState(terminal.dispenseHeader ?? '');
  const [receiptId, setReceiptId] = useState(terminal.receiptPrinterId ?? '');
  const [active, setActive] = useState(terminal.active);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [revoking, setRevoking] = useState(false);

  const usable = printers.filter((p) => p.active || p.id === dispenseId || p.id === receiptId);
  const printerName = (id: string) => printers.find((p) => p.id === id)?.name ?? null;
  const shownHeader = header.trim() || terminal.effectiveDispenseHeader;
  const preview = routingPreview(
    name.trim() || terminal.name,
    printerName(receiptId),
    shownHeader,
    printerName(dispenseId),
  );

  const save = async () => {
    setBusy(true);
    setError(null);
    try {
      await api(`/api/admin/terminals/${terminal.id}`, {
        method: 'PUT',
        body: {
          name: name.trim(),
          priceListId: priceListId || null,
          dispensePrinterId: dispenseId || null,
          dispenseHeader: header.trim() || null,
          receiptPrinterId: receiptId || null,
          active,
          rowVersion: terminal.rowVersion,
        },
      });
      onSaved();
    } catch (e) {
      if (e instanceof ApiError && e.status === 409) onStale();
      setError(e instanceof ApiError ? e.message : t('common.somethingWrong'));
    } finally {
      setBusy(false);
    }
  };

  const revoke = async () => {
    setRevoking(false);
    try {
      await api(`/api/admin/terminals/${terminal.id}/revoke`, { method: 'POST' });
      onSaved();
    } catch (e) {
      setError(e instanceof ApiError ? e.message : t('common.somethingWrong'));
    }
  };

  const printerOptions = [
    { value: '', label: t('admin.devices.none') },
    ...usable.map((p) => ({ value: p.id, label: p.name })),
  ];

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
        label={t('admin.devices.priceList')}
        value={priceListId}
        onChange={(e) => setPriceListId(e.target.value)}
        options={[
          { value: '', label: t('admin.devices.defaultList') },
          ...priceLists
            .filter((l) => l.active || l.id === priceListId)
            .map((l) => ({ value: l.id, label: l.name })),
        ]}
      />
      <SelectField
        label={t('admin.devices.dispensePrinter')}
        value={dispenseId}
        onChange={(e) => setDispenseId(e.target.value)}
        options={printerOptions}
      />
      <Field
        label={t('admin.devices.dispenseHeader')}
        value={header}
        placeholder={terminal.effectiveDispenseHeader}
        maxLength={40}
        onChange={(e) => setHeader(e.target.value)}
        hint={t('admin.devices.dispenseHeaderHint')}
      />
      <SelectField
        label={t('admin.devices.receiptPrinter')}
        value={receiptId}
        onChange={(e) => setReceiptId(e.target.value)}
        options={printerOptions}
      />
      <Toggle label={t('admin.devices.active')} checked={active} onChange={setActive} />
      <section className="devices-panel__routing" aria-label={t('admin.devices.routing')}>
        <strong>{t('admin.devices.routing')}</strong>
        <p data-testid="routing-preview">{preview}</p>
      </section>
      {error && (
        <p className="devices-panel__error" role="alert">
          {error}
        </p>
      )}
      <Button type="submit" variant="primary" size="lg" block disabled={busy}>
        {t('common.save')}
      </Button>
      {terminal.paired && (
        <Button variant="danger" block onClick={() => setRevoking(true)}>
          {t('admin.devices.revoke')}
        </Button>
      )}
      {revoking && (
        <Modal
          title={t('admin.devices.revokeTitle', { name: terminal.name })}
          onClose={() => setRevoking(false)}
        >
          <div className="devices-confirm">
            <p>{t('admin.devices.revokeText', { name: terminal.name })}</p>
            <Button variant="danger" size="lg" block onClick={() => void revoke()}>
              {t('admin.devices.revoke')}
            </Button>
            <Button block onClick={() => setRevoking(false)}>
              {t('common.cancel')}
            </Button>
          </div>
        </Modal>
      )}
    </form>
  );
}
