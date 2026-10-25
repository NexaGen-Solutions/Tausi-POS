import { useQuery } from '@tanstack/react-query';
import { useEffect, useRef, useState } from 'react';
import { useSearchParams } from 'react-router';
import { ApiError, api, apiFile, type ApiFile } from '../../shared/api/client';
import { t } from '../../shared/i18n/i18n';
import { Button, Modal, SelectField, useToast } from '../../shared/ui';
import { ReportFilterBar } from './ReportFilterBar';
import { TabCount } from './format';
import { ReportView } from './ReportView';
import type { ReportDefinition, ReportFilters, ReportOptions, ReportResult } from './types';
import './reports.css';

/** Opens a PDF in a new tab, or saves a file. */
function deliver(file: ApiFile, open: boolean) {
  if (typeof URL.createObjectURL !== 'function') return;
  const url = URL.createObjectURL(file.blob);
  if (open) window.open(url, '_blank', 'noopener');
  else {
    const link = document.createElement('a');
    link.href = url;
    link.download = file.fileName;
    link.click();
  }
  setTimeout(() => URL.revokeObjectURL(url), 60_000);
}

/** Only the filters that are set, so the server applies its own defaults to the rest. */
function cleaned(filters: ReportFilters): ReportFilters {
  return Object.fromEntries(
    Object.entries(filters).filter(([, v]) => v !== undefined && v !== ''),
  ) as ReportFilters;
}

/** Reports (wireframe 19): the tab row, the report's filters, Print and Export to Excel, and the grouped table. */
export function ReportsPage() {
  const [params, setParams] = useSearchParams();
  const toast = useToast();
  const list = useQuery({
    queryKey: ['reports', 'list'],
    queryFn: () => api<ReportDefinition[]>('/api/admin/reports'),
  });
  const options = useQuery({
    queryKey: ['reports', 'options'],
    queryFn: () => api<ReportOptions>('/api/admin/reports/options'),
    enabled: Array.isArray(list.data) && list.data.length > 0,
  });
  const reports = Array.isArray(list.data) ? list.data : [];
  const key = params.get('report') ?? reports[0]?.key;
  const definition = reports.find((r) => r.key === key) ?? reports[0];
  const [filters, setFilters] = useState<ReportFilters>({});
  const sent = cleaned(filters);
  const result = useQuery({
    queryKey: ['reports', 'run', definition?.key, sent],
    queryFn: () =>
      api<ReportResult>(`/api/admin/reports/${definition!.key}/run`, {
        method: 'POST',
        body: sent,
        readOnly: true,
      }),
    enabled: !!definition,
    placeholderData: (previous) => (previous?.key === definition?.key ? previous : undefined),
  });
  const [busy, setBusy] = useState<string | null>(null);
  const [printing, setPrinting] = useState(false);

  const choose = (next: string) => {
    // The period carries over; the other filters belong to the report.
    setFilters((f) => ({ workPeriodId: f.workPeriodId, from: f.from, to: f.to, quick: f.quick }));
    setParams({ report: next });
  };

  const file = async (what: 'xlsx' | 'csv' | 'pdf') => {
    if (!definition) return;
    setBusy(what);
    try {
      const path =
        what === 'pdf'
          ? `/api/admin/reports/${definition.key}/print?target=pdf`
          : `/api/admin/reports/${definition.key}/export?format=${what}`;
      deliver(await apiFile(path, { method: 'POST', body: sent, readOnly: true }), what === 'pdf');
    } catch (e) {
      toast(e instanceof ApiError ? e.message : t('common.somethingWrong'), { tone: 'warning' });
    } finally {
      setBusy(null);
    }
  };

  if (list.isSuccess && reports.length === 0)
    return (
      <main className="reports">
        <h1 className="admin__title">{t('admin.reports.title')}</h1>
        <p role="alert">{t('admin.reports.noAccess')}</p>
      </main>
    );

  const tabs = reports.slice(0, TabCount);
  const more = reports.slice(TabCount);
  return (
    <main className="reports">
      <div className="reports__header">
        <h1 className="admin__title">{t('admin.reports.title')}</h1>
        <div className="reports__buttons">
          <PrintMenu
            definition={definition}
            busy={busy !== null}
            onPdf={() => void file('pdf')}
            onCsv={() => void file('csv')}
            onReceipt={() => setPrinting(true)}
          />
          <Button
            variant="primary"
            disabled={!definition || busy !== null}
            onClick={() => void file('xlsx')}
          >
            {t('admin.reports.exportExcel')}
          </Button>
        </div>
      </div>
      <nav className="reports__tabs" aria-label={t('admin.reports.title')}>
        {tabs.map((r) => (
          <button
            key={r.key}
            type="button"
            className={`report-tab${r.key === definition?.key ? ' report-tab--on' : ''}`}
            aria-pressed={r.key === definition?.key}
            onClick={() => choose(r.key)}
          >
            {r.title}
          </button>
        ))}
        {more.length > 0 && (
          <MoreReports reports={more} current={definition?.key} onPick={choose} />
        )}
      </nav>
      {definition && (
        <ReportFilterBar
          key={definition.key}
          definition={definition}
          options={options.data}
          filters={filters}
          onChange={setFilters}
        />
      )}
      {result.data && (
        <p className="report-period">
          {[result.data.period, ...result.data.filterText].filter(Boolean).join(' · ')}
        </p>
      )}
      {result.error && (
        <p className="nx-field__error" role="alert">
          {result.error instanceof ApiError ? result.error.message : t('common.somethingWrong')}
        </p>
      )}
      {result.isLoading && <p className="report-note">{t('common.loading')}</p>}
      {result.data && <ReportView result={result.data} />}
      {printing && definition && (
        <ReceiptDialog
          definition={definition}
          filters={sent}
          printers={options.data?.printers ?? []}
          onClose={() => setPrinting(false)}
        />
      )}
    </main>
  );
}

