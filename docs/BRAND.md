# Tausi POS brand

Tausi POS is a product of NexaGen Solutions. The Tausi brand appears only on Tausi's own surfaces: the
splash, the pairing screen, the setup wizard, the small mark on the PIN screen and the admin sign-in,
the admin footer, the About page and the installers. The POS screens, bills and receipts show the
**customer's** name and logo. Receipts never carry the Tausi logo; "Powered by Tausi POS" is text only,
behind the `print.poweredByFooter` setting, which is off by default.

## Colours

All colours live in `web/src/shared/ui/tokens.css`. Components use the semantic token names only, never
colour values, so a colour change is that one file. `tokens.test.ts` checks every text/background pair
against WCAG AA (4.5:1 for text, 3:1 for large text, icons and focus rings).

| Token | Value | Use |
| --- | --- | --- |
| `--color-primary` | teal `#0E6670` | Primary actions, white text (6.7:1) |
| `--color-primary-pressed` | deep teal `#0A4A52` | A pressed primary button |
| `--color-surface-dark` (`--color-topbar`) | deep teal `#0A4A52` | Top bar, dark panels, splash, cream text (8.8:1) |
| `--color-ground-brand` | cream `#F6F1E4` | PIN screen, setup wizard, pairing |
| `--color-ground` | `#F4F4F2` | Admin and POS work screens |
| `--color-panel` | `#FFFFFF` | Panels and cards |
| `--color-accent` (`--color-focus`) | gold `#C9A227` | Active tab underline, focus ring, small badges (dark text) |
| `--color-selected` | blue `#1F5FA8` | Selected and occupied |
| `--color-warning` | orange `#B34A0C` | Needs attention: idle tables, Not printed, failed, differences, Void |

**Gold is never text on cream or white** (2.4:1); the contrast test fails if a stylesheet does it. The
focus ring is 3 px gold with a deep teal edge, so it shows on both light and dark grounds. Orange is
never a primary button.

## Files

The sources are in `brand/`:

| File | What | Where |
| --- | --- | --- |
| `tausi-mark.png` / `tausi-mark.svg` | The mark: the gold peacock head on a deep teal rounded square | **Everything below 64 px**: favicons, app and installer icons, the small mark in the UI (`TausiMark`) |
| `tausi-logo-teal.png` | The full logo: peacock, "Tausi", the gold POS pill, "Simply Outstanding" | **On deep teal only**, 64 px and up: splash, About, installer pictures, README (`TausiLogo`) |

The SVG mark is a traced draft; replace it when a designer's SVG arrives and rebuild.

**Clear space:** keep at least the height of the "POS" pill clear on every side of the full logo.

**Don't:** stretch or squash either picture, recolour the peacock, put the full logo on a light ground,
use the full logo below 64 px, or put any Tausi picture on a bill or receipt.

## Rebuilding the icons

Every icon and installer picture is made from `brand/` by one script. After changing a source file:

```sh
cd tools/brand && npm ci && node build-icons.mjs
node --test icons.test.mjs   # every size is there and every reference points at a real file
```

It writes the web favicons and app icons (`web/public/`), the UI's mark and logo
(`web/src/shared/brand/`), the terminal's Tauri icon set (through `tauri icon`) and NSIS installer
pictures (`terminal/src-tauri/icons/`, `terminal/src-tauri/windows/`), and the server installer's icon,
banner and dialog pictures (`installer/windows/`). Commit what it writes.
