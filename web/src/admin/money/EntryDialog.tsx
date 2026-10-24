import { useQuery } from '@tanstack/react-query';
import { useState } from 'react';
import { ApiError, api, apiUrl } from '../../shared/api/client';
import { formatDayTime } from '../../shared/format/dates';
import { formatAmount } from '../../shared/format/money';
import { t } from '../../shared/i18n/i18n';
import { Button, Field, Modal, Table, type Column } from '../../shared/ui';
import { formatBusinessDate } from './dates';
import type { EntryDetail, EntryLine, MoneyDocument } from './types';

/**
 * A cash book line in full: the journal lines, who and when, the reference, the receipt, and
 * Reverse (a reason; cash.book). Entries are never edited: Reverse posts the exact opposite.
 */
export function EntryDialog({
  entryId,
  onOpen,
  onClose,
}: {
  entryId: string;
  /** Opens the linked entry (the reversal, or the entry it reverses). */
  onOpen: (entryId: string) => void;
  onClose: (reversed: MoneyDocument | null) => void;
}) {
  const entry = useQuery({
    queryKey: ['cashbook', 'entry', entryId],
    queryFn: () => api<EntryDetail>(`/api/admin/cashbook/entries/${entryId}`),
  });
  const [reversing, setReversing] = useState(false);
  const [reason, setReason] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const detail = entry.data;
  const document = detail?.document ?? null;
  const canReverse =
    !!document &&
    document.status === 'Posted' &&
    document.reversalStatus !== 'Posted' &&
    document.reversalStatus !== 'Pending' &&
    document.journalEntryId === detail?.id;

  const reverse = async () => {
    setBusy(true);
    setError(null);
    try {
      onClose(
        await api<MoneyDocument>(`/api/admin/money/${document!.id}/reverse`, {
          method: 'POST',
          body: { reason: reason.trim() },
        }),
      );
    } catch (e) {
      setError(e instanceof ApiError ? e.message : t('common.somethingWrong'));
    } finally {
      setBusy(false);
    }
  };

  const columns: Column<EntryLine>[] = [
    {
      key: 'account',
      header: t('admin.cashbook.account.header'),
      render: (l) => (l.category ? `${l.account} · ${l.category}` : l.account),
    },
    { key: 'memo', header: t('admin.cashbook.detail.memo'), render: (l) => l.memo },
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
  ];

  return (
    <Modal
      title={detail?.memo ?? t('common.loading')}
      subtitle={
        detail &&
        [
          detail.businessDate && formatBusinessDate(detail.businessDate),
          formatDayTime(detail.at),
          detail.by && t('admin.cashbook.detail.by', { name: detail.by }),
          detail.late && t('admin.cashbook.late'),
        ]
          .filter(Boolean)
          .join(' · ')
      }
      onClose={() => onClose(null)}
      width={720}
    >
      {detail && (
        <>
          <Table
            caption={t('admin.cashbook.detail.lines')}
            columns={columns}
            rows={detail.lines}
            rowKey={(l) => `${l.code}-${l.memo}-${l.debit}-${l.credit}`}
          />
          {document && (
            <dl className="money-facts">
              {document.reference && (
                <>
                  <dt>{t('admin.cashbook.form.reference')}</dt>
                  <dd>{document.reference}</dd>
                </>
              )}
              {document.payee && (
                <>
                  <dt>{t('admin.cashbook.form.payee')}</dt>
                  <dd>{document.payee}</dd>
                </>
              )}
              {document.note && (
                <>
                  <dt>{t('admin.cashbook.form.note')}</dt>
                  <dd>{document.note}</dd>
                </>
              )}
              {document.approvedBy && (
                <>
                  <dt>{t('admin.cashbook.detail.approvedBy')}</dt>
                  <dd>{document.approvedBy}</dd>
                </>
              )}
              {document.reversalReason && (
                <>
                  <dt>{t('admin.cashbook.detail.reversalReason')}</dt>
                  <dd>{document.reversalReason}</dd>
                </>
              )}
            </dl>
          )}
          <div className="money-links">
            {document?.hasAttachment && (
              <a
                className="nx-btn nx-btn--link nx-btn--md"
                href={apiUrl(`/api/admin/money/${document.id}/attachment`)}
                target="_blank"
                rel="noreferrer"
              >
                {t('admin.cashbook.detail.receipt')}
              </a>
            )}
            {detail.reversesEntryId && (
              <Button variant="link" onClick={() => onOpen(detail.reversesEntryId!)}>
                {t('admin.cashbook.detail.openReversed')}
              </Button>
            )}
            {detail.reversedByEntryId && (
              <Button variant="link" onClick={() => onOpen(detail.reversedByEntryId!)}>
                {t('admin.cashbook.detail.openReversal')}
              </Button>
            )}
          </div>
          {document?.reversalStatus === 'Pending' && (
            <p className="stock-note">{t('admin.cashbook.detail.reversalPending')}</p>
          )}
          {reversing && (
            <Field
              label={t('admin.cashbook.detail.reason')}
              value={reason}
              maxLength={200}
              autoFocus
              onChange={(e) => setReason(e.target.value)}
            />
          )}
          {error && (
            <p className="nx-field__error" role="alert">
              {error}
            </p>
          )}
          <div className="stock-dialog__buttons">
            <Button onClick={() => onClose(null)}>{t('common.close')}</Button>
            {canReverse &&
              (reversing ? (
                <Button
                  variant="danger"
                  disabled={busy || !reason.trim()}
                  onClick={() => void reverse()}
                >
                  {t('admin.cashbook.detail.confirmReverse')}
                </Button>
              ) : (
                <Button variant="danger" onClick={() => setReversing(true)}>
                  {t('admin.cashbook.detail.reverse')}
                </Button>
              ))}
          </div>
        </>
      )}
    </Modal>
  );
}
