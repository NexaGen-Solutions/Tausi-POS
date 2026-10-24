import { useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { useNavigate } from 'react-router';
import { ApiError, api } from '../../shared/api/client';
import { t } from '../../shared/i18n/i18n';
import { Button, useToast } from '../../shared/ui';

/** "Close ticket": a fully voided ticket closes with no receipt (tickets.closeVoidedWithoutReceipt). */
export function CloseVoidedButton({
  ticketId,
  disabled,
}: {
  ticketId: string;
  disabled?: boolean;
}) {
  const navigate = useNavigate();
  const queries = useQueryClient();
  const toast = useToast();
  const [busy, setBusy] = useState(false);

  const close = async () => {
    setBusy(true);
    try {
      await api(`/api/pos/tickets/${ticketId}/close-voided`, { method: 'POST' });
      toast(t('pos.settle.closedVoided'));
      void queries.invalidateQueries({ queryKey: ['table-map'] });
      navigate('/pos/tables');
    } catch (error) {
      toast(error instanceof ApiError ? error.message : t('common.somethingWrong'), {
        tone: 'warning',
      });
    } finally {
      setBusy(false);
    }
  };

  return (
    <Button variant="primary" size="lg" disabled={disabled || busy} onClick={() => void close()}>
      {t('pos.settle.closeTicket')}
    </Button>
  );
}
