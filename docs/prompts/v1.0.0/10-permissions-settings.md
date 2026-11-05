# Task 10 — Admin: Permissions and Settings

Read `AGENTS.md` first. Use the wireframes **"16 · Admin: permissions"** and **"18 · Admin: settings"** in `docs/wireframes/`. Work on a new branch `screen/10-permissions-settings` from `main` (tag `screen-09-v1`).

Do not merge into `main` and do not create tags. Push your branch and stop.

Until now, permissions and settings were set by seed, migration or curl. This task gives the owner two admin screens:
- the **permission grid** (roles × actions)
- **Settings**, which gathers every setting added in Tasks 1–9 in one place

It also turns the wireframe's rule switches into real behaviour.

---

## Part A — One settings registry (server)

Before building the screen, gather every setting into **one typed registry** in Application. Each entry has:
- key
- type: bool, int, decimal, string, enum, list
- default
- section
- label and help text
- min/max or allowed values
- whether a change needs the terminals to reload

Read settings only through this registry, and replace any scattered default values. Every key below must exist; add any key from earlier tasks that this list missed. Values the owner hasn't changed fall back to the defaults.

**Business:**
- name
- logo
- address
- phone
- VAT rate (default 16)
- currency (KES)
- `business.timeZone`
- receipt footer

