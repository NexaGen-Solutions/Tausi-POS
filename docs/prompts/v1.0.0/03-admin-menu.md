# Task 3 — Admin: Menu and prices

Read `AGENTS.md` first. Use the wireframe **"10 · Admin: menu and prices"** in `docs/wireframes/`. Work on a new branch `screen/03-admin-menu` from `main` (tag `screen-02-v1`).

Do not merge into `main` and do not create tags. Push your branch and stop.

---

## Part A — Small fix: CI actions

Update the GitHub Actions to versions that don't use the retiring Node 20 runtime (`actions/checkout`, `actions/setup-node`, `actions/setup-dotnet`, and any others that warn). CI must stay green.

## Part B — Data model

Add these entities, with migrations for both providers:

- **Category:** name (unique), sort order, active.
- **MenuItem:**
    - name (unique, 2–60 characters)
    - **short name** (max 18 characters; used on buttons and printed slips; defaults to the first 18 characters of the name)
    - category
    - sort order
    - **VAT rate:** nullable, so it uses the business default; set it to 0 for exempt items
    - active
    - **ERPNext item code** (optional, for the later sync)
    - row version
- **PriceList:** name (unique), active, is-default.
    - Seed **Standard** (default) and **Price 2**.
    - Exactly one default at any time, enforced in the Application layer and by a filtered unique index.
- **MenuItemPrice:** menu item, price list, price (decimal, **including VAT**), unique per item and list.
    - An item has a price on the default list (required) and optionally on others.
    - If an item has no price on the active list, the terminal uses the default list price.
- **ModifierGroup:** name (e.g. "Temperature"), min select, max select (min ≤ max, max ≥ 1), sort order, active.
- **ModifierOption:** group, name (e.g. "Cold"), price add-on (decimal, incl. VAT, default 0), sort order, active.
- **ModifierGroupAssignment:** group, plus **either** a category **or** a menu item. An item gets the groups of its category plus its own.
- **PriceChangeLog:**
    - item, price list, old price, new price
    - changed by, approved by (nullable), terminal or "admin site", time
    - source (`admin` | `import`)
- **Terminal.PriceListId:** nullable. A terminal uses this list instead of the default when set. Editing it comes with the Terminals screen; add only the field and API support now.

Prices on order lines are **copied** onto the line when ordered, so changing a menu price never alters existing tickets. Write this rule down in `DECISIONS.md` now. The order line itself comes in the order-screen task.

## Part C — Permissions (server-enforced)

| Action | Permission |
| --- | --- |
| View the menu admin | `menu.edit` or `menu.edit_prices` |
| Create, edit, deactivate categories, items, option groups, price lists | `menu.edit` |
| Change any price, or a modifier price add-on | `menu.edit_prices` |
| Import CSV | `menu.edit`, plus `menu.edit_prices` if the file changes prices |

- **Read-only prices:** a user with `menu.edit` but not `menu.edit_prices` sees prices as read-only.
- **No price fields from the wrong user:** the server rejects any price field sent by them, even unchanged ones. Simplest rule: price fields are only accepted on the dedicated price endpoint.
- **`NeedsApproval` support:** if `menu.edit_prices` is `NeedsApproval` for a role, the existing approval token flow applies, and the log records the approver.
- **Audit:** every change is audited. Price changes **also** write PriceChangeLog rows.

## Part D — Menu admin screen (`/admin/menu`)

Match wireframe 10.

**Top bar:**
- the title "Menu and prices"
- **Import CSV**
- **Export CSV**
- **Option groups**
- **Price lists**
- primary **Add item**

**Category chips:** All, then each category in sort order, plus "+ Category". Categories can be drag-reordered in a small "Manage categories" panel.

**Items table:**
- **Columns:** Name (with the short name underneath in small grey text), Category, one price column **per active price list** (Standard, Price 2…), Options (group names), Active, Edit.
- **Searching:** search by name.
- **Sorting:** by category order, then item sort order.

**Add / edit item** (right-side panel):
- **Fields:** name, short name (with a live character count out of 18), category, VAT rate ("Business default (16%)" or a custom rate or "Exempt (0%)"), active.
- **Prices:** one field per price list. Below each price, a live line shows **"Net KSh 215.52 · VAT KSh 34.48"** using the foundation's VatCalculator.
- **Option groups:** checkboxes for the item's own groups, plus a read-only note listing the groups inherited from its category.
- **ERPNext item code** (optional).
- **Saving:** non-price changes and price changes are saved as separate API calls, so the permissions stay clean. The UI makes this invisible: one Save button.

**Option groups panel:**
- list and add or edit groups: name, min/max, options with price add-ons and order
- assign groups to categories or items
- preview line: "Tusker Lager will ask: Temperature (choose 1)"

**Price lists panel:** add, rename, deactivate, and set the default.

**Deleting:** nothing is hard-deleted. Categories, items, groups, options and lists are **deactivated**.
- Deactivating a category with active items asks to also deactivate them.
- Inactive items don't show on terminals.

## Part E — Price changes and terminals

