# Task B1 — Tausi POS brand: logo, icons, teal colours, and no customer names

Read `AGENTS.md` first. It has been updated: there are new **colour tokens**, a **brand assets** rule, and a rule against **customer names in the product**. Use the updated wireframes `docs/wireframes/tausi-pos-wireframes.pdf`, which now show the teal colours and generic names.

Work on a new branch `brand/b1-tausi-brand` from `main` (tag `release-15-v2`).

Do not merge into `main` and do not create tags. Push your branch and stop.

This task applies the Tausi POS brand from NexaGen Solutions and makes the product **customer-neutral**. It changes **no behaviour**: only colours, images, icons, text and demo data. Every test that passed before must still pass, apart from golden files that contain the renamed demo data (Part D), which are regenerated.

---

## Part A — Brand assets

The owner provides three files. Commit them to `brand/` as they are:
- `tausi-logo-teal.png`: the full logo (peacock in a cream circle, "Tausi", the gold "POS" pill and "Simply Outstanding") on deep teal, 2400×819.
- `tausi-mark.png`: the simplified mark (a gold peacock head on a deep teal rounded square), 512×512 with transparent corners.
- `tausi-mark.svg`: a traced vector of the mark. It's a draft, so use it where vectors help (favicon, web UI), and replace it if the owner later supplies a designer's SVG.

**Generate the derived files** with a script (`tools/brand/build-icons`, Node with `sharp`, or `tauri icon`), so they can be remade whenever the source changes. Commit the outputs:
- **Web:** `favicon.ico` (16, 32, 48), `favicon.svg` (from the mark), `apple-touch-icon.png` (180), and `icon-192.png` / `icon-512.png`. Put them in `web/public/`, and link them in the HTML head of the admin site and the terminal UI.
- **Terminal app:** the full Tauri icon set (`icons/` for Windows `.ico`, PNG sizes, and so on) from `tausi-mark.png`.
- **Server installer (WiX):** the `.ico` for Add/Remove Programs, a **banner** (493×58: deep teal, with the mark and "Tausi POS" in cream on the left) and a **dialog** background (493×312: deep teal panel on the left with the full logo scaled to fit, cream on the right where WiX draws its text).
- **Terminal installer:** the matching icon and, if the installer type supports it, the header and sidebar images, in the same style.
- **`web/src/shared/brand/`:** `TausiMark.tsx` (inline SVG component, sized by prop) and `TausiLogo.tsx` (the full logo image, `<img>` with alt "Tausi POS"), so screens use components rather than file paths.

**Small sizes:** below 64 px, **always use the mark**, never the full logo.

## Part B — Colour tokens

Update `web/src/shared/ui/tokens.css` to the tokens in `AGENTS.md`:

| Token | Old | New |
| --- | --- | --- |
| Primary action | orange `#B34A0C` | **teal `#0E6670`**, pressed `#0A4A52` |
| Top bar and dark panels | `#1C1C1C` | **deep teal `#0A4A52`** |
| Ground on the PIN screen, setup wizard, splash and pairing | `#F4F4F2` | **cream `#F6F1E4`** |
| Accent (active tab underline, focus ring, small badges) | — | **gold `#C9A227`** |
| Warning, needs attention | `#B34A0C` | **stays orange `#B34A0C`** |

**Go through every use of the old primary colour.** Each one becomes either **primary** (teal) or **warning** (orange). Examples:
- **Teal:** Print, Settle, Complete, Save, Next, Open ticket, Approve, Add.
- **Orange stays:** idle table tiles and their legend, "Not printed · Retry", Failed pills, count and cash-up differences, the Void button's outline, offline and "day isn't open" banners, and the "Over your licence" warning.

**Rules:**
- **Never use colour names in components.** Use semantic tokens only (`--color-primary`, `--color-warning`, `--color-surface-dark` and so on), so a future colour change is one file.
- **Focus rings:** gold `#C9A227`, 3 px, on every focusable element, and clearly visible on both teal and white.
- **Selected and occupied** stay blue `#1F5FA8`, so a selected item never looks like a button.
- **Contrast:** add a unit test that checks every text/background token pair used by the UI meets **WCAG AA**: 4.5:1 for text and 3:1 for large text and icons. Teal with white text is 6.7:1, and deep teal with cream is 8.8:1. **Gold is never used for text on cream or white**, and the test enforces it.

## Part C — Where the Tausi brand appears

The **customer's** name and logo stay on the POS screens, bills and receipts, as built. The Tausi brand appears only on Tausi's own surfaces:

1. **Splash screen** (terminal app, while it starts and connects): deep teal, the full logo centred at up to 60% of the width, and "Connecting to [server]…" in cream underneath.
2. **Pairing screen:** deep teal on the left with the mark and "Tausi POS". The right side has the server address and the 6-digit code on cream.
3. **PIN screen:** the left panel is deep teal.
   - The **customer's business name stays the large text**, with their logo if one is set.
   - Above it is a small mark (36 px) with "TAUSI POS" in cream, as in wireframe 1.
   - The right side (the PIN pad) is on cream.
   - The Enter button is teal.