/** The More menu: a real button that opens a list of buttons (no hover). */
function MoreReports({
  reports,
  current,
  onPick,
}: {
  reports: ReportDefinition[];
  current?: string;
  onPick: (key: string) => void;
}) {
  const [open, setOpen] = useState(false);
  const box = useRef<HTMLDivElement>(null);
  useEffect(() => {
    if (!open) return;
    const close = (event: MouseEvent | KeyboardEvent) => {
      if (
        event instanceof KeyboardEvent
          ? event.key === 'Escape'
          : !box.current?.contains(event.target as Node)
      )
        setOpen(false);
    };
    window.addEventListener('mousedown', close);
    window.addEventListener('keydown', close);
    return () => {
      window.removeEventListener('mousedown', close);
      window.removeEventListener('keydown', close);
    };
  }, [open]);
  const active = reports.find((r) => r.key === current);
  return (
    <div className="report-more-menu" ref={box}>
      <button
        type="button"
        className={`report-tab${active ? ' report-tab--on' : ''}`}
        aria-expanded={open}
        aria-haspopup="true"
        onClick={() => setOpen((o) => !o)}
      >
        {active
          ? t('admin.reports.moreActive', { title: active.title })
          : t('admin.reports.moreReports')}
      </button>
      {open && (
        <ul className="report-more-menu__list">
          {reports.map((r) => (
            <li key={r.key}>
              <button
                type="button"
                className="report-more-menu__item"
                aria-current={r.key === current ? 'true' : undefined}
                onClick={() => {
                  setOpen(false);
                  onPick(r.key);
                }}
              >
                {r.title}
              </button>
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}

/** Print: the A4 PDF, a CSV file, and the receipt printer for the short reports. */
function PrintMenu({
  definition,
  busy,
  onPdf,
  onCsv,
  onReceipt,
}: {
  definition?: ReportDefinition;
  busy: boolean;
  onPdf: () => void;
  onCsv: () => void;
  onReceipt: () => void;
}) {
  const [open, setOpen] = useState(false);
  const pick = (action: () => void) => {
    setOpen(false);
    action();
  };
  return (
    <div className="report-more-menu">
      <Button
        aria-expanded={open}
        aria-haspopup="true"
        disabled={!definition || busy}
        onClick={() => setOpen((o) => !o)}
      >
        {t('admin.reports.print')}
      </Button>
      {open && (
        <ul className="report-more-menu__list">
          <li>
            <button type="button" className="report-more-menu__item" onClick={() => pick(onPdf)}>
              {t('admin.reports.printPdf')}
            </button>
          </li>
          {definition?.receipt && (
            <li>
              <button
                type="button"
                className="report-more-menu__item"
                onClick={() => pick(onReceipt)}
              >
                {t('admin.reports.printReceipt')}
              </button>
            </li>
          )}
          <li>
            <button type="button" className="report-more-menu__item" onClick={() => pick(onCsv)}>
              {t('admin.reports.exportCsv')}
            </button>
          </li>
        </ul>
      )}
    </div>
  );
}

function ReceiptDialog({
  definition,
  filters,
  printers,
  onClose,
}: {
  definition: ReportDefinition;
  filters: ReportFilters;
  printers: ReportOptions['printers'];
  onClose: () => void;
}) {
  const toast = useToast();
  const [printerId, setPrinterId] = useState(printers[0]?.id ?? '');
  const [error, setError] = useState<string | null>(null);
  const print = async () => {
    setError(null);
    try {
      await api(
        `/api/admin/reports/${definition.key}/print?target=printer&printerId=${printerId}`,
        { method: 'POST', body: filters },
      );
      toast(t('admin.reports.printing', { title: definition.title }));
      onClose();
    } catch (e) {
      setError(e instanceof ApiError ? e.message : t('common.somethingWrong'));
    }
  };
  return (
    <Modal title={t('admin.reports.printReceipt')} onClose={onClose} width={460}>
      <SelectField
        label={t('admin.reports.printer')}
        value={printerId}
        onChange={(e) => setPrinterId(e.target.value)}
        options={printers.map((p) => ({ value: p.id, label: p.name }))}
      />
      {error && (
        <p className="nx-field__error" role="alert">
          {error}
        </p>
      )}
      <div className="reports__dialog-buttons">
        <Button onClick={onClose}>{t('common.cancel')}</Button>
        <Button variant="primary" disabled={!printerId} onClick={() => void print()}>
          {t('admin.reports.print')}
        </Button>
      </div>
    </Modal>
  );
}