1. **`PUT /api/admin/menu/items/{id}/prices`** takes `{ priceListId: price }`.
    - Write one PriceChangeLog row per changed price.
    - Publish `menu.changed`.
    - Reject negative prices.
    - Allow 0 only with a confirmation flag (`allowZero: true`), because a free item is usually a mistake.
2. **Terminal menu endpoint:** `GET /api/pos/menu` returns the active categories, items with **the price for that terminal's price list** (falling back to the default), short names, effective VAT rate and effective option groups. It also returns a `menuVersion` (a hash or counter).
3. **Live refresh:** the terminal app caches the menu and refetches when `menu.changed` arrives or `menuVersion` differs. Show it on the POS placeholder page from Screen 1 as a simple read-only list, "Menu (for testing)", so it can be checked before the order screen exists. Mark this test list to be removed in the order-screen task.

## Part F — CSV import and export

**Export:** `GET /api/admin/menu/export.csv` with columns `category,name,short_name,vat_rate,active,<one column per price list name>,option_groups,erpnext_item_code`.

**Import (from SambaPOS or edited exports):**
1. **Upload** a CSV with at least `category,name,price`. The other columns from the export are optional, and `price2` maps to the "Price 2" list.
2. **Map columns:** if the headers don't match, show a mapping step where each CSV column is matched to a field.
3. **Preview** a table of what will happen:
    - **New**, **Updated** (showing old → new values, with price changes highlighted), **Unchanged** and **Errors** (with the reason per row)
    - new categories that will be created
4. **Import options:** "Update existing items matched by name" (on by default) and "Deactivate items not in the file" (off by default).
5. **Apply** runs everything in one transaction, writes PriceChangeLog rows with source `import`, and audits a summary ("Import: 12 new, 30 updated, 4 prices changed").
6. **Encoding and number formats:** UTF-8 and Windows-1252 files both work, since SambaPOS exports may be either. Numbers like `1,200.00` and `1200` both parse.

## Part G — Demo menu seed

Add `seed --demo-menu` (also run by `seed --demo` when the menu is empty):

**Categories:** Beers, Spirits, Soft drinks, Water & juice, Pool, Food.

**Items** (Standard price, KSh):

| Item | Category | Price |
| --- | --- | --- |
| Tusker Lager | Beers | 250 |
| Guiness | Beers | 300 |
| White Cap Lager | Beers | 280 |
| Balozi Lager | Beers | 250 |
| Heineken | Beers | 300 |
| Faxe Can | Beers | 175 |
| Viceroy 375ml | Spirits | 900 |
| Gilbeys 350ml | Spirits | 900 |
| Sodas 300ml | Soft drinks | 100 |
| Mau's Water 1L | Water & juice | 100 |
| Pineapple Punch | Water & juice | 270 |
| Delmonte | Water & juice | 400 |
| Pool Table | Pool | 40 |

- **Option group:** "Temperature", min 1, max 1, options Cold and Warm at 0, assigned to **Beers** and **Soft drinks**.
- **Price 2:** leave it empty for now.

## Part H — Tests

**Server:**
- Permissions: `menu.edit` can't change prices (the price endpoint is forbidden, and price fields are rejected elsewhere). `menu.edit_prices` with `NeedsApproval` needs a token, and the log records the approver.
- Price changes write PriceChangeLog rows and publish `menu.changed`. Negative prices are rejected, and 0 needs the flag.
- Only one default price list. A terminal's list overrides the default, and missing prices fall back to the default.
- Effective option groups (category + item) and the min/max validation.
- Menu endpoint: prices per terminal, inactive items excluded, and `menuVersion` changes after edits.
- CSV import:
    - header mapping
    - the preview counts
    - updates matched by name, case-insensitive
    - "deactivate missing"
    - one transaction (a bad row with "skip errors" off rolls everything back)
    - Windows-1252 input
    - `1,200.00` parsing
    - PriceChangeLog rows with source `import`
- An export → import round trip leaves the menu unchanged.
- VAT preview values: 250 at 16% → net 215.52, VAT 34.48.

**Web:**
- The item form: the short-name counter, the live net/VAT line, and prices read-only without `menu.edit_prices`.
- The import preview rendering and the apply confirmation.
- The terminal test list updates after a `menu.changed` event.

## Acceptance checklist

- [ ] `seed --demo` creates the demo menu. `/admin/menu` shows it grouped by category, with Standard and Price 2 columns.
- [ ] Change Tusker Lager to 260 as Administrator. The Counter A terminal's test menu list shows 260 within 2 seconds, and the change appears in the price log.
- [ ] Give MAU's role `menu.edit` but not `menu.edit_prices` (via the API for now). MAU can rename an item but can't change any price, including through a crafted request.
- [ ] Export the CSV, change two prices and add one item in a spreadsheet, and import it. The preview shows exactly 1 new and 2 updated with old → new prices, and applying it works.
- [ ] Import a SambaPOS-style CSV with `price2`, and the Price 2 column fills.
- [ ] Set Counter B to use Price 2 (via the API). Counter B's test list shows Price 2 where set and Standard elsewhere.
- [ ] Deactivating a category offers to deactivate its items, and they disappear from terminals.
- [ ] All tests, lint and CI pass, with no Node 20 warnings. Push `screen/03-admin-menu` and stop.

When done, report as `AGENTS.md` describes.
