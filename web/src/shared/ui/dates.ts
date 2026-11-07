const pad = (n: number) => String(n).padStart(2, '0');

/** A date as "2026-10-20". */
export type IsoDate = string;

export interface DateRange {
  from: IsoDate;
  to: IsoDate;
}

export const isoDate = (date: Date): IsoDate =>
  `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}`;
