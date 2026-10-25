import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useEffect, useRef, useState } from 'react';
import { Link, useSearchParams } from 'react-router';
import { ApiError, api, apiUrl } from '../../shared/api/client';
import { formatAmount, formatSigned } from '../../shared/format/money';
import { t } from '../../shared/i18n/i18n';
import { Button, Modal, SelectField, Table, useToast, type Column } from '../../shared/ui';
import { AwaitingApproval } from '../approvals/AwaitingApproval';
import { CategoriesDialog } from './CategoriesDialog';
import { addDays, formatBusinessDate, formatTime, todayIso } from './dates';
import { EntryDialog } from './EntryDialog';
import {
  ExpenseDialog,
  MoveMoneyDialog,
  OpeningBalancesDialog,
  OtherIncomeDialog,
  OwnerMoneyDialog,
  PaySupplierDialog,
  type Balances,
} from './MoneyForms';
import type { CashBook, CashBookRow, DailySummary, MoneyDocument } from './types';
import '../stock/stock.css';
import './money.css';

type Action =
  | 'banking'
  | 'move'
  | 'supplier'
  | 'expense'
  | 'owner'
  | 'income'
  | 'opening'
  | 'categories'
  | 'print';

const moreActions: Action[] = ['move', 'owner', 'income', 'opening', 'categories'];

