import { useQuery } from '@tanstack/react-query';
import { useState } from 'react';
import { useNavigate } from 'react-router';
import { ApiError, api } from '../shared/api/client';
import type { Brand } from '../shared/api/types';
import { OfflineBanner } from '../shared/connection/OfflineBanner';
import { formatAmount, formatMoney } from '../shared/format/money';
import { t } from '../shared/i18n/i18n';
import { Button, Modal, TopBar, useToast } from '../shared/ui';
import { usePinSession } from './usePinSession';
import './cash/cash.css';

interface MySales {
  name: string;
  periodOpenedAt: string | null;
  total: number;
  tickets: number;
  cash: number;
  mpesa: number;
  card: number;
  openBills: { id: string; number: number; where: string; total: number }[];
  lines: {
    ticketId: string;
    ticketNumber: number;
    at: string;
    name: string;
    options: string | null;
    quantity: number;
    amount: number;
    voided: boolean;
    gift: boolean;
  }[];
}

const time = (iso: string) =>
  new Date(iso).toLocaleTimeString('en-GB', { hour: '2-digit', minute: '2-digit' });

/** Wireframe 8: a waiter's own figures for the day (only their own), and their cash-up slip. */
export function MySalesPage() {
  const navigate = useNavigate();
  const toast = useToast();
  const session = usePinSession();
  const brand = useQuery({ queryKey: ['brand'], queryFn: () => api<Brand>('/api/brand') });
  const currency = brand.data?.currency ?? 'KES';
  const sales = useQuery({
    queryKey: ['my-sales'],
    queryFn: () => api<MySales>('/api/pos/my-sales'),
    staleTime: 0,
  });
  const [billsOpen, setBillsOpen] = useState(false);
  const [busy, setBusy] = useState(false);
  const m = sales.data;

  const print = async () => {
    setBusy(true);
    try {
      await api('/api/pos/my-sales/print', { method: 'POST' });
      toast(t('pos.mySalesPage.printed'));
    } catch (error) {
      toast(error instanceof ApiError ? error.message : t('common.somethingWrong'), {
        tone: 'warning',
      });
    } finally {
      setBusy(false);
    }
  };

  return (
    <div className="pos-screen cash-screen">
      <TopBar
        start={
          <Button variant="ghost-dark" onClick={() => navigate('/pos/tables')}>
            {t('pos.order.tables')}
          </Button>
        }
        title={t('pos.mySalesPage.title', { name: session?.user.name ?? '' })}
        end={<span className="order-who">{t('pos.mySalesPage.privacy')}</span>}
      />
      <OfflineBanner />
      {m && (
        <main className="cash-body cash-body--sales">
          <div className="sales-side">
            <section className="cash-panel">
              <span className="cash-label">{t('pos.mySalesPage.total')}</span>
              <span className="sales-big">{formatMoney(m.total, currency)}</span>
            </section>
            <section className="cash-panel">
              <span className="cash-label">{t('pos.mySalesPage.tickets')}</span>
              <span className="sales-big">{m.tickets}</span>
            </section>
            <section className="cash-panel" aria-label={t('pos.mySalesPage.brought')}>
              <span className="cash-label">{t('pos.mySalesPage.brought')}</span>
              <div className="cash-row">
                <span>{t('pos.settle.type.Cash')}</span>
                <span className="cash-amount">{formatAmount(m.cash)}</span>
              </div>
              <div className="cash-row">
                <span>{t('pos.settle.type.Mpesa')}</span>
                <span className="cash-amount">{formatAmount(m.mpesa)}</span>
              </div>
              <div className="cash-row">
                <span>{t('pos.settle.type.Card')}</span>
                <span className="cash-amount">{formatAmount(m.card)}</span>
              </div>
            </section>
            <button
              type="button"
              className={`cash-panel sales-bills${m.openBills.length > 0 ? ' sales-bills--open' : ''}`}
              onClick={() => setBillsOpen(true)}
            >
              <span>{t('pos.mySalesPage.openBills')}</span>
              <span className={`cash-amount${m.openBills.length > 0 ? ' cash-warn' : ''}`}>
                {m.openBills.length}
              </span>
            </button>
            <span className="cash-spacer" />
            <Button variant="primary" size="lg" block disabled={busy} onClick={() => void print()}>
              {t('pos.mySalesPage.print')}
            </Button>
          </div>
          <section className="cash-panel sales-lines" aria-label={t('pos.mySalesPage.lines')}>
            <div className="sales-table-scroll">
              <table className="cash-table sales-table">
                <thead>
                  <tr>
                    <th scope="col">{t('pos.mySalesPage.ticket')}</th>
                    <th scope="col">{t('pos.mySalesPage.time')}</th>
                    <th scope="col">{t('pos.mySalesPage.item')}</th>
                    <th scope="col">{t('pos.mySalesPage.qty')}</th>
                    <th scope="col">{t('pos.mySalesPage.amount')}</th>
                  </tr>
                </thead>
                <tbody>
                  {m.lines.map((l, i) => (
                    <tr
                      key={`${l.ticketId}-${i}`}
                      className={l.voided ? 'sales-line--voided' : undefined}
                    >
                      <td className="cash-amount">#{l.ticketNumber}</td>
                      <td className="cash-amount">{time(l.at)}</td>
                      <td className="sales-item">
                        <span className="sales-item__name">
                          {l.options ? `${l.name} (${l.options})` : l.name}
                        </span>
                        {l.voided && <span className="sales-tag">{t('pos.mySalesPage.void')}</span>}
                        {l.gift && (
                          <span className="sales-tag sales-tag--gift">
                            {t('pos.mySalesPage.gift')}
                          </span>
                        )}
                      </td>
                      <td className="cash-amount">{l.quantity}</td>
                      <td className="cash-amount">{formatAmount(l.amount)}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
            <p className="cash-note">{t('pos.mySalesPage.note')}</p>
          </section>
        </main>
      )}
      {billsOpen && m && (
        <Modal
          title={t('pos.mySalesPage.openBills')}
          onClose={() => setBillsOpen(false)}
          width={480}
        >
          {m.openBills.length === 0 ? (
            <p>{t('pos.mySalesPage.noOpenBills')}</p>
          ) : (
            <ul className="cash-list">
              {m.openBills.map((b) => (
                <li key={b.id}>
                  <button
                    type="button"
                    className="cash-link"
                    onClick={() => navigate(`/pos/ticket/${b.id}`)}
                  >
                    #{b.number} · {b.where} · {formatMoney(b.total, currency)}
                  </button>
                </li>
              ))}
            </ul>
          )}
        </Modal>
      )}
    </div>
  );
}
