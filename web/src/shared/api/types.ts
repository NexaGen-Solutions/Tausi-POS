export interface Brand {
  name: string;
  logoFileId: string | null;
  currency: string;
}

export interface WorkPeriod {
  id: string;
  openedAt: string;
  openedByUserId: string;
}

export interface DeviceInfo {
  terminalId: string;
  terminalName: string;
  businessName: string;
  logoFileId: string | null;
  workPeriod: WorkPeriod | null;
  printing?: DevicePrinting | null;
  rules?: PosRules | null;
  /** Training mode is on for this terminal: practice, kept out of the real figures. */
  training?: boolean;
}

/** Settings that change what the terminal screens offer (the server enforces them too). */
export interface PosRules {
  approvalsFromOwnCounter: boolean;
  recordBroughtBy: boolean;
  mpesaCodeRequired: boolean;
  closeVoidedWithoutReceipt: boolean;
  returnOnVoidIfNotServed: boolean;
  approvalChime: boolean;
}

/** The rules a terminal follows until /api/device has answered (the defaults). */
export const defaultPosRules: PosRules = {
  approvalsFromOwnCounter: true,
  recordBroughtBy: true,
  mpesaCodeRequired: true,
  closeVoidedWithoutReceipt: true,
  returnOnVoidIfNotServed: true,
  approvalChime: true,
};

/** Where this terminal prints, and which print buttons the order screen shows. */
export interface DevicePrinting {
  mode: 'OneButton' | 'SendAndBill';
  receiptPrinter: string | null;
  dispensePrinter: string | null;
  /** "Dispense A" (the dispense printer's template) or the printer's name. */
  dispenseName: string | null;
}

export interface Me {
  user: {
    id: string;
    name: string;
    role: string;
    terminalId: string | null;
    terminalName: string | null;
  };
  permissions: Record<string, 'Allowed' | 'NeedsApproval' | 'NotAllowed'>;
}
