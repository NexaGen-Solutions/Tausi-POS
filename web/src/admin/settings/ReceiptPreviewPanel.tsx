import { keepPreviousData, useQuery } from '@tanstack/react-query';
import { useEffect, useState } from 'react';
import { ApiError, api } from '../../shared/api/client';
import { t } from '../../shared/i18n/i18n';
import { Button } from '../../shared/ui';
import type { ReceiptPreviewResult } from './types';

/**
 * A sample bill and receipt as the printer would print them, with the form's unsaved values,
 * at 42 (80 mm) or 32 (58 mm) columns. It follows the form as it changes.
 */
export function ReceiptPreviewPanel({
  values,
  onClose,
}: {
  values: Record<string, unknown>;
  onClose: () => void;
}) {
  const [width, setWidth] = useState<42 | 32>(42);
  const text = JSON.stringify(values);
  const [settled, setSettled] = useState(text);
  useEffect(() => {
    const timer = window.setTimeout(() => setSettled(text), 300);
    return () => window.clearTimeout(timer);
  }, [text]);

  const preview = useQuery({
    queryKey: ['receipt-preview', width, settled],
    queryFn: () =>
      api<ReceiptPreviewResult>('/api/admin/settings/receipt-preview', {
        method: 'POST',
        body: { width, values: JSON.parse(settled) as Record<string, unknown> },
      }),
    placeholderData: keepPreviousData,
  });

  return (
    <aside className="settings-preview" aria-label={t('admin.settings.previewTitle')}>
      <div className="settings-preview__header">
        <h2 className="settings-panel__title">{t('admin.settings.previewTitle')}</h2>
        <Button onClick={onClose}>{t('common.close')}</Button>
      </div>
      <div className="settings-preview__widths" role="group" aria-label={t('admin.settings.paper')}>
        {([42, 32] as const).map((w) => (
          <Button
            key={w}
            aria-pressed={width === w}
            variant={width === w ? 'dark' : 'secondary'}
            onClick={() => setWidth(w)}
          >
            {t('admin.settings.columns', { columns: w, mm: w === 42 ? 80 : 58 })}
          </Button>
        ))}
      </div>
      {preview.error && (
        <p className="nx-field__error" role="alert">
          {preview.error instanceof ApiError && preview.error.problem.fieldErrors
            ? t('admin.settings.previewFix')
            : t('common.somethingWrong')}
        </p>
      )}
      {preview.data && (
        <div className="settings-preview__slips">
          {preview.data.logoPng && (
            <img
              className="settings-preview__logo"
              src={`data:image/png;base64,${preview.data.logoPng}`}
              alt={t('admin.settings.logoPrint')}
            />
          )}
          <pre className="settings-preview__slip" data-testid="preview-bill">
            {preview.data.bill}
          </pre>
          <pre className="settings-preview__slip" data-testid="preview-receipt">
            {preview.data.receipt}
          </pre>
        </div>
      )}
    </aside>
  );
}
