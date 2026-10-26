#!/usr/bin/env bash
# Tausi POS Server for Linux: installs or upgrades the server from this folder.
#
#   sudo ./install.sh [--with-postgres] [--port 5080]
#
# --with-postgres  installs PostgreSQL (Ubuntu 22.04 or 24.04) with a database and a random password.
# --port           the port terminals and browsers use (default 5080; an upgrade keeps the current one).
#
# Run the server on the bar's own network. Don't expose port 5080 to the internet.
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROGRAM=/opt/tausi
HOME_DIR=/var/lib/tausi
LOGS=/var/log/tausi
ETC=/etc/tausi
ENV_FILE=$ETC/tausi.env
WITH_POSTGRES=0
PORT=""

while [[ $# -gt 0 ]]; do
  case "$1" in
    --with-postgres) WITH_POSTGRES=1 ;;
    --port) PORT="$2"; shift ;;
    -h|--help) sed -n '2,10p' "$0"; exit 0 ;;
    *) echo "Unknown option: $1" >&2; exit 2 ;;
  esac
  shift
done

say() { printf '\n== %s\n' "$*"; }
die() { printf '\nERROR: %s\n' "$*" >&2; exit 1; }

[[ $EUID -eq 0 ]] || die "Run it as root: sudo ./install.sh"
[[ -x "$HERE/server/TausiServer" ]] || die "server/TausiServer is missing next to install.sh. Unpack the whole tarball."
if [[ -n "$PORT" ]] && ! [[ "$PORT" =~ ^[0-9]+$ && "$PORT" -ge 1 && "$PORT" -le 65535 ]]; then
  die "--port must be a number from 1 to 65535."
fi

UPGRADE=0
[[ -x "$PROGRAM/TausiServer" ]] && UPGRADE=1

# ---- user and folders ----
say "User and folders"
id tausi >/dev/null 2>&1 || useradd --system --home-dir "$HOME_DIR" --shell /usr/sbin/nologin tausi
install -d -o tausi -g tausi -m 0750 "$HOME_DIR" "$HOME_DIR/data" "$HOME_DIR/backups" "$HOME_DIR/config"
install -d -o tausi -g tausi -m 0750 "$LOGS"
install -d -o root -g tausi -m 0750 "$ETC"
install -d -o root -g root -m 0755 "$PROGRAM"

# ---- PostgreSQL ----
if [[ $WITH_POSTGRES -eq 1 ]]; then
  say "PostgreSQL"
  . /etc/os-release
  [[ "${ID:-}" == "ubuntu" && ( "${VERSION_ID:-}" == "22.04" || "${VERSION_ID:-}" == "24.04" ) ]] \
    || echo "Note: --with-postgres is tested on Ubuntu 22.04 and 24.04; this is ${PRETTY_NAME:-unknown}."
  if ! command -v psql >/dev/null 2>&1; then
    apt-get update -q
    DEBIAN_FRONTEND=noninteractive apt-get install -y -q postgresql postgresql-client
  fi
  systemctl enable --now postgresql
  if [[ ! -f "$ENV_FILE" ]] || ! grep -q '^ConnectionStrings__Tausi=' "$ENV_FILE"; then
    PASSWORD="$(head -c 24 /dev/urandom | base64 | tr -dc 'A-Za-z0-9' | head -c 32)"
    if sudo -u postgres psql -tAc "SELECT 1 FROM pg_roles WHERE rolname='tausi'" | grep -q 1; then
      sudo -u postgres psql -q -c "ALTER ROLE tausi WITH LOGIN PASSWORD '$PASSWORD'"
    else
      sudo -u postgres psql -q -c "CREATE ROLE tausi WITH LOGIN PASSWORD '$PASSWORD'"
    fi
    sudo -u postgres psql -tAc "SELECT 1 FROM pg_database WHERE datname='tausipos'" | grep -q 1 \
      || sudo -u postgres psql -q -c "CREATE DATABASE tausipos OWNER tausi"
    CONNECTION="Host=localhost;Port=5432;Database=tausipos;Username=tausi;Password=$PASSWORD"
  fi
fi

# ---- settings ----
say "Settings ($ENV_FILE)"
if [[ ! -f "$ENV_FILE" ]]; then
  cp "$HERE/tausi.env.example" "$ENV_FILE"
fi
if [[ -n "${CONNECTION:-}" ]]; then
  sed -i "s|^ConnectionStrings__Tausi=.*|ConnectionStrings__Tausi=$CONNECTION|" "$ENV_FILE"
  sed -i "s|^Database__Provider=.*|Database__Provider=Postgres|" "$ENV_FILE"
