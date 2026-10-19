export interface Brand {
  name: string;
  logoFileId: string | null;
  currency: string;
}

export interface DeviceInfo {
  terminalId: string;
  terminalName: string;
  businessName: string;
  logoFileId: string | null;
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