**POS:**
- `pos.idleLogoutEnabled`, default on (the wireframe's switch)
- `pos.idleLogoutMinutes`, 10, range 1–120
- `pos.logoutAfterSettle`
- `pos.tableIdleMinutes`, 30
- `pos.approvalExpiryMinutes`, 30
- `pos.approvalsFromOwnCounter`, default on
- `pos.approvalChime`, default on (the follow-up from Task 7)

**Printing:**
- `print.mode` (OneButton / SendAndBill)
- `print.logoOnBill`
- `print.poweredByFooter`
- `print.showVoidsOnReceipt`, default off

**Money:**
- `money.rounding`
- `payments.mpesaCodeRequired`, default on
- `payments.recordBroughtBy`, default on
- `tickets.closeVoidedWithoutReceipt`, default on
- `cash.defaultFloat`
- `cash.denominations`
- `cashup.blind`

**Stock:** `stock.returnOnVoidIfNotServed`, default on. It's stored now and used by Task 11.

**Endpoints:**
- `GET /api/admin/settings` returns sections, entries, values, defaults and a `version`.
- `PUT /api/admin/settings` takes the changed keys plus the `version`. It needs `settings.manage`.
  - It's refused with a conflict message if someone else saved in the meantime.
  - It checks every value against the registry, and returns errors per field.
  - It writes **one audit entry per changed key**, with the old and new value.
  - It publishes `settings.changed` with the changed keys.

**Terminals and the admin site** reload what changed straight away when they get `settings.changed`. A changed business name or logo shows on the PIN screen without a restart.

**Logo:**
- `POST /api/admin/settings/logo` takes a PNG or JPG of up to 2 MB.
- Store the original, plus a web-sized copy.
- Make the cached 1-bit print raster again, as in Task 4.
- `DELETE` removes the logo.
- If logo upload already exists from an earlier task, reuse it.

**Receipt preview:** `GET /api/admin/settings/receipt-preview?width=42|32` renders a sample bill and receipt **with the current settings, including unsaved values sent as query or body**, using the real slip template code. It returns plain text, and the logo as a small image.

## Part B — Make the switches do what they say

These come from the wireframe's Rules list. Each one must change behaviour on the server, not just hide a button.

| Setting | When on (default) | When off |
| --- | --- | --- |
| `print.mode` = OneButton ("One Print button") | Task 6 behaviour | `SendAndBill`: separate Send and Print bill |
| `pos.approvalsFromOwnCounter` | Option 2 (send request) is offered | Only the PIN on the spot. `POST /api/pos/approvals` is refused, and pending requests still in flight can still be decided. |
| `payments.recordBroughtBy` | Brought by is required, as in Task 8 | The field is hidden and filled with the user who opened the ticket. The receipt drops the "Cash brought by" line. |
| `print.showVoidsOnReceipt` (default **off**) | Voided items are listed on the receipt, struck through or marked "VOID", with 0 | Voided items are left off the receipt and bill (the current behaviour) |
| `tickets.closeVoidedWithoutReceipt` | A ticket whose lines are all voided or cancelled, with a 0 total, gets **Close ticket** on the order and settle screens for anyone with `ticket.settle`, with no receipt | Such a ticket is settled with a 0 receipt. `close-empty` from Task 8 still works for `day.open_close`. |
| `stock.returnOnVoidIfNotServed` | Stored for Task 11. Help text: "Supervisor ticks 'not served' when approving". In the void pop-up, "Was it already poured?" stays. | The question is hidden, and every void counts as poured |
| `payments.mpesaCodeRequired` | The code is required, as in Task 8 | The code is optional. When given, it must still be valid and unique. Payments without a code are marked "No code" on the receipt and cash-up. |
| `pos.idleLogoutEnabled` with `pos.idleLogoutMinutes` | Server-enforced idle logout, as in Task 1 | No idle logout. The 12-hour absolute session limit stays. The switch label shows the real minutes: "Log out after 10 minutes idle". |

## Part C — Settings screen (`/admin/settings`)

Match wireframe 18. It needs `settings.manage`.

**Layout:** two columns.
- **Left, "Business details":**
  - business name
  - logo: a preview, **Upload logo**, **Remove**, and the help text "PNG or JPG. Printed in black and white on receipts.", with a preview of the 1-bit print version next to the colour one
  - address and phone
  - VAT rate
  - currency
  - time zone, as a searchable list of IANA names
  - receipt footer
- **Right, "Rules":** the switches, in the wireframe's order, each with its help text, followed by sections for **POS**, **Printing**, **Money**, **Cash-up** and **Stock**, holding the other settings as the right inputs (number fields with their range, choices, the denominations list).

**Saving:**
- **Save changes** sits at the top right. It's disabled until something changes.
- Leaving with unsaved changes asks first.
- Field errors show under each field.
- On success, a toast: "Saved. Terminals updated."
- On a version conflict: "Someone else saved settings. Reload to see their changes." Nothing is lost from the form.

**Receipt preview:** a **Preview receipt** button opens a side panel with the Part A preview at 42 or 32 columns. It updates as the form changes, before saving.

**Every changed setting** is listed in the audit log. Add a small **History** link that shows the last 50 changes: who, when, key, old and new.

## Part D — Permissions screen (`/admin/permissions`)

Match wireframe 16. It needs `permissions.manage`.

**The grid:**
- **Columns:** one per role (Waiter, Cashier, Supervisor, Admin, plus any added).
- **Rows:** **every permission key in `AGENTS.md`**, grouped under section headings with plain labels:
  - **Orders:** add, cancel unprinted, void, gift, discount, change price
  - **Tickets:** print, reprint, move, merge, settle
  - **Menu:** edit, edit prices
  - **Stock:** receive, waste, count, adjust
  - **Money:** expenses, cash book
  - **Reports:** own, all
  - **Day:** open and close
  - **Admin:** staff, permissions, devices, settings, ERPNext

  The wireframe's combined rows were only a sketch. Show each key, so nothing is hidden.
- **Cells** are pills: **Allowed** in blue, **Needs approval** in orange and **Not allowed** in grey.
  - Tapping a cell steps through the values the key allows.
  - Keys that can't be approved show only Allowed and Not allowed: `ticket.settle`, every Menu, Day and Admin key, and `reports.*`. The server rejects NeedsApproval for them too.
- **Changes are staged.** Changed cells get an outline and the count shows on **Save changes (3)**, with a **Discard** button next to it. Saving sends them all at once, with a version check.
- **Each row has a short help line** under its label, for example "Void a printed item. Needs approval: supervisor PIN or request".

**Roles:**
- **+ Add role:** a name, plus **Copy permissions from** an existing role.
- **Rename.**
- **Delete,** but only when no user has that role. Otherwise it says which users have it.

**Protection against locking yourself out:**
- The save is refused if it would leave **no active user** with `permissions.manage` Allowed.
- The same goes for `staff.manage`.
- The Admin role's `permissions.manage` cell is locked to Allowed, with a padlock and the tooltip "Kept so you can't lock yourself out".

**After saving:**
- Write one audit entry per changed cell: role, key, old, new, who.
- Publish `permissions.changed`. Open POS screens refresh their permission map straight away, so buttons appear or disappear without logging out. The server checks every request anyway.

**Who approves what:** a side panel lists, for each approvable key, which roles can approve it (the roles where it's Allowed). If a key is NeedsApproval for some role but **no role is Allowed**, show a warning: "Nobody can approve voids". Saving is refused in that case.

**History:** the last 50 permission changes, as in Part C.

## Part E — Tests

**Server:**
- **The registry:**
  - every key has a default and validation
  - unknown keys are refused
  - out-of-range values return errors per field
  - the version conflict
  - one audit entry per changed key
  - `settings.changed` is published with exactly the changed keys
- **Each switch in Part B changes server behaviour:**
  - Option 2 is refused when `pos.approvalsFromOwnCounter` is off
  - brought-by is filled in automatically, and the receipt drops its line
  - voided items appear on the receipt when `print.showVoidsOnReceipt` is on (as a golden slip)
  - Close ticket without a receipt works for a zero-total ticket, and is refused when the total isn't 0
  - an M-Pesa payment without a code is accepted when the code is optional, and a code that is given is still checked for uniqueness
  - no idle logout when `pos.idleLogoutEnabled` is off, while the 12-hour limit still applies
- **Logo:**
  - the type and size limits
  - the raster is regenerated
  - remove
- **Receipt preview:** it uses the unsaved values, and matches the golden bill for the same settings.
- **Permissions:**
  - NeedsApproval is refused for keys that can't be approved
  - saving is refused when nobody would have `permissions.manage` or `staff.manage`
  - saving is refused when nobody can approve a NeedsApproval key
  - adding a role copies its permissions
  - a role with users can't be deleted
  - the version conflict
  - one audit entry per cell
  - `permissions.changed` is published
  - a change takes effect on the next request without logging out

**Web:**
- **Settings:**
  - Save stays disabled until something changes
  - the unsaved-changes prompt
  - field errors
  - the conflict message
  - the preview panel updates before saving
  - logo upload and remove
- **A terminal** shows a new business name live after a save.
- **Permissions:**
  - cell cycling skips NeedsApproval on keys that can't be approved
  - staged changes with their count, Discard
  - the locked Admin cell
  - the "Nobody can approve" warning
  - Add role with Copy from
  - deleting a role that has users is blocked
- **A POS button** (Void) appears or disappears live after a permissions save.

## Acceptance checklist

- [ ] As Administrator, change the business name to "Mau's Village Bar" and upload a logo. The PIN screens on every terminal update within 2 seconds, and Preview receipt shows the new header and the logo.
- [ ] Turn off "One Print button". The order screen shows Send and Print bill, and both work.
- [ ] Turn off "Supervisors approve from their own counter". The void pop-up shows only the PIN pad, and the API refuses requests.
- [ ] Turn off "Record who brought the cash". Settle has no Brought by field, and the receipt has no brought-by line.
- [ ] Void every item on a ticket. With "Close a fully voided ticket without a receipt" on, the cashier sees Close ticket, and the ticket closes with no receipt.
- [ ] Turn off "M-Pesa code required". An M-Pesa payment without a code settles and shows "No code".
- [ ] In Permissions, set Waiter `order.gift` to Needs approval and save. The Gift button appears on edna's screen without her logging out.
- [ ] Try to set Admin `permissions.manage` to Not allowed. It's locked. Set every role's `order.void` to Needs approval: the warning shows and saving is refused.
- [ ] Add a role "Bar captain", copied from Waiter, and give it `order.void` Allowed. Assign it to a user in Staff. That user can approve voids.
- [ ] Every change shows in History with old and new values.
- [ ] All tests, lint and CI pass, including the Windows installer job. Push `screen/10-permissions-settings` and stop.

When done, report as `AGENTS.md` describes, with anything you decided differently from this prompt.