fi
if [[ -n "$PORT" ]]; then
  sed -i "s|^Urls=.*|Urls=http://0.0.0.0:$PORT|" "$ENV_FILE"
fi
chown root:tausi "$ENV_FILE"
chmod 0640 "$ENV_FILE"
grep -q 'Password=CHANGE-ME' "$ENV_FILE" \
  && die "Set the database in $ENV_FILE (ConnectionStrings__Tausi), or run again with --with-postgres."
PORT="$(sed -n 's|^Urls=http://[^:]*:\([0-9]*\).*|\1|p' "$ENV_FILE")"
PORT="${PORT:-5080}"

as_tausi() {
  local vars=()
  mapfile -t vars < <(grep -v -e '^#' -e '^[[:space:]]*$' "$ENV_FILE")
  sudo -u tausi env "${vars[@]}" ASPNETCORE_ENVIRONMENT=Production "$@"
}

# ---- upgrade: a backup first, and the old program kept to roll back to ----
if [[ $UPGRADE -eq 1 ]]; then
  say "Upgrade: backing up first"
  systemctl stop tausi || true
  (cd "$PROGRAM" && as_tausi "$PROGRAM/TausiServer" backup --reason upgrade) \
    || die "The backup before the upgrade failed; nothing was changed. See $LOGS."
  rm -rf "$PROGRAM.previous"
  cp -a "$PROGRAM" "$PROGRAM.previous"
fi

rollback() {
  say "The new version didn't start: going back to the previous one"
  systemctl stop tausi || true
  rm -rf "$PROGRAM"
  mv "$PROGRAM.previous" "$PROGRAM"
  (cd "$PROGRAM" && as_tausi "$PROGRAM/TausiServer" restore --latest-upgrade --yes --no-safety-backup) || true
  systemctl start tausi || true
  die "The upgrade was rolled back; the previous version and its data are back. See $LOGS."
}

# ---- program files ----
say "Program files ($PROGRAM)"
find "$PROGRAM" -mindepth 1 -maxdepth 1 -exec rm -rf {} +
cp -a "$HERE/server/." "$PROGRAM/"
chown -R root:root "$PROGRAM"
chmod 0755 "$PROGRAM/TausiServer"
install -m 0644 "$HERE/tausi.service" /etc/systemd/system/tausi.service

# ---- database ----
say "Database"
if ! (cd "$PROGRAM" && as_tausi "$PROGRAM/TausiServer" migrate); then
  [[ $UPGRADE -eq 1 ]] && rollback
  die "The database isn't reachable or couldn't be updated. Check ConnectionStrings__Tausi in $ENV_FILE."
fi

# ---- firewall: the local network only ----
if command -v ufw >/dev/null 2>&1 && ufw status | grep -q 'Status: active'; then
  say "Firewall (ufw): port $PORT for the local network only"
  for net in 10.0.0.0/8 172.16.0.0/12 192.168.0.0/16; do
    ufw allow from "$net" to any port "$PORT" proto tcp comment 'Tausi POS Server' >/dev/null
  done
fi

# ---- start ----
say "Starting the service"
systemctl daemon-reload
systemctl enable tausi >/dev/null
systemctl restart tausi
if ! (cd "$PROGRAM" && as_tausi "$PROGRAM/TausiServer" wait-health --timeout 120); then
  [[ $UPGRADE -eq 1 ]] && rollback
  die "The service didn't start. See: journalctl -u tausi and $LOGS."
fi
[[ $UPGRADE -eq 1 ]] && rm -rf "$PROGRAM.previous"
# A new install writes its setup token just after it starts.
if [[ $UPGRADE -eq 0 ]]; then
  for _ in $(seq 1 30); do [[ -f "$HOME_DIR/setup-token.txt" ]] && break; sleep 1; done
fi

say "Done"
echo "Tausi POS Server is running. Terminals connect to:"
for ip in $(hostname -I); do
  [[ "$ip" == *:* ]] || echo "    http://$ip:$PORT"
done
if [[ -f "$HOME_DIR/setup-token.txt" ]]; then
  echo
  echo "Open http://<this server>:$PORT/setup in a browser and enter the setup token:"
  echo "    $(cat "$HOME_DIR/setup-token.txt")"
fi
echo
echo "Give this PC a fixed IP address, so terminals always find it."
echo "Run the server on the bar's own network. Don't expose port $PORT to the internet."
