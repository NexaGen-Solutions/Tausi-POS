export type SettingType = 'Bool' | 'Int' | 'Decimal' | 'String' | 'Enum' | 'List';

export interface SettingEntry {
  key: string;
  type: SettingType;
  label: string;
  help: string;
  value: unknown;
  default: unknown;
  min: number | null;
  max: number | null;
  allowed: string[] | null;
  reloadsTerminals: boolean;
  maxLength: number;
  required: boolean;
  /** The switch's place in the Rules list, or null. */
  rule: number | null;
  editable: boolean;
}

export interface SettingsSection {
  key: string;
  title: string;
  entries: SettingEntry[];
}

export interface Settings {
  version: number;
  sections: SettingsSection[];
  logoFileId: string | null;
  logoPrintUrl: string | null;
}

export interface SettingChange {
  at: string;
  who: string;
  key: string;
  label: string;
  old: string | null;
  new: string | null;
}

export interface ReceiptPreviewResult {
  width: number;
  bill: string;
  receipt: string;
  logoPng: string | null;
}

export const settingsKey = ['settings', 'admin'] as const;
