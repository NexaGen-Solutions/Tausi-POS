# Task 12 — Cash book, money movements and the daily summary

Read `AGENTS.md` first. Use the wireframe **"14 · Admin: cash book and daily summary"** in `docs/wireframes/`. Work on a new branch `screen/12-cashbook` from `main` (tag `screen-11-v1`).

Do not merge into `main` and do not create tags. Push your branch and stop.

By now, sales, cash-ups, deliveries, stock and losses all post balanced journal entries. This task gives the owner the **money side**:
- expenses paid from the safe, bank or M-Pesa
- paying suppliers
- banking and moving money between Safe, Bank and M-Pesa
- opening balances
- one page that shows a day's money and a **real profit figure**

The ledger stays behind the scenes. Staff never see debits and credits, but an **Accounts** view lets the owner check the books.

**Not in this task:**
- Reports for date ranges come in Task 13. This task is per business day.
- ERPNext comes in a later version.

---

## Part A — Business date

Every journal entry gets a **business date**:
- **Entries made inside a work period** (sales, cash-ups, pay-outs, cost of sales and so on) use the period's **business date**, which is the date it was opened, in `business.timeZone`. A Friday that runs past midnight is still Friday.
- **Entries made on the admin site** (expenses, supplier payments, banking, deliveries, counts) use the date chosen on the form. It defaults to today and is never in the future.
  - **Back-dating** is allowed up to `books.backdateDays` (a new setting, default 31).
  - An entry back-dated into a day that's already closed is marked **Late entry** and shown that way in the cash book.

Add the column, and **back-fill existing entries** from their work period, or from their created date where there's no work period.

## Part B — Accounts

**Ledger accounts:** add any that are missing.
- **Money:** Cash (drawers), Safe, Bank, M-Pesa, Card clearing
- **Other:** Supplier payables, Owner's capital, Owner's drawings, Bank charges, Other income
- **Expense accounts:** keep one **Expenses** account, with the **category** stored on each journal line as a dimension, so reports can split it.

**Expense categories:**
- An admin list (name, active) reached from the cash book through **Manage categories**.
- It extends the Task 9 seed.
- A category that's in use can be deactivated but not deleted.

**Accounts view** (`/admin/accounts`), read-only, needs `cash.book`:
- **Balances:** every account with its balance as at a chosen date, grouped as Money, Stock, Owed, Sales and income, Costs and expenses, and Owner.
- **Account ledger:** tapping an account lists its journal lines for a date range, showing the date, entry, reference, debit, credit and running balance.
- **Trial balance check:** at the top, "Debits = Credits ✓", or a red warning with the difference. A test proves it's always balanced.

## Part C — Money actions (admin site)

All of these need `cash.book`, except Add expense, which needs `cash.expense`. Each one:
- posts **one balanced journal entry**
- is audited
- takes `Idempotency-Key`
- follows the **Needs approval = pending** pattern from Task 11 Part G. Generalise that pattern into one shared admin approvals queue, so stock and money documents use the same code.

**Add expense:**
- business date
- amount
- category
- **paid from**: Safe, Bank or M-Pesa
- payee
- reference
- note
- an optional **receipt photo**: JPG, PNG or PDF, up to 5 MB, stored with the existing file storage

Journal: debit **Expenses** (with the category), credit the account it was paid from. **Drawer** expenses stay in the POS as Pay out (Task 9). The help text says so.

**Pay supplier:**
- supplier
- **owed**, shown as a read-only figure
- amount, which can't be more than what's owed: "KBL is owed 10,000. Pay 10,000 or less."
- paid from: Safe, Bank or M-Pesa
- reference
- date
- an optional link to specific deliveries

Journal: debit **Supplier payables**, credit the account it was paid from. The supplier's balance on the deliveries page updates.

**Move money:** one form covering:
- **Bank the cash:** Safe → Bank
- **M-Pesa to bank:** M-Pesa → Bank
- **Withdraw:** Bank → Safe
- **Float to the safe:** M-Pesa → Safe, and so on

Its fields are from, to, amount, an **optional fee** (posted as debit **Bank charges**, credit "from"), reference and date. A transfer that would take the "from" account below 0 shows a warning, but isn't blocked. The page's **Record banking** button opens this form with Safe → Bank already chosen.

**Owner money:**
- **Owner took money:** debit **Owner's drawings**, credit Safe, Bank or M-Pesa.
- **Owner put money in:** debit the money account, credit **Owner's capital**.

**Other income:** money received outside the POS (for example rent from a vendor). Debit the money account, credit **Other income**, with a note.

**Opening balances:** once only, for users with `settings.manage`:
- the starting Safe, Bank and M-Pesa balances
- what's owed to each supplier

Journal: debit the money accounts, credit **Opening balances**. For each supplier, debit **Opening balances**, credit **Supplier payables**. After it's saved, the form locks, and changing it needs a reversal. The setup wizard (Task 14) reuses this.

**Correcting an entry:** entries are never edited or deleted. Every money action has **Reverse**, which needs a reason and `cash.book`. It posts the exact opposite entry, linked both ways, and both show in the cash book.

## Part D — The cash book page (`/admin/cashbook`)

Match wireframe 14. Access needs `cash.book` or `reports.all`.

**Header:**
- "Cash book · [business date]", with a date picker and previous / next arrows
- the buttons **Record banking**, **Pay supplier**, **Add expense**, and a **More** menu with Move money, Owner money, Other income, Opening balances and Manage categories

