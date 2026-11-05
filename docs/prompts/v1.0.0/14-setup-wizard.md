# Task 14 — First-run setup wizard, imports and licence key

Read `AGENTS.md` first. Use the wireframe **"21 · First-run setup wizard"** in `docs/wireframes/`. Work on a new branch `screen/14-setup-wizard` from `main` (tag `screen-13-v1`).

Do not merge into `main` and do not create tags. Push your branch and stop.

Until now, a business was set up with `seed`. A customer installing Tausi POS needs a guided **first-run wizard** that takes them from an empty database to a business that can trade. It has six steps, following the wireframe:
1. Your business
2. First admin and PIN
3. Terminals and printers
4. Import the menu
5. Opening stock count
6. Licence key

Most steps reuse what's already built. This task adds the wizard itself, **menu import (CSV and from a SambaPOS database)**, and **licence keys**.

**Not in this task:** the Windows installer, running as a service, backups and updates. They come in Task 15. The wizard must work the same however the server was started.

---

## Part A — When the wizard runs, and keeping it safe

**When it shows:**
- The server is in **setup mode** while `Setup.CompletedAt` is empty. That's true of a fresh database after migrations, with no business settings and no users.
- In setup mode:
  - The admin site and every terminal page redirect to `/setup`.
  - Every API except `/api/setup/*`, health and static files returns `423 Setup required`.
- A database that already has users (every existing dev and demo database) is marked **setup complete** by a migration, so nothing changes for it.

**Setup token:**
- On first start in setup mode, the server makes a random **setup token** of 8 characters, grouped as `XXXX-XXXX`.
- It's **printed in the console and the log**, and written to `setup-token.txt` in the data folder.
- The first wizard page asks for it, so nobody else on the network can take over a new install.
- The token expires when setup is completed. Five wrong tries lock the setup for 5 minutes.

**Progress:**
- Each step is saved on the server when it's completed (a `SetupProgress` row).
- Closing the browser and coming back resumes at the next unfinished step.
- **Back** works for every step until Finish.

**Skipping:** steps 3, 4 and 5 can be **skipped** with "I'll do this later", and the Finish page lists what was skipped. Steps 1, 2 and 6 are required, though step 6 can be a trial (Part G).

## Part B — Wizard shell (`/setup`)

Match wireframe 21:
- **Left panel:** dark, with "Tausi POS" and "Set up your business".
  - **The six numbered steps:** the current one highlighted, finished ones with a ✓, and skipped ones marked "Skipped".
  - At the bottom, "You can change all of this later in Settings."
- **Right panel:**
  - "Step n of 6", a title, the form, and the primary button "Next: [next step]" in the bottom right, plus Back.
  - "Skip for now" on the steps that can be skipped.

The wizard is the same React app at a **wide, touch-friendly size**, so it can be done on a counter PC or a laptop. It has no admin sidebar and no PIN pad.

## Part C — Step 1: Your business

The wireframe's form:
- business name
- **type**: Bar / Restaurant / Bar and restaurant
- address
- phone
- **VAT rate** (16% by default, with "prices include VAT")
- **currency** (KES · KSh)
- **logo upload**, with the preview from Task 10
- the **time zone**, which isn't in the wireframe. It defaults to the server's zone, shown as a searchable list.

On Next, these are saved through the Task 10 settings registry, as normal audited setting changes, with "setup" as the user.

**Type defaults:** the type sets sensible defaults that can be changed later.
- **Bar:** the Temperature modifier group (Cold / Warm) is created, and the `print.mode` default is OneButton.
- **Restaurant:** no Temperature group, and a "Kitchen" dispense header suggestion.
- **Bar and restaurant:** both.

## Part D — Step 2: First admin and PIN

- **First admin:** full name, **admin-site username and password** (minimum 10 characters, with a strength hint), and a **4-digit PIN** (entered twice).
  - The user gets the built-in Admin role, with every permission.
  - The env-var bootstrap admin from the foundation (`TAUSI_ADMIN_PASSWORD`) **isn't created** on databases set up through the wizard. Keep it for dev and CI only, and document this.
