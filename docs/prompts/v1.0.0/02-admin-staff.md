# Task 2 — Admin: Staff and PINs

Read `AGENTS.md` first. Use the wireframe **"15 · Admin: staff and PINs"** in `docs/wireframes/`. Work on a new branch `screen/02-admin-staff` from `main` (tag `screen-01-v1`).

**Do not merge into `main` and do not create tags.** Push your branch and stop; I review and merge.

---

## Part A — Fix: four demo printers

The demo seed should create **four** printers, one per terminal:

| Printer | On terminal | Used as |
| --- | --- | --- |
| Ticket Printer 1 | Counter A | Dispense A + receipts for Counter A |
| Pillar 1 | Pillar 1 | Dispense A for Pillar 1 + receipts for Pillar 1 |
| Ticket Printer 2 | Counter B | Dispense B + receipts for Counter B |
| Pillar 2 | Pillar 2 | Dispense B for Pillar 2 + receipts for Pillar 2 |

- Each terminal's dispense printer and receipt printer is its own printer.
- "Dispense A" and "Dispense B" are the **templates** (header "Counter A" / "Counter B"), not separate devices. Pillar 1 prints with the Dispense A template and Pillar 2 with the Dispense B template.
- **Re-running the seed** on an existing database converts the two-printer setup to four without losing anything.
- Update `docs/DECISIONS.md`.

## Part B — Staff list (`/admin/staff`)

Match wireframe 15. Access needs `staff.manage`.

**Table columns:**
- **Name**
- **Role**
- **Status:** Active or Inactive
- **Must change PIN:** a small "PIN reset" tag
- **Last login:** time and terminal
- **Terminals used (7 days):** from audit login entries, e.g. "Counter A, Pillar 1"
- **Actions:** Edit, Reset PIN, Deactivate / Reactivate

**List behaviour:**
- Filter by role and by status (Active by default) and search by name.
- Inactive users are greyed and listed after active ones.

## Part C — Add and edit staff

Use a right-side panel, as in the wireframe.

**Fields:**
- **Name:** required, unique among active users, case-insensitive, 2–40 characters.
- **Role:** a dropdown of roles.
- **Starting PIN:** 4–6 digits. New users always get `MustChangePin = true`. The same PIN rules as Screen 1 apply: not another active user's PIN, and not a blocked sequence.
- **Admin-site password:** only shown when the role is Supervisor or Admin. Required for new Supervisor/Admin users, minimum 10 characters, hashed. Shown with a "set / reset" control when editing.

**When saving:**
- Show the starting PIN **once** in a confirmation: "Give this PIN to Grace. She must change it at first login." After that it is never shown again.
- Audit the change with the old and new values, **never** including PINs or passwords.

**Editing:**
- Allowed: name, role and admin-site password.
- Not allowed here: the PIN. That has its own action (Part D).

## Part D — Reset PIN, deactivate, reactivate

- **Reset PIN:** the manager enters a new temporary PIN, or presses **Generate**, which creates a random valid one.
    - The user gets `MustChangePin = true`.
    - The temporary PIN is shown once.
    - The user's **current sessions end immediately** on every terminal.
- **Deactivate:** after a confirmation, the user can no longer log in and their sessions end immediately.
    - Every terminal where they are logged in returns to the PIN screen within 2 seconds, via a SignalR event `user.deactivated`.
    - Their past orders, payments and audit history stay linked to them.
    - Deactivating does **not** free up their name for reuse.
- **Reactivate:** the user becomes active again with `MustChangePin = true`, and a new PIN must be set during reactivation.
- **No deleting.** People can't be deleted, because their history must stay intact.

## Part E — Who can manage whom (server-enforced)

These rules are checked in the Application layer, with tests for each:

1. **`staff.manage` is required** for everything on this screen.
2. **A Supervisor can only create, edit, reset or deactivate users whose role is Waiter or Cashier.**
    - They can't assign Supervisor or Admin.
    - They can't edit or deactivate Supervisors or Admins, including themselves.
    - These targets are hidden in the UI, and the server rejects them.
3. **An Admin can manage everyone**, with these exceptions:
    - **They can't deactivate the last active Admin.** The error says: "There must be at least one active Admin."
    - **They can't change their own role or deactivate themselves.** Another admin must do it, so nobody locks themselves out by accident.
4. **No user can ever see another user's PIN.** PINs are only ever stored hashed.
5. **Custom roles.** Roles other than the four built-in ones (from a future Permissions screen) count as "Waiter-level" for rule 2 unless their permissions include `staff.manage`. Record this decision in `DECISIONS.md`.

## Part F — API

Use `/api/admin/staff` for list, get, create and update, and `POST /api/admin/staff/{id}/reset-pin`, `/deactivate` and `/reactivate`.

- Every write uses `Idempotency-Key` and optimistic concurrency.
- If another admin changed the same person meanwhile, return 409, and the UI shows "This person was changed by someone else. Reload and try again."

## Part G — Tests

**Server:**
- Create: the name uniqueness check is case-insensitive.
- PIN rules on create and reset.
- `MustChangePin` is set on create, reset and reactivate.
- The temporary PIN appears only in the create and reset responses.
- Every Part E rule, including:
    - a Supervisor trying to promote a user
    - a Supervisor trying to edit an Admin
    - deactivating the last Admin
    - self-deactivation
- Deactivate and reset PIN end sessions: a request with the old session returns 401.
- The `user.deactivated` event is published.
- Audit entries contain no PIN or password data. Assert this explicitly.
- A concurrent edit returns 409.
- "Terminals used (7 days)" is computed correctly from audit entries.
- The four-printer seed, including converting from the two-printer setup.

**Web:**
- List filtering and search.
- The add form validation.
- The one-time PIN confirmation disappears and can't be shown again.
- The supervisor view hides Admin/Supervisor rows and roles.
- A 409 shows the reload message.

## Acceptance checklist

- [ ] As Administrator on the admin site: add "Grace" (Waiter) with a starting PIN. Grace logs in on a terminal and must change her PIN.
- [ ] Reset edna's PIN while she's logged in on Counter B. Counter B returns to the PIN screen within 2 seconds, and her old PIN no longer works.
- [ ] Deactivate Grace. She can't log in, and her name still shows on any past records.
- [ ] Log in to the admin site as MAU (Supervisor): only waiters and cashiers are visible and editable, and the role list offers only Waiter and Cashier.
- [ ] The last Admin can't be deactivated, and an Admin can't deactivate or demote themselves.
- [ ] `seed --demo` gives four printers, one per terminal, and re-running it on the old database converts it.
- [ ] All tests, lint and CI pass. Push the branch `screen/02-admin-staff` and stop (no merge).

When done, report as `AGENTS.md` describes.
