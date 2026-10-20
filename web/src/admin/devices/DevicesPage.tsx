import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { api } from '../../shared/api/client';
import { formatDayTime } from '../../shared/format/dates';
import { t } from '../../shared/i18n/i18n';
import { Banner, Button, Table, useToast, type Column } from '../../shared/ui';
import { JobsPanel } from './JobsPanel';
import { PairPanel } from './PairPanel';
import { PrinterPanel } from './PrinterPanel';
import { TerminalPanel } from './TerminalPanel';
import { ago, type PrinterRow, type TerminalRow } from './types';
import './devices.css';

type Panel =
  | { kind: 'terminal'; terminal: TerminalRow }
  | { kind: 'pair' }
  | { kind: 'printer'; printer?: PrinterRow }
  | { kind: 'jobs'; printer: PrinterRow };

interface PriceLists {
  priceLists: { id: string; name: string; active: boolean }[];
}

/** Wireframe 17: terminals and printers, with a right-side panel opened when needed. */
export function DevicesPage() {
  const queries = useQueryClient();
  const toast = useToast();
  const terminals = useQuery({
    queryKey: ['terminals'],
    queryFn: () => api<TerminalRow[]>('/api/admin/terminals'),
  });
  const printers = useQuery({
    queryKey: ['printers'],
    queryFn: () => api<PrinterRow[]>('/api/admin/printers'),
  });
  const lists = useQuery({
    queryKey: ['price-lists'],
    queryFn: async () =>
      (await api<PriceLists>('/api/admin/menu').catch(() => ({ priceLists: [] }))).priceLists,
  });
  const [panel, setPanel] = useState<Panel | null>(null);
  const [stale, setStale] = useState(false);

  const refresh = () => {
    void queries.invalidateQueries({ queryKey: ['terminals'] });
    void queries.invalidateQueries({ queryKey: ['printers'] });
  };
  const done = () => {
    setPanel(null);
    setStale(false);
    refresh();
  };

  const testPrint = async (p: PrinterRow) => {
    try {
      await api(`/api/admin/printers/${p.id}/test`, { method: 'POST' });
      toast(t('admin.devices.testQueued', { printer: p.name }));
    } catch {
      toast(t('common.somethingWrong'), { tone: 'warning' });
    }
  };

  const status = (x: TerminalRow) => {
    const label =
      x.status === 'Online'
        ? t('admin.devices.online')
        : x.status === 'Revoked'
          ? t('admin.devices.revoked')
          : x.status === 'NotPaired'
            ? t('admin.devices.notPaired')
            : x.lastSeenAt
              ? t('admin.devices.offlineSince', { ago: ago(x.lastSeenAt) })
              : t('admin.devices.offline');
    return (
      <span className={`devices-status devices-status--${x.status.toLowerCase()}`}>
        <span className="devices-status__dot" aria-hidden="true" />
        {label}
      </span>
    );
  };

  const terminalColumns: Column<TerminalRow>[] = [
    {
      key: 'name',
      header: t('admin.devices.colTerminal'),
      render: (x) => <strong>{x.name}</strong>,
    },
    { key: 'status', header: t('admin.devices.colStatus'), render: status },
    { key: 'user', header: t('admin.devices.colUser'), render: (x) => x.currentUser ?? '—' },
    {
      key: 'app',
      header: t('admin.devices.colApp'),
      mono: true,
      render: (x) => x.appVersion ?? '—',
    },
    {
      key: 'list',
      header: t('admin.devices.colPriceList'),
      render: (x) => x.priceListName ?? t('admin.devices.defaultList'),
    },
    {
      key: 'dispense',
      header: t('admin.devices.colDispense'),
      render: (x) => x.dispensePrinterName ?? '—',
    },
    {
      key: 'receipt',
      header: t('admin.devices.colReceipt'),
      render: (x) => x.receiptPrinterName ?? '—',
    },
    {
      key: 'edit',
      header: '',
      render: (x) => (
        <Button
          variant="link"
          aria-label={`${t('common.edit')} ${x.name}`}
          onClick={() => setPanel({ kind: 'terminal', terminal: x })}
        >
          {t('common.edit')}
        </Button>
      ),
    },
  ];

  const printerStatus = (p: PrinterRow) =>
    p.status === 'Error' ? (
      <button
        type="button"
        className="devices-printer-error"
        title={p.lastError ?? ''}
        onClick={() => toast(p.lastError ?? '', { tone: 'warning' })}
      >
        {t('admin.devices.error')}
      </button>
    ) : p.status === 'Online' ? (
      t('admin.devices.ready')
    ) : (
      t('admin.devices.unknown')
    );

  const printerColumns: Column<PrinterRow>[] = [
    {
      key: 'name',
      header: t('admin.devices.colPrinter'),
      render: (p) => <strong>{p.name}</strong>,
    },
    {
      key: 'connection',
      header: t('admin.devices.colConnection'),
      render: (p) => p.connectionText,
    },
    {
      key: 'used',
      header: t('admin.devices.colUsedBy'),
      render: (p) =>
        p.usedBy.length ? (
          <ul className="devices-used">
            {p.usedBy.map((u) => (
              <li key={u.terminal}>
                {u.terminal}: {u.roles.join(', ')}
              </li>
            ))}
          </ul>
        ) : (
          '—'
        ),
    },
    {
      key: 'paper',
      header: t('admin.devices.colPaper'),
      mono: true,
      render: (p) => `${p.paperWidthMm} mm`,
    },
    { key: 'status', header: t('admin.devices.colStatus'), render: printerStatus },
    {
      key: 'last',
      header: t('admin.devices.colLastPrinted'),
      render: (p) => (p.lastPrintedAt ? formatDayTime(p.lastPrintedAt) : t('admin.devices.never')),
    },
    {
      key: 'actions',
      header: '',
      render: (p) => (
        <div className="devices-actions">
          <Button disabled={!p.active} onClick={() => void testPrint(p)}>
            {t('admin.devices.testPrint')}
          </Button>
          <Button
            variant="link"
            aria-label={`${t('common.edit')} ${p.name}`}
            onClick={() => setPanel({ kind: 'printer', printer: p })}
          >
            {t('common.edit')}
          </Button>
          {p.failedJobs > 0 && (
            <Button variant="link" onClick={() => setPanel({ kind: 'jobs', printer: p })}>
              {t('admin.devices.failedJobs', { count: p.failedJobs })}
            </Button>
          )}
        </div>
      ),
    },
  ];

  const title = !panel
    ? ''
    : panel.kind === 'terminal'
      ? t('admin.devices.editTerminal', { name: panel.terminal.name })
      : panel.kind === 'pair'
        ? t('admin.devices.pairTitle')
        : panel.kind === 'jobs'
          ? t('admin.devices.jobsTitle', { name: panel.printer.name })
          : panel.printer
            ? t('admin.devices.editPrinter', { name: panel.printer.name })
            : t('admin.devices.newPrinter');

  return (
    <main className={`devices${panel ? ' devices--panel' : ''}`}>
      <div className="devices__main">
        <div className="devices__header">
          <h1 className="admin__title">{t('admin.devices.title')}</h1>
          <div className="devices__buttons">
            <Button onClick={() => setPanel({ kind: 'pair' })}>{t('admin.devices.pair')}</Button>
            <Button variant="primary" onClick={() => setPanel({ kind: 'printer' })}>
              {t('admin.devices.addPrinter')}
            </Button>
          </div>
        </div>
        {stale && (
          <Banner action={{ label: t('common.reload'), onClick: done }}>
            {t('admin.devices.stale')}
          </Banner>
        )}
        <h2 className="devices__section">{t('admin.devices.terminals')}</h2>
        <Table
          caption={t('admin.devices.terminals')}
          columns={terminalColumns}
          rows={terminals.data ?? []}
          rowKey={(x) => x.id}
        />
        <h2 className="devices__section">{t('admin.devices.printers')}</h2>
        <Table
          caption={t('admin.devices.printers')}
          columns={printerColumns}
          rows={printers.data ?? []}
          rowKey={(p) => p.id}
          rowClassName={(p) => (p.active ? undefined : 'devices-row--inactive')}
        />
      </div>
      {panel && (
        <aside className="devices-panel" aria-label={title}>
          <div className="devices-panel__head">
            <h2 className="devices-panel__title">{title}</h2>
            <Button variant="link" onClick={() => setPanel(null)}>
              {t('common.close')}
            </Button>
          </div>
          {panel.kind === 'terminal' && (
            <TerminalPanel
              key={panel.terminal.id + panel.terminal.rowVersion}
              terminal={panel.terminal}
              printers={printers.data ?? []}
              priceLists={lists.data ?? []}
              onSaved={done}
              onStale={() => setStale(true)}
            />
          )}
          {panel.kind === 'pair' && (
            <PairPanel terminals={terminals.data ?? []} onPaired={refresh} />
          )}
          {panel.kind === 'printer' && (
            <PrinterPanel
              key={panel.printer?.id ?? 'new'}
              printer={panel.printer}
              terminals={terminals.data ?? []}
              onSaved={done}
              onStale={() => setStale(true)}
            />
          )}
          {panel.kind === 'jobs' && (
            <JobsPanel printer={panel.printer} printers={printers.data ?? []} />
          )}
        </aside>
      )}
    </main>
  );
}