4. **Setup wizard:** the left panel is deep teal, with the mark and "Tausi POS" at the top, as in wireframe 21. The current step's number circle is gold, with deep teal text. The right side is cream.
5. **Admin site:**
   - The sidebar header keeps the **customer's** logo and name.
   - The **footer line** becomes the 20 px mark + "Tausi POS · by NexaGen Solutions · version x".
   - The **admin sign-in page** shows the customer's logo and name, and a small "Tausi POS" mark under the form.
6. **About** (from the admin footer and the terminal's setup screen): the full logo, version, "© NexaGen Solutions", nexagensolutions.com, the licence summary, and a link to the third-party notices.
7. **Installers:** the images from Part A, and the product name "Tausi POS" with publisher "NexaGen Solutions".
8. **Receipts:** **no Tausi logo.** "Powered by Tausi POS" stays **text only**, behind `print.poweredByFooter` (off by default). Confirm the default is still off.

## Part D — No customer names in the product

Tausi POS is sold to many businesses. The first site was a case study, and its names must not appear in the product. Replace them in **code, UI text, seed data, tests, golden files, fixtures and current docs**:

| Old | New |
| --- | --- |
| Business "Mau's Village" | **"Demo Bar"** |
| Menu item "Mau's Water 1L" | **"House Water 1L"** |
| Staff "MAU" (Supervisor) | **"SAM"** |
| Server name "POSSERVER2" | **"POS-SERVER"** |
| Any other customer-specific wording | Generic wording, for example "a bar with four counters" |

**Mechanics:**
- Regenerate the golden slip files, screenshots and the wireframe PDF reference that change because of the new demo names. Check each diff is only a name change.
- `seed --demo` creates "Demo Bar". **Existing databases are not renamed**, because business data belongs to the customer and isn't touched.
- **Don't edit** `prompts/` or past entries in `docs/DECISIONS.md`. They're history. Add one DECISIONS entry: "Product made customer-neutral; demo business is 'Demo Bar'."
- **Rename** the docs from Task 16 if they exist (none should yet): `PILOT-MAUS.md` → `PILOT.md`.
- **Add a CI check:** a test that searches the repo, excluding `prompts/`, `docs/DECISIONS.md` and `.git`, for `mau's`, `mau village`, `POSSERVER2` and the whole word `MAU` (case-insensitive for the phrases). It fails with the file and line for any match.

## Part E — Wireframe and doc updates

- **Replace** `docs/wireframes/` PDFs with the new `tausi-pos-wireframes.pdf` the owner commits, and delete the old one.
- **Docs:** update the README with the logo at the top (`brand/tausi-logo-teal.png`), one line, "Tausi POS by NexaGen Solutions. Simply Outstanding.", and the generic description.
- **`docs/BRAND.md`** (new, short):
  - the colour tokens with their hex values
  - which file to use where (the mark below 64 px, the full logo on teal only)
  - clear space around the logo: at least the height of the "POS" pill
  - don'ts: no stretching, no recolouring the peacock, no logo on receipts
  - how to rebuild the icons

## Part F — Tests

- **Contrast:** the token pairs meet WCAG AA, and gold-on-light text is refused.
- **Icons:** the build script produces every required size, and the Tauri config and the HTML head reference files that exist.
- **No customer names:** the CI check passes, and it fails on a planted `Mau's` in a test fixture, as a self-test.
- **Seed:** `seed --demo` creates "Demo Bar", "House Water 1L" and "SAM", and an existing database keeps its name.
- **Visual check:** a Playwright script captures the PIN screen, table map, order screen, settle screen, setup wizard step 1, pairing screen, splash, and an admin page in the new colours, and saves them as CI artifacts for the owner to look at. They aren't pixel-compared, just reviewed.
- **No behaviour changes:** all the existing server and web tests pass unchanged, apart from the regenerated golden files.

## Acceptance checklist

- [ ] The terminal app's taskbar and window icon is the gold peacock mark. The splash shows the full logo on deep teal.
- [ ] The PIN screen matches wireframe 1: a deep teal panel, a small Tausi mark, **the customer's** business name large, the PIN pad on cream, and a teal Enter button.
- [ ] The table map has a deep teal top bar, teal "Open ticket", blue occupied tiles, and orange idle tiles. Primary buttons are never orange.
- [ ] The order screen has a teal "Print (n new)" and an orange-outlined Void. "Not printed · Retry" is orange.
- [ ] The setup wizard and pairing screen show the Tausi brand as described.
- [ ] The admin site footer reads "Tausi POS · by NexaGen Solutions · version x" with the mark, and the browser tab shows the favicon.
- [ ] Both installers show the Tausi icon and images, with publisher "NexaGen Solutions".
- [ ] A receipt has no Tausi logo, and "Powered by Tausi POS" appears only when the setting is on.
- [ ] `seed --demo` gives "Demo Bar". The customer-name CI check passes, and nowhere outside `prompts/` and DECISIONS history mentions the case-study customer.
- [ ] The contrast test passes, and `docs/BRAND.md` exists.
- [ ] All tests, lint and CI pass, including the Windows installer jobs. Push `brand/b1-tausi-brand` and stop.

When done, report as `AGENTS.md` describes, with links to the screenshot artifacts so I can review the new look.
