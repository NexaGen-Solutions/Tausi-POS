import type { ReactNode } from 'react';

export interface Column<Row> {
  key: string;
  header: string;
  align?: 'left' | 'right';
  /** Money, quantities and codes are shown in the mono font. */
  mono?: boolean;
  render: (row: Row) => ReactNode;
}

export interface TableProps<Row> {
  columns: Column<Row>[];
  rows: Row[];
  rowKey: (row: Row) => string;
  caption?: string;
  empty?: ReactNode;
}

/** Admin list table. Wide tables scroll sideways inside their box, never the page. */
export function Table<Row>({ columns, rows, rowKey, caption, empty }: TableProps<Row>) {
  const cellClass = (column: Column<Row>) =>
    [column.align === 'right' ? 'nx-table--right' : '', column.mono ? 'nx-table--mono' : '']
      .join(' ')
      .trim() || undefined;

  return (
    <div className="nx-table-box">
      <table className="nx-table">
        {caption && <caption className="visually-hidden">{caption}</caption>}
        <thead>
          <tr>
            {columns.map((column) => (
              <th
                key={column.key}
                scope="col"
                className={column.align === 'right' ? 'nx-table--right' : undefined}
              >
                {column.header}
              </th>
            ))}
          </tr>
        </thead>
        <tbody>
          {rows.length === 0 && empty ? (
            <tr>
              <td colSpan={columns.length}>{empty}</td>
            </tr>
          ) : (
            rows.map((row) => (
              <tr key={rowKey(row)}>
                {columns.map((column) => (
                  <td key={column.key} className={cellClass(column)}>
                    {column.render(row)}
                  </td>
                ))}
              </tr>
            ))
          )}
        </tbody>
      </table>
    </div>
  );
}
