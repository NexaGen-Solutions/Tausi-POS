#!/usr/bin/env bash
# The release's update files, in dist/:
#   updates.json                       NexaGen's update feed (servers check it from Settings → System)
#   TausiPOS-Server-<v>.tausiupdate    the Windows server update for a USB drive (update.json + the installer)
#   SHA256SUMS.txt                     checksums of every file
# Server updates are signed with TAUSI_UPDATE_SIGNING_KEY (base64 private key); without it they are
# marked unsigned and servers refuse them.
#   scripts/release-files.sh 1.2.0 dist https://updates.nexagensolutions.com/tausi
set -euo pipefail
VERSION="$1"
DIST="$(cd "$2" && pwd)"
BASE="${3%/}/$VERSION"
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
WIN="TausiPOS-Server-Setup-$VERSION.exe"
LINUX="tausi-server-$VERSION-linux-x64.tar.gz"

sign() { # file → JSON {version, sha256, signature}
  if [[ -n "${TAUSI_UPDATE_SIGNING_KEY:-}" ]]; then
    local key; key="$(mktemp)"; printf '%s' "$TAUSI_UPDATE_SIGNING_KEY" > "$key"
    dotnet run --project "$ROOT/tools/Tausi.LicenseTool" -c Release -- sign-update "$key" "$1" "$VERSION"
    rm -f "$key"
  else
    printf '{"version":"%s","sha256":"%s","signature":null}\n' "$VERSION" "$(sha256sum "$1" | cut -d' ' -f1)"
  fi
}

WIN_SIGNED="$(sign "$DIST/$WIN" | tail -1)"
LINUX_SIGNED="$(sign "$DIST/$LINUX" | tail -1)"
MIN_TERMINAL="$(grep -oP 'public const string Minimum = "\K[^"]+' "$ROOT/server/src/Tausi.Application/Updates/Updates.cs")"
NOTES="$("$ROOT/scripts/changelog.sh" "$VERSION" --notes-only)"

python3 - "$DIST" "$VERSION" "$BASE" "$WIN" "$LINUX" "$WIN_SIGNED" "$LINUX_SIGNED" "$MIN_TERMINAL" "$NOTES" <<'PY'
import json, os, sys, zipfile, datetime
dist, version, base, win, linux, win_signed, linux_signed, minimum, notes = sys.argv[1:]
win_signed, linux_signed = json.loads(win_signed), json.loads(linux_signed)
def asset(name, signed):
    return {"url": f"{base}/{name}", "sha256": signed["sha256"], "signature": signed["signature"],
            "size": os.path.getsize(os.path.join(dist, name))}
feed = {"version": version, "minimumTerminalVersion": minimum,
        "publishedAt": datetime.datetime.now(datetime.timezone.utc).isoformat(), "notes": notes,
        "server": {"windows-x64": asset(win, win_signed), "linux-x64": asset(linux, linux_signed)}}
json.dump(feed, open(os.path.join(dist, "updates.json"), "w"), indent=2)
with zipfile.ZipFile(os.path.join(dist, f"TausiPOS-Server-{version}.tausiupdate"), "w", zipfile.ZIP_STORED) as z:
    z.write(os.path.join(dist, win), win)
    z.writestr("update.json", json.dumps({"version": version, "platform": "windows-x64", "file": win,
                                          "sha256": win_signed["sha256"], "signature": win_signed["signature"], "notes": notes}))
PY
[[ -n "${TAUSI_UPDATE_SIGNING_KEY:-}" ]] || echo "No TAUSI_UPDATE_SIGNING_KEY: the server updates are UNSIGNED (servers won't install them)."
(cd "$DIST" && sha256sum -- * | grep -v SHA256SUMS > SHA256SUMS.txt)
cat "$DIST/SHA256SUMS.txt"