/** Cash book · date (wireframe 14): tiles, the day's entries in plain words, and the daily summary. */
export function CashBookPage() {
  const [params, setParams] = useSearchParams();
  const date = params.get('date') ?? '';
  const all = params.get('show') === 'all';
  const queries = useQueryClient();
  const toast = useToast();
  const book = useQuery({
    queryKey: ['cashbook', 'day', date, all],
    queryFn: () =>
      api<CashBook>(
        `/api/admin/cashbook?${new URLSearchParams({ ...(date && { date }), all: String(all) })}`,
      ),
  });
  const shown = book.data?.date ?? date;
  const [action, setAction] = useState<Action | null>(null);
  const [entry, setEntry] = useState<string | null>(null);

  const go = (next: { date?: string; all?: boolean }) => {
    const p = new URLSearchParams();
    const d = next.date ?? shown;
    if (d) p.set('date', d);
    if (next.all ?? all) p.set('show', 'all');
    setParams(p);
  };

  const refresh = () =>
    Promise.all(
      ['cashbook', 'accounts', 'awaiting', 'stock'].map((key) =>
        queries.invalidateQueries({ queryKey: [key] }),
      ),
    );

  const done = (document: MoneyDocument | null) => {
    setAction(null);
    if (!document) return;
    void refresh();
    toast(
      document.status === 'Pending' || document.reversalStatus === 'Pending'
        ? t('admin.cashbook.savedPending')
        : t('admin.cashbook.saved', { title: document.title }),
    );
    if (document.warning) toast(document.warning, { tone: 'warning' });
  };

  if (book.error) {
    const denied = book.error instanceof ApiError && book.error.status === 403;
    return (
      <main className="stock stock--single">
        <h1 className="admin__title">{t('admin.cashbook.title')}</h1>
        <p role="alert">{denied ? t('admin.cashbook.noAccess') : t('common.somethingWrong')}</p>
      </main>
    );
  }

  const data = book.data;
  const tiles = data?.tiles;
  const balances: Balances = {
    Safe: (tiles?.cashOnHand ?? 0) - (tiles?.inOpenDrawers ?? 0),
    Bank: tiles?.bank ?? 0,
    Mpesa: tiles?.mpesaBalance ?? 0,
  };

  const columns: Column<CashBookRow>[] = [
    { key: 'time', header: t('admin.cashbook.time'), mono: true, render: (r) => formatTime(r.at) },
    {
      key: 'entry',
      header: t('admin.cashbook.entry'),
      render: (r) => (
        <>
          <button type="button" className="stock-link" onClick={() => setEntry(r.entryId)}>
            {r.text}
          </button>
          {r.late && <span className="money-tag">{t('admin.cashbook.late')}</span>}
          {r.reversed && <span className="money-tag">{t('admin.cashbook.reversed')}</span>}
        </>
      ),
    },
    { key: 'account', header: t('admin.cashbook.account.header'), render: (r) => r.account },
    {
      key: 'in',
      header: t('admin.cashbook.in'),
      align: 'right',
      mono: true,
      render: (r) => (r.in ? formatAmount(r.in) : ''),
    },
    {
      key: 'out',
      header: t('admin.cashbook.out'),
      align: 'right',
      mono: true,
      render: (r) => (r.out ? formatAmount(r.out) : ''),
    },
  ];

  return (
    <main className="money-page">
      <div className="stock__main">
        <div className="stock__header">
          <h1 className="admin__title">
            {t('admin.cashbook.titleOn', { date: shown ? formatBusinessDate(shown) : '' })}
          </h1>
          {data?.canRecord && (
            <div className="stock__buttons">
              <Button onClick={() => setAction('banking')}>
                {t('admin.cashbook.recordBanking')}
              </Button>
              <Button onClick={() => setAction('supplier')}>
                {t('admin.cashbook.paySupplier')}
              </Button>
              <Button variant="primary" onClick={() => setAction('expense')}>
                {t('admin.cashbook.addExpense')}
              </Button>
              <MoreMenu onPick={setAction} />
            </div>
          )}
        </div>
        <div className="money-dates">
          <Button
            aria-label={t('admin.cashbook.previousDay')}
            disabled={!shown}
            onClick={() => go({ date: addDays(shown, -1) })}
          >
            ‹
          </Button>
          <label className="money-dates__picker">
            <span className="visually-hidden">{t('admin.cashbook.pickDate')}</span>
            <input
              type="date"
              className="nx-field__input"
              value={shown}
              max={todayIso()}
              onChange={(e) => e.target.value && go({ date: e.target.value })}
            />
          </label>
          <Button
            aria-label={t('admin.cashbook.nextDay')}
            disabled={!shown || shown >= todayIso()}
            onClick={() => go({ date: addDays(shown, 1) })}
          >
            ›
          </Button>
          <Link className="nx-btn nx-btn--link nx-btn--md" to="/admin/accounts">
            {t('admin.cashbook.accounts')}
          </Link>
        </div>
        {tiles && (
          <div className="money-tiles">
            <Tile label={t('admin.cashbook.tiles.cashOnHand')} amount={tiles.cashOnHand}>
              {tiles.inOpenDrawers !== 0 &&
                t('admin.cashbook.tiles.inDrawers', { amount: formatAmount(tiles.inOpenDrawers) })}
            </Tile>
            <Tile label={t('admin.cashbook.tiles.mpesaReceived')} amount={tiles.mpesaReceived}>
              {t('admin.cashbook.tiles.mpesaBalance', { amount: formatSigned(tiles.mpesaBalance) })}
            </Tile>
            <Tile label={t('admin.cashbook.tiles.bank')} amount={tiles.bank} />
            <Tile label={t('admin.cashbook.tiles.owed')} amount={tiles.owedToSuppliers} />
          </div>
        )}
        <AwaitingApproval only="money" />
      </div>
      <div className="stock">
        <div className="stock__main">
          <div className="stock__tabs" role="group" aria-label={t('admin.cashbook.filter')}>
            {[false, true].map((showAll) => (
              <button
                key={String(showAll)}
                type="button"
                className={`stock-tab${all === showAll ? ' stock-tab--on' : ''}`}
                aria-pressed={all === showAll}
                onClick={() => go({ all: showAll })}
              >
                {showAll ? t('admin.cashbook.allEntries') : t('admin.cashbook.moneyOnly')}
              </button>
            ))}
          </div>
          {data && (
            <Table
              caption={t('admin.cashbook.entries')}
              columns={columns}
              rows={data.rows}
              rowKey={(r) => `${r.entryId}-${r.text}`}
              rowClassName={(r) => (r.reversed || r.reversal ? 'money-row--reversed' : undefined)}
              empty={t('admin.cashbook.noEntries')}
            />
          )}
        </div>
        <aside className="money-summary-box">
          {data && <Summary summary={data.summary} />}
          {data && (
            <div className="money-summary__actions">
              <a
                className="nx-btn nx-btn--secondary nx-btn--md"
                href={apiUrl(`/api/admin/cashbook/summary.pdf?date=${data.date}`)}
                target="_blank"
                rel="noreferrer"
              >
                {t('admin.cashbook.printSummary')}
              </a>
              <Button onClick={() => setAction('print')}>{t('admin.cashbook.printReceipt')}</Button>
              <a
                className="nx-btn nx-btn--secondary nx-btn--md"
                href={apiUrl(`/api/admin/cashbook/export.csv?date=${data.date}&all=${all}`)}
                download
              >
                {t('admin.cashbook.exportCsv')}
              </a>
            </div>
          )}
        </aside>
      </div>
      {action === 'expense' && <ExpenseDialog onDone={done} />}
      {action === 'supplier' && <PaySupplierDialog onDone={done} />}
      {(action === 'banking' || action === 'move') && (
        <MoveMoneyDialog
          title={
            action === 'banking' ? t('admin.cashbook.recordBanking') : t('admin.cashbook.moveMoney')
          }
          balances={balances}
          onDone={done}
        />
      )}
      {action === 'owner' && <OwnerMoneyDialog onDone={done} />}
      {action === 'income' && <OtherIncomeDialog onDone={done} />}
      {action === 'opening' && <OpeningBalancesDialog onDone={done} />}
      {action === 'categories' && <CategoriesDialog onClose={() => setAction(null)} />}
      {action === 'print' && data && (
        <PrintDialog date={data.date} onClose={() => setAction(null)} />
      )}
      {entry && (
        <EntryDialog
          key={entry}
          entryId={entry}
          onOpen={setEntry}
          onClose={(reversed) => {
            setEntry(null);
            done(reversed);
          }}
        />
      )}
    </main>
  );
}

