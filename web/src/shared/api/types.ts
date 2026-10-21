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
}

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
