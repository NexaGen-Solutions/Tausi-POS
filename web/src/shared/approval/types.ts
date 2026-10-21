import type { ApprovalKind } from '../api/client';

/** GET /api/pos/approvals: one request for approval. */
export interface ApprovalRequest {
  id: string;
  kind: ApprovalKind;
  status: 'Pending' | 'Approved' | 'Rejected' | 'Cancelled' | 'Expired';
  ticketId: string;
  ticketNumber: number;
  where: string;
  lineId: string | null;
  summary: string;
  amount: number | null;
  reason: string | null;
  requestedById: string;
  requestedBy: string;
  terminal: string;
  requestedAt: string;
  preferredApproverId: string | null;
  sentToMe: boolean;
  mine: boolean;
  canDecide: boolean;
  decidedBy: string | null;
  decidedAt: string | null;
  decisionNote: string | null;
}

/** Someone logged in who can approve a kind of request, for "Send request to". */
export interface Approver {
  userId: string;
  name: string;
  role: string;
  terminal: string;
}

/** The approval.requested and approval.resolved events. */
export interface ApprovalEvent {
  requestId: string;
  kind: ApprovalKind;
  ticketId: string;
  status?: ApprovalRequest['status'];
  requestedByUserId?: string;
  requestedBy?: string;
  summary?: string;
  decidedBy?: string | null;
  note?: string | null;
}

export const permissionFor: Record<ApprovalKind, string> = {
  Void: 'order.void',
  Gift: 'order.gift',
  Discount: 'order.discount',
  PriceChange: 'order.change_price',
  Reprint: 'ticket.reprint',
  Move: 'ticket.move',
  Merge: 'ticket.merge',
};

export const pendingKey = ['approvals', 'pending'] as const;