function Tile({
  label,
  amount,
  children,
}: {
  label: string;
  amount: number;
  children?: string | false;
}) {
  return (
    <section className="money-tile" aria-label={label}>
      <span className="money-tile__label">{label}</span>
      <span className="money-tile__amount">{formatSigned(amount)}</span>
      {children && <span className="money-tile__note">{children}</span>}
    </section>
  );
}

/** The More menu: a real button that opens a list of buttons (no hover). */
function MoreMenu({ onPick }: { onPick: (action: Action) => void }) {
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
  return (
    <div className="money-more" ref={box}>
      <Button aria-expanded={open} aria-haspopup="true" onClick={() => setOpen((o) => !o)}>
        {t('admin.cashbook.more')}
      </Button>
      {open && (
        <ul className="money-more__list">
          {moreActions.map((a) => (
            <li key={a}>
              <button
                type="button"
                className="money-more__item"
                onClick={() => {
                  setOpen(false);
                  onPick(a);
                }}
              >
                {t(`admin.cashbook.actions.${a}`)}
              </button>
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}

function SummaryLine({
  label,
  amount,
  strong,
}: {
  label: string;
  amount: number;
  strong?: boolean;
}) {
  return (
    <div className={`money-line${strong ? ' money-line--total' : ''}`}>
      <span>{label}</span>
      <span className="mono">{formatSigned(amount)}</span>
    </div>
  );
}

/** A line that opens its parts underneath (expenses by category; stock loss and cash parts). */
function SplitLine({
  label,
  amount,
  parts,
}: {
  label: string;
  amount: number;
  parts: DailySummary['expensesByCategory'];
}) {
  const [open, setOpen] = useState(false);
  if (!parts.length) return <SummaryLine label={label} amount={amount} />;
  return (
    <>
      <button
        type="button"
        className="money-line money-line--button"
        aria-expanded={open}
        onClick={() => setOpen((o) => !o)}
      >
        <span>
          {label} <span aria-hidden="true">{open ? '▴' : '▾'}</span>
        </span>
        <span className="mono">{formatSigned(amount)}</span>
      </button>
      {open && (
        <ul className="money-parts">
          {parts.map((p) => (
            <li key={p.name} className="money-line money-line--part">
              <span>{p.name}</span>
              <span className="mono">{formatSigned(p.amount)}</span>
            </li>
          ))}
        </ul>
      )}
    </>
  );
}

function Summary({ summary: s }: { summary: DailySummary }) {
  return (
    <section className="money-summary" aria-labelledby="money-summary">
      <h2 id="money-summary" className="stock-section-title">
        {t('admin.cashbook.summary.title')}
      </h2>
      <SummaryLine label={t('admin.cashbook.summary.sales')} amount={s.salesInclVat} />
      <SummaryLine label={t('admin.cashbook.summary.vat')} amount={s.vat} />
      {s.rounding !== 0 && (
        <SummaryLine label={t('admin.cashbook.summary.rounding')} amount={s.rounding} />
      )}
      <SummaryLine label={t('admin.cashbook.summary.netSales')} amount={s.netSales} />
      <SummaryLine label={t('admin.cashbook.summary.costOfSales')} amount={s.costOfSales} />
      <div className="money-line money-line--total">
        <span>
          {t('admin.cashbook.summary.grossProfit')}
          {s.grossMarginPercent != null && (
            <span className="money-muted"> · {formatAmount(s.grossMarginPercent)}%</span>
          )}
        </span>
        <span className="mono">{formatSigned(s.grossProfit)}</span>
      </div>
      <SplitLine
        label={t('admin.cashbook.summary.expenses')}
        amount={s.expenses}
        parts={s.expensesByCategory}
      />
      <SplitLine
        label={t('admin.cashbook.summary.stockLoss')}
        amount={s.stockLossAndCash}
        parts={s.stockLossParts}
      />
      <SummaryLine label={t('admin.cashbook.summary.bankCharges')} amount={s.bankCharges} />
      <SummaryLine label={t('admin.cashbook.summary.otherIncome')} amount={s.otherIncome} />
      <SummaryLine label={t('admin.cashbook.summary.net')} amount={s.net} strong />
      <p className="stock-note">{t('admin.cashbook.summary.note')}</p>
      {s.noRecipeItems.length > 0 && (
        <p className="money-warning" role="status">
          {s.noRecipeItems.length === 1
            ? t('admin.cashbook.summary.noRecipeOne')
            : t('admin.cashbook.summary.noRecipe', { count: s.noRecipeItems.length })}{' '}
          <Link to="/admin/stock">{t('admin.cashbook.summary.noRecipeLink')}</Link>
          <span className="money-muted"> ({s.noRecipeItems.join(', ')})</span>
        </p>
      )}
    </section>
  );
}

/** The summary on a receipt printer (80 or 58 mm). */
function PrintDialog({ date, onClose }: { date: string; onClose: () => void }) {
  const toast = useToast();
  const printers = useQuery({
    queryKey: ['printers'],
    queryFn: () => api<{ id: string; name: string; active: boolean }[]>('/api/admin/printers'),
  });
  const active = (printers.data ?? []).filter((p) => p.active);
  const [printerId, setPrinterId] = useState('');
  const [error, setError] = useState<string | null>(null);
  const chosen = printerId || active[0]?.id || '';
  const print = async () => {
    setError(null);
    try {
      await api(`/api/admin/cashbook/summary/print?date=${date}`, {
        method: 'POST',
        body: { printerId: chosen },
      });
      toast(t('admin.cashbook.printing'));
      onClose();
    } catch (e) {
      setError(e instanceof ApiError ? e.message : t('common.somethingWrong'));
    }
  };
  return (
    <Modal title={t('admin.cashbook.printReceipt')} onClose={onClose} width={460}>
      <SelectField
        label={t('admin.cashbook.printer')}
        value={chosen}
        onChange={(e) => setPrinterId(e.target.value)}
        options={active.map((p) => ({ value: p.id, label: p.name }))}
      />
      {error && (
        <p className="nx-field__error" role="alert">
          {error}
        </p>
      )}
      <div className="stock-dialog__buttons">
        <Button onClick={onClose}>{t('common.cancel')}</Button>
        <Button variant="primary" disabled={!chosen} onClick={() => void print()}>
          {t('admin.cashbook.print')}
        </Button>
      </div>
    </Modal>
  );
}
