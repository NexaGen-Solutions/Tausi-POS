#!/usr/bin/env bash
# Release notes from the conventional commits since the last v* tag, and CHANGELOG.md with them on top.
#   scripts/changelog.sh 1.2.0              prints the notes and updates CHANGELOG.md
#   scripts/changelog.sh 1.2.0 --notes-only prints the notes only
# A version that already has a hand-written "## 1.2.0" entry in CHANGELOG.md (1.0.0 does) keeps it: that
# entry is printed as the notes and CHANGELOG.md isn't changed.
set -euo pipefail
VERSION="$1"
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"
if [[ -f CHANGELOG.md ]] && grep -q "^## $VERSION\b" CHANGELOG.md; then
  sed -n "/^## $VERSION\b/,/^## /{/^## /d;p}" CHANGELOG.md | sed -e :a -e '/^\n*$/{$d;N;ba' -e '}'
  exit 0
fi
PREVIOUS="$(git describe --tags --abbrev=0 --match 'v*' HEAD^ 2>/dev/null || true)"
RANGE="${PREVIOUS:+$PREVIOUS..}HEAD"

section() { # type, title
  local lines
  lines="$(git log --no-merges --pretty='%s' $RANGE | grep -E "^$1(\([^)]*\))?!?: " | sed -E "s/^$1(\(([^)]*)\))?!?: /- **\2** /; s/- \*\*\*\* /- /" || true)"
  [[ -n "$lines" ]] && printf '### %s\n\n%s\n\n' "$2" "$lines"
  return 0
}

NOTES="$(section feat 'New'; section fix 'Fixed'; section perf 'Faster')"
[[ -n "$NOTES" ]] || NOTES="Maintenance release."
if [[ "${2:-}" == "--notes-only" ]]; then
  printf '%s\n' "$NOTES"
  exit 0
fi

HEADER="# Changelog

Tausi POS by NexaGen Solutions. Each release's changes, newest first (built by the release workflow from the commit messages).
"
ENTRY="## $VERSION ($(date -u +%Y-%m-%d))

$NOTES
"
OLD=""
[[ -f CHANGELOG.md ]] && OLD="$(sed '1,/^## /{/^## /!d}' CHANGELOG.md)"
printf '%s\n%s\n%s\n' "$HEADER" "$ENTRY" "$OLD" > CHANGELOG.md
printf '%s\n' "$NOTES"
