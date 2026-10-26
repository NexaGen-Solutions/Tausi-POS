#!/usr/bin/env bash
# Makes tausi-server-<version>-linux-x64.tar.gz from a linux-x64 server publish:
#   ./make-tarball.sh 1.2.0 ../../out/server-linux ../../out
set -euo pipefail
VERSION="$1"
PUBLISH="$(cd "$2" && pwd)"
OUT="$(mkdir -p "$3" && cd "$3" && pwd)"
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$HERE/../.." && pwd)"
NAME="tausi-server-$VERSION-linux-x64"
STAGE="$(mktemp -d)"
mkdir -p "$STAGE/$NAME/server"
cp -a "$PUBLISH/." "$STAGE/$NAME/server/"
cp "$HERE/install.sh" "$HERE/tausi.service" "$HERE/tausi.env.example" "$HERE/README.txt" "$STAGE/$NAME/"
cp "$ROOT/LICENSE" "$ROOT/THIRD-PARTY-NOTICES" "$STAGE/$NAME/"
chmod 0755 "$STAGE/$NAME/install.sh" "$STAGE/$NAME/server/TausiServer"
tar -C "$STAGE" -czf "$OUT/$NAME.tar.gz" "$NAME"
rm -rf "$STAGE"
echo "$OUT/$NAME.tar.gz"