**Tiles,** as at the end of the chosen day:
- **Cash on hand**: Safe plus the drawers. The drawers should be 0 once every shift is closed. If one isn't, show "incl. KSh x in open drawers".
- **M-Pesa received**: M-Pesa payments that day, with the M-Pesa account balance underneath.
- **Bank**: the balance.
- **Owed to suppliers**.

**Entries for the day:** time, entry, account, In, Out.
- **Entry text is plain words, built from the journal entry:**
  - "Cash sales · CAROL shift"
  - "M-Pesa sales · CAROL shift", one line per shift and payment type rather than one per receipt
  - "Ice and charcoal (expense)"
  - "Paid KBL"
  - "Banked cash"
  - "Stock count variance"
  - "Cash short · CAROL shift"
  - "Late entry" tags and "Reversed" links
- **A filter:** **Money only** (the default: entries touching Cash, Safe, Bank, M-Pesa or Card clearing) or **All entries** (also stock loss and cost of sales).
- Tapping a row opens its detail: the journal lines, who, when, the reference, an attachment link, and **Reverse**.

**Daily summary,** on the right panel:
- Sales (incl. VAT), after discounts
- VAT
- **Net sales**
- Cost of sales
- **Gross profit**, with its % of net sales
- Expenses (drawer pay-outs + admin expenses), split by category when expanded
- **Stock loss and cash differences**: waste, poured voids, count variance, cash over/short. This line isn't in the wireframe, but without it profit is overstated.
- Bank charges
- Other income
- **Net for the day**
- Under it, the note from the wireframe: "Cost of sales comes from recipes and stock cost, so profit is real, not an estimate."
- **Cost of sales check:** if any menu items sold that day have no recipe, show a warning, "Cost of sales is missing for 5 items with no recipe", with a link to the list.

**Every figure must equal what the ledger says,** computed from journal lines for that business date and never estimated separately. Add a test that compares the summary with the ledger.

**Print and export:**
- **Print summary**: an A4 PDF using the same small PDF writer as Task 11, and optionally a receipt-printer version.
- **Export entries (CSV)**.

## Part E — Day report and close day

- **The Task 9 day report (Z)** gets the profit lines from the daily summary: cost of sales, gross profit, expenses, stock loss and cash differences, and net. Late entries made after the close aren't on the printed Z. The cash book shows them as Late entry.
- **The Close day screen** (Task 9) shows the same net figure.

## Part F — Default permissions

Set these only where they aren't set yet:

| Permission | Waiter | Cashier | Supervisor | Manager |
| --- | --- | --- | --- | --- |
| `cash.book` | NotAllowed | NotAllowed | NeedsApproval | Allowed |

`cash.expense` stays as it was in Task 9.

## Part G — Tests

**Server:**
- **Business date:**
  - A work period that runs past midnight uses its opening date.
  - Back-dating is limited by `books.backdateDays`, and future dates are refused.
  - Late entries are flagged.
  - The back-fill migration works on both providers.
- **Each money action posts the right balanced entry:**
  - an expense from Safe, from Bank and from M-Pesa, with its category on the line
  - a supplier payment, including the refusal when it's more than what's owed
  - Move money, with and without a fee
  - owner drawings and owner capital
  - other income
  - opening balances, including the lock after saving
- **Reverse:** it posts the exact opposite and links the two entries, and an entry can't be reversed twice.
- **Shared admin approvals:** money documents stay pending until approved, and a document's creator can't approve it. Stock documents still work through the shared queue.
- **Trial balance:** debits always equal credits over a full seeded day: sales, voids, gifts, discounts, cash-up short, deliveries, waste, a count, expenses, banking.
- **Daily summary:**
  - every line equals the ledger for that business date
  - net = gross profit − expenses − stock loss and cash differences − bank charges + other income
  - the no-recipe warning
- **Tiles:** correct at the end of the day, including the "open drawers" case.
- **Entry text:** sales are grouped one line per shift and payment type, and every kind of entry has its wording.
- **Attachments:** type and size limits, and only users with `cash.book` can download them.
- **Summary PDF** is produced, and the **CSV export** columns are right.

**Web:**
- the date picker and arrows
- the tiles
- the Money only / All filter
- each action form, with its validation (overpaying a supplier, a future date, back-dating limits)
- the Reverse flow
- the summary lines, expanding the expenses split, and the no-recipe warning
- the Accounts view, its ledger, and the trial balance banner

## Acceptance checklist

- [ ] Enter opening balances: Safe 20,000, Bank 150,000, M-Pesa 5,000, KBL owed 30,000. The tiles and the Accounts view show them, and the form locks.
- [ ] Run a demo day: sales, a void, a gift, a cash-up with 100 short, a delivery and a count. The cash book shows grouped sales lines per shift and type, and the daily summary shows real cost of sales, gross profit, stock loss and cash differences, and net.
- [ ] Add an expense of 1,500 for Repairs, paid from the Safe, with a photo. It appears, and the Safe tile drops by 1,500.
- [ ] Pay KBL 10,000 from the Bank. KBL's owed amount drops to 20,000, and paying 25,000 is refused.
- [ ] Record banking of 15,000 from the Safe to the Bank with a 50 fee. The tiles move, and Bank charges shows 50.
- [ ] Reverse the expense. Both entries show, and the Safe goes back up.
- [ ] As a supervisor (cash.book Needs approval), pay a supplier. It waits pending until the Administrator approves it.
- [ ] The Accounts view shows "Debits = Credits ✓", and the daily summary equals the account ledger figures for the day.
- [ ] The Z report from closing the day includes the profit lines.
- [ ] All tests, lint and CI pass, including the Windows installer job. Push `screen/12-cashbook` and stop.

When done, report as `AGENTS.md` describes, with anything you decided differently from this prompt.
