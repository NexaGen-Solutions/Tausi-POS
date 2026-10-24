import { useQuery } from '@tanstack/react-query';
import { useState } from 'react';
import { Link, useSearchParams } from 'react-router';
import { ApiError, api } from '../../shared/api/client';
import { formatAmount, formatSigned } from '../../shared/format/money';
import { t } from '../../shared/i18n/i18n';
import { Field, Table, type Column } from '../../shared/ui';
import { addDays, formatBusinessDate, todayIso } from './dates';
import type { AccountBalance, AccountLedger, Accounts, LedgerLine } from './types';
import '../stock/stock.css';
import './money.css';

/**
 * Accounts (read-only, cash.book): every account's balance as at a date, grouped the way people
 * read them, the trial balance check, and an account's ledger with its running balance.
 */
export function AccountsPage() {
  const [params, setParams] = useSearchParams();
  const asAt = params.get('date') ?? '';
  const code = params.get('account');
  const accounts = useQuery({
    queryKey: ['accounts', asAt],
    queryFn: () => api<Accounts>(`/api/admin/accounts${asAt ? `?date=${asAt}` : ''}`),
  });
  const shown = accounts.data?.asAt ?? asAt;

  const set = (changes: Record<string, string | null>) => {
    const p = new URLSearchParams(params);
    for (const [k, v] of Object.entries(changes)) {
      if (v) p.set(k, v);
      else p.delete(k);
    }
    setParams(p);
  };

  if (accounts.error) {
    const denied = accounts.error instanceof ApiError && accounts.error.status === 403;
    return (
      <main className="stock stock--single">
        <h1 className="admin__title">{t('admin.accounts.title')}</h1>
        <p role="alert">{denied ? t('admin.accounts.noAccess') : t('common.somethingWrong')}</p>
      </main>
    );
  }

  const trial = accounts.data?.trial;
  const columns: Column<AccountBalance>[] = [
    {
      key: 'name',
      header: t('admin.accounts.account'),
      render: (a) => (
        <button
          type="button"
          className="stock-link"
          aria-pressed={a.code === code}
          onClick={() => set({ account: a.code })}
        >
          {a.name}
        </button>
      ),
    },
    { key: 'code', header: t('admin.accounts.code'), mono: true, render: (a) => a.code },
    {
      key: 'balance',
      header: t('admin.accounts.balance'),
      align: 'right',
      mono: true,
      render: (a) => formatSigned(a.balance),
    },
  ];

  return (
    <main className={`stock${code ? ' money-accounts' : ' stock--single'}`}>
      <div className="stock__main">
        <div className="stock__header">
          <h1 className="admin__title">{t('admin.accounts.title')}</h1>
          <Link className="nx-btn nx-btn--secondary nx-btn--md" to="/admin/cash-book">
            {t('admin.accounts.backToCashBook')}
          </Link>
        </div>
        <div className="stock-filters">
          <Field
            label={t('admin.accounts.asAt')}
            type="date"
            value={shown}
            max={todayIso()}
            onChange={(e) => e.target.value && set({ date: e.target.value })}
          />
        </div>
        {trial &&
          (trial.difference === 0 ? (
            <p className="money-trial money-trial--ok" role="status">
              {t('admin.accounts.balanced')}
            </p>
          ) : (
            <p className="money-trial money-trial--off" role="alert">
              {t('admin.accounts.unbalanced', { amount: formatSigned(trial.difference) })}
            </p>
          ))}
        {accounts.data?.groups.map((g) => (
          <section key={g.name} className="money-group" aria-labelledby={`group-${g.name}`}>
            <h2 id={`group-${g.name}`} className="stock-section-title money-group__title">
              <span>{g.name}</span>
              <span className="mono">{formatSigned(g.total)}</span>
            </h2>
            <Table
              caption={g.name}
              columns={columns}
              rows={g.accounts}
              rowKey={(a) => a.code}
              rowClassName={(a) => (a.code === code ? 'money-row--selected' : undefined)}
            />
          </section>
        ))}
      </div>
      {code && shown && (
        <LedgerPanel key={code} code={code} asAt={shown} onClose={() => set({ account: null })} />
      )}
    </main>
  );
}

function LedgerPanel({ code, asAt, onClose }: { code: string; asAt: string; onClose: () => void }) {
  const [from, setFrom] = useState(addDays(asAt, -6));
  const [to, setTo] = useState(asAt);
  const ledger = useQuery({
    queryKey: ['accounts', 'ledger', code, from, to],
    queryFn: () => api<AccountLedger>(`/api/admin/accounts/${code}/ledger?from=${from}&to=${to}`),
    enabled: from <= to,
  });
  const columns: Column<LedgerLine>[] = [
    {
      key: 'date',
      header: t('admin.accounts.date'),
      mono: true,
      render: (l) => (l.date ? l.date.slice(5) : ''),
    },
    {
      key: 'entry',
      header: t('admin.accounts.entry'),
      render: (l) => (
        <>
          {l.entry}
          {l.late && <span className="money-tag">{t('admin.cashbook.late')}</span>}
        </>
      ),
    },
    { key: 'memo', header: t('admin.accounts.reference'), render: (l) => l.memo },
    {
      key: 'debit',
      header: t('admin.accounts.debit'),
      align: 'right',
      mono: true,
      render: (l) => (l.debit ? formatAmount(l.debit) : ''),
    },
    {
      key: 'credit',
      header: t('admin.accounts.credit'),
      align: 'right',
      mono: true,
      render: (l) => (l.credit ? formatAmount(l.credit) : ''),
    },
    {
      key: 'balance',
      header: t('admin.accounts.balance'),
      align: 'right',
      mono: true,
      render: (l) => formatSigned(l.balance),
    },
  ];
  const data = ledger.data;
  return (
    <aside className="money-ledger" aria-label={t('admin.accounts.ledger')}>
      <div className="stock__header">
        <h2 className="stock-section-title">{data?.name ?? code}</h2>
        <button type="button" className="stock-link" onClick={onClose}>
          {t('common.close')}
        </button>
      </div>
      <div className="stock-form__row">
        <Field
          label={t('admin.accounts.from')}
          type="date"
          value={from}
          max={to}
          onChange={(e) => e.target.value && setFrom(e.target.value)}
        />
        <Field
          label={t('admin.accounts.to')}
          type="date"
          value={to}
          max={todayIso()}
          onChange={(e) => e.target.value && setTo(e.target.value)}
        />
      </div>
      {data && (
        <>
          <p className="money-line">
            <span>{t('admin.accounts.opening', { date: formatBusinessDate(data.from) })}</span>
            <span className="mono">{formatSigned(data.opening)}</span>
          </p>
          <Table
            caption={t('admin.accounts.ledger')}
            columns={columns}
            rows={data.lines}
            rowKey={(l) => `${l.entryId}-${l.memo}-${l.debit}-${l.credit}`}
            empty={t('admin.accounts.noLines')}
          />
          <p className="money-line money-line--total">
            <span>{t('admin.accounts.closing', { date: formatBusinessDate(data.to) })}</span>
            <span className="mono">{formatSigned(data.closing)}</span>
          </p>
        </>
      )}
    </aside>
  );
}
