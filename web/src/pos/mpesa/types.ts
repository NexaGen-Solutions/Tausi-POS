/** Shapes of the M-Pesa API (server: Tausi.Application.Mpesa). */

export interface PosMpesa {
  setUp: boolean;
  stkEnabled: boolean;
  waiterPrompts: boolean;
  allowTypedCodes: boolean;
  received: number;
}

export type PromptStatus = 'Waiting' | 'Paid' | 'Failed' | 'Cancelled' | 'Expired';

export interface Prompt {
  id: string;
  ticketId: string;
  phone: string;
  amount: number;
  status: PromptStatus;
  sentAt: string;
  expiresAt: string;
  message: string | null;
  code: string | null;
  payerName: string | null;
  payerPhone: string | null;
  paidAt: string | null;
  paymentId: string | null;
  broughtByUserId: string | null;
  broughtBy: string | null;
}

export interface ReceivedPayment {
  id: string;
  code: string;
  at: string;
  payerName: string | null;
  phone: string | null;
  amount: number;
  available: number;
  how: string;
  match: 'QrOnThisBill' | 'SameAmount' | 'None';
}

export const posMpesaKey = ['pos-mpesa'] as const;

/** 07…, 01…, +254… and 254… as Safaricom numbers (the server checks again). */
export function validPhone(phone: string): boolean {
  let digits = phone.replace(/\D/g, '');
  if (digits.startsWith('254') && digits.length === 12) digits = digits.slice(3);
  else if (digits.startsWith('0') && digits.length === 10) digits = digits.slice(1);
  return digits.length === 9 && (digits[0] === '7' || digits[0] === '1');
}

/** "0712 345 678" as it is typed. */
export function spacedPhone(digits: string): string {
  const d = digits.replace(/\D/g, '').slice(0, 12);
  if (d.startsWith('254'))
    return [d.slice(0, 3), d.slice(3, 6), d.slice(6, 9), d.slice(9)].filter(Boolean).join(' ');
  return [d.slice(0, 4), d.slice(4, 7), d.slice(7, 10)].filter(Boolean).join(' ');
}