- **Optional quick staff list** (can be skipped): rows of name and role (Waiter / Cashier / Supervisor), and **+ Add person**.
  - Each person gets a **random PIN** and must change it at first login (Task 1's must-change-PIN).
  - **Print PIN sheet** (A4 PDF) lists name, role and the first PIN, with "Change this PIN at first login" and a cut line between people. Show the PINs on screen **once only**.

## Part E — Step 3: Terminals and printers (can be skipped)

This reuses Task 4, in a simpler layout.

1. **Printers:** **Add printer** with the Task 4 form (name, connection, address, paper width), **Check connection** and **Test print** inline.
2. **Terminals:** **Add terminal** (name, for example "Counter A").
    - Each new terminal shows a **pairing panel**: "On the counter PC, open Tausi POS and enter this server's address: `http://192.168.1.10:5080`. Then type the 6-digit code it shows here."
    - Use the Task 4 pairing.
    - The server's LAN address is detected and shown, with a **copy** button and a list to choose from when there are several network cards.
3. **Routing:** for each terminal, choose the **receipt printer**, the **dispense printer** and the **dispense header**, and set **Sells from** (once Step 5 has made locations; until then, the default location). The Task 4 routing preview is shown.

**The licence's terminal limit applies.** If the trial or key allows fewer terminals, the extra ones are refused with the Task 4 message.

## Part F — Step 4: Import the menu (can be skipped)

Three choices on one screen:

**1. Start from a template CSV:**
- **Download template**, a CSV with the columns: `category, name, short_name, price, price_list_2_price, vat_rate, vat_exempt, modifier_group, erpnext_item_code, active`.
- An example row for each column, and a **Help** panel explaining each one.

**2. Upload a CSV or Excel file** (`.csv` or `.xlsx`, up to 2 MB):
- **Column mapping:** the file's headers are matched automatically by name, and each can be changed with a dropdown.
- **Preview table** of the first 200 rows, each marked ✓ or with an error in orange:
  - missing name
  - price not a number
  - name over 18 characters for the short name (it suggests a cut)
  - duplicate name in the same category
  - unknown VAT rate
- Counts: "212 items ready, 3 with errors, 4 duplicates of existing items (will be skipped)".
- **Import** creates categories, items, prices on the Standard price list (and Price 2 if that column is used), and modifier group assignments by name, in **one transaction**. It's audited, and `menu.changed` is published.
- **Rows with errors are skipped**, and a **download of the skipped rows** with the reason added lets the owner fix them and import again. **Importing again never duplicates:** items are matched by category and name, and only updated when "Update existing items" is ticked.

**3. Import from SambaPOS:** this is the main path for Mau's, which runs SambaPOS 5 on SQL Server.
- **Connection:** the owner enters the SambaPOS **SQL Server connection** (server such as `POSSERVER2\SQLEXPRESS`, database (default `SambaPOS5`), and Windows or SQL login), then **Test connection**.
- **Read only.** Never write to the SambaPOS database. Open the connection with `ApplicationIntent=ReadOnly` and only run `SELECT`s.
- **Check the schema first.** Inspect the SambaPOS schema before mapping, and code defensively: SambaPOS versions differ. Read what's there, and **report what couldn't be read** rather than failing.
- **What to bring across:**
  - **Menu items** with their **group code as the category**, or the screen menu categories if the owner picks that option.
  - **Portions:** an item with one portion becomes one item. An item with several portions becomes one item per portion, named "Item (Portion)", for example "Tusker (Large)".
  - **Prices:** each SambaPOS **price tag** becomes a **price list**. The untagged price is Standard.
  - **Order tag groups** (for example Cold / Warm) become **modifier groups**, with their options, and are assigned to the items they were mapped to where that can be read.
  - **Tables:** entities of the table type become tables, all in one "Main" area that can be renamed later.
  - **Users:** names and roles only, with roles mapped by name to Waiter, Cashier, Supervisor or Admin, or left unmapped for the owner to choose. **Never import PINs or passwords.** New PINs are made and printed on the PIN sheet, as in Step 2.
- **Same preview and confirm flow** as the CSV import, with checkboxes for Menu, Prices, Modifiers, Tables and Users, and the same rule that importing again never duplicates.
- **Saved for the record:** the SambaPOS item ids are kept in an `ExternalRef` column, so a later re-import can match them. Record in DECISIONS.md which SambaPOS tables and columns were used.
- **Tests:** add a **test fixture**, a small SQL Server database created in the integration tests (Testcontainers) with a SambaPOS-like schema and data, and test against it.

**After an import:** a summary reads "Imported 212 items in 9 categories, 2 price lists, 1 modifier group, 20 tables, 8 staff." There's a **Review menu** link to the Task 3 screen, which opens in a new tab.

**Tables without an import:** a quick "Add tables 1 to [16] in [Main]" from Task 5 is offered here too, if no tables came in.

## Part G — Step 5: Opening stock count (can be skipped)

**1. Locations:** Store and Bar, which can be renamed, with an option to add more. This also builds the **locations screen** that was a Task 11 follow-up, at `/admin/stock/locations`.

**2. Stock items from the menu:** a helper lists the imported menu items and suggests a **1:1 stock item and recipe** for bottled items, for example "Tusker Lager sells 1 bottle of Tusker Lager", with a pack such as "crate = 25" that can be edited.
- The owner ticks which suggestions to create.
- Spirits sold in tots are left for the recipe screen, with a hint.

**3. Opening count per location:** the Task 11 **Opening stock** count, in the wizard. Each item takes a quantity per pack and a unit cost. **Print count sheet** is offered here too.

**4. Opening balances (optional):** the Task 12 form, covering Safe, Bank, M-Pesa and what's owed to each supplier.

Approving the opening count here needs no second person. It's the owner's own setup, and that's recorded in the audit log as "setup". After setup, the normal rule (someone other than the counter approves) applies.

## Part H — Step 6: Licence key

**Format:** a licence is a **signed token** that is checked **offline**. No internet is needed at the bar.
- It's a JSON payload: licence id, business name, edition, **max terminals**, issued, **expires** (or `null` for a perpetual licence), and support until.
- It's signed with **Ed25519**.
- It's encoded as one copy-paste key, for example `TAUSI-` + base32 of the payload and signature, grouped in fives.

**Checking a key:**
- The server holds only the **public key**, built in.
- The **private key never goes in this repo.**
- The business name in the key must match the business name in Step 1, ignoring case and spaces. Otherwise the wizard says "This key is for 'X'".

**Issuing keys (separate tool):** add a small separate tool, `tools/Tausi.LicenseTool`, a .NET console app.
- It **issues** keys from a private key file passed as an argument.
- `generate-keypair` creates a key pair, and its README explains keeping the private key offline and backed up.
- CI builds the tool but never runs it with a real key.
- Unit tests use a **test key pair** that only exists in the tests. The server accepts the test public key **only** in the Development environment.

**Trial:**
- If there's no key yet, **Start 30-day trial**: 2 terminals, with every feature.
- A banner in the admin site counts the days: "Trial: 23 days left · Enter licence key".

**The rules.** Never stop a bar in the middle of service:
- **Terminal limit:** enforced at **pairing** (Task 4 already does this). Terminals that are already paired keep working if a new licence has a lower limit. The Terminals page then shows "Over your licence: 4 of 3".
- **Expiry:**
  - From **14 days before** expiry, a banner shows on the admin site and the PIN screen for users with `settings.manage`.
  - After expiry there's a **14-day grace period** with a stronger banner.
  - After the grace period, **selling, printing, settling and closing the day keep working.** Only these are blocked until a key is entered:
    - pairing new terminals
    - the admin pages for Menu, Stock, Reports and Settings, except the licence page
  - The owner can confirm or change this policy. Write it in DECISIONS.md.
- **Clock checks:** a clock set back in time doesn't extend the licence. Store the latest time the server has seen, and use whichever is later.

**Admin page:** **Settings → Licence** shows the edition, business, terminals used of the allowed number, the expiry, support until, and **Enter new key**.

## Part I — Finish

**Summary:**
- What was set up: the business, admins and staff, printers and terminals, the menu counts, stock and balances, and the licence or trial.
- What was **skipped**, each with a link to where it can be done later.

**Finish setup:**
1. Sets `Setup.CompletedAt`.
2. Expires the setup token, and deletes `setup-token.txt`.
3. Writes an audit entry.
4. Takes the owner to the admin site, signed in.
5. Publishes `setup.completed`, so paired terminals reload to the PIN screen.

**After Finish,** `/setup` returns "Setup is already complete". To set up again, run `tausi reset --confirm` on the server, which is dev-only. It's not in the product.

## Part J — Tests

**Server:**
- **Setup mode:**
  - a fresh database is in setup mode, and other APIs return 423
  - databases with users are marked complete by the migration
  - the token is required, and locks for 5 minutes after 5 wrong tries
  - the token expires when setup is finished
  - progress is saved, and setup resumes where it stopped
- **Steps 1 and 2:**
  - the settings are saved and audited as "setup"
  - the type defaults are applied
  - the password and PIN rules
  - staff get random PINs with must-change set
  - the bootstrap admin isn't created on wizard databases
- **CSV and Excel import:**
  - column matching
  - each validation error
  - rows with errors are skipped, and the skipped-rows file is right
  - a second import makes no duplicates
  - "Update existing items"
  - it's one transaction, so a failure part-way imports nothing
  - `menu.changed` is published
- **SambaPOS import, against the fixture database:**
  - items, single and multiple portions, price tags as price lists, order tags as modifiers, tables and users (with no PINs)
  - only SELECTs are run (check the commands sent)
  - a missing table is reported, not crashed on
  - a re-import matches by `ExternalRef`
- **Stock helper:** the 1:1 suggestions, and the opening count approved without a second person during setup only.
- **Licence:**
  - a valid key, a tampered key, a key for the wrong business, and a key signed with the test key outside Development
  - the trial's limits and countdown
  - expiry warnings, the grace period, and after the grace period exactly the listed features are blocked while selling, printing, settling and closing the day still work
  - the terminal limit at pairing, and over-limit terminals keep working
  - a clock set back in time
- **Licence tool:** issuing a key and checking it again (round trip).

**Web:**
- the token page and its lock
- the step list states (current, done, skipped)
- Back, Next and Skip
- resuming after a reload
- each step's validation
- the import mapping, preview errors and counts
- the SambaPOS connection test, its errors, and the preview checkboxes
- the PIN sheet shown once
- the licence entry messages, and the trial and expiry banners
- the Finish summary with its skipped links

## Acceptance checklist

- [ ] Start the server on an **empty** database. The console prints the setup token. The admin site and a terminal both redirect to `/setup`, and the token unlocks it.
- [ ] Step 1: enter "Mau's Village" (Bar, 16%, KES, Africa/Nairobi) with a logo. Step 2: create the owner, and add 4 staff. The PIN sheet prints, with each PIN shown once.
- [ ] Step 3: add a File printer and test print, add "Counter A", and pair a browser terminal with its code.
- [ ] Step 4: import from the SambaPOS fixture database. The preview shows items, two price lists, Cold/Warm, tables and staff. After importing, a second import adds nothing.
- [ ] Step 4 again with a CSV that has 3 bad rows. They're skipped, and the skipped-rows file explains why.
- [ ] Step 5: create stock items from the suggestions, enter an opening count for the Bar, and enter opening balances.
- [ ] Step 6: a key from `Tausi.LicenseTool` for "Mau's Village", 4 terminals, is accepted. A key for another business is refused. "Start 30-day trial" works on a second fresh database.
- [ ] Finish: the summary lists everything, the owner lands in the admin site, and the terminal shows the PIN screen. `/setup` now says it's complete.
- [ ] An expired licence past its grace period still lets a waiter order, print and a cashier settle, but blocks pairing and the Menu admin page.
- [ ] My existing dev database (with users) isn't in setup mode after the migration.
- [ ] All tests, lint and CI pass, including the Windows installer job. Push `screen/14-setup-wizard` and stop.

When done, report as `AGENTS.md` describes, with anything you decided differently from this prompt, and the SambaPOS tables and columns you read.
