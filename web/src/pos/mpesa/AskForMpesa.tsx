import { useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { formatMoney } from '../../shared/format/money';
import { t } from '../../shared/i18n/i18n';
import { Button, Modal } from '../../shared/ui';
import { PromptForm } from './PromptForm';
import { PromptStatus } from './PromptStatus';
import type { Prompt } from './types';
import './mpesa.css';

/**
 * Board 25: a waiter asks for M-Pesa from the order screen (when mpesa.waiterPrompts is on). The
 * same prompt and flow as the cashier's; the payment goes on the ticket and the cashier completes it.
 */
export function AskForMpesa({
  ticketId,
  ticketNumber,
  where,
  total,
  remaining,
  currency,
  business,
  disabled,
}: {
  ticketId: string;
  ticketNumber: number;
  where: string;
  total: number;
  remaining: number;
  currency: string;
  business: string;
  disabled: boolean;
}) {
  const queries = useQueryClient();
  const [open, setOpen] = useState(false);
  const [prompt, setPrompt] = useState<Prompt | null>(null);
  const [phone, setPhone] = useState('');
  const [paid, setPaid] = useState<number | null>(null);

  const close = () => {
    if (prompt?.status === 'Paid') setPaid(prompt.amount);
    setOpen(false);
    setPrompt(null);
    void queries.invalidateQueries({ queryKey: ['ticket', ticketId] });
  };

  return (
    <>
      <Button size="lg" block disabled={disabled || remaining <= 0} onClick={() => setOpen(true)}>
        {paid !== null
          ? t('pos.mpesa.paidTick', { amount: formatMoney(paid, currency) })
          : t('pos.mpesa.ask')}
      </Button>
      {open && (
        <Modal
          title={t('pos.mpesa.askTitle')}
          subtitle={t('pos.mpesa.askFor', {
            where,
            number: ticketNumber,
            amount: formatMoney(total, currency),
          })}
          onClose={prompt?.status === 'Waiting' ? undefined : close}
          width={720}
        >
          {prompt ? (
            <PromptStatus
              prompt={prompt}
              currency={currency}
              onChange={setPrompt}
              onSendAgain={() => setPrompt(null)}
              onDone={close}
            />
          ) : (
            <PromptForm
              ticketId={ticketId}
              remaining={remaining}
              currency={currency}
              business={business}
              fromOrderScreen
              initialPhone={phone}
              onSent={(p, typed) => {
                setPhone(typed);
                setPrompt(p);
              }}
              onCancel={close}
            />
          )}
        </Modal>
      )}
    </>
  );
}
