# Running Tausi POS

Everything except the Windows terminal installer builds and runs on Ubuntu.

## 1. What you need

| Tool | Version | Used for |
| --- | --- | --- |
| .NET SDK | 10.0 | Tausi Server and its tests |
| Node.js | 24 (with npm) | Web UI, Tauri CLI |
| Docker | any recent | Development databases and the integration tests |
| Rust | stable (via rustup) | Only for the terminal app |
| Tauri system libraries | see below | Only for the terminal app |

Tauri on Ubuntu needs:

```bash
sudo apt install libwebkit2gtk-4.1-dev build-essential curl wget file libxdo-dev \
  libssl-dev libayatana-appindicator3-dev librsvg2-dev
```

## 2. From a clean clone

```bash
git clone <repo> tausi-pos && cd tausi-pos

# Database (PostgreSQL 16 on localhost:5432, database tausipos, user tausi / tausi_dev)
docker compose -f docker-compose.dev.yml up -d

# Web UI: install and build once (the server serves web/dist in development)
cd web && npm ci && npm run build && cd ..

# Server: tools, demo data, then run
cd server
dotnet tool restore
export TAUSI_ADMIN_PASSWORD='choose-a-dev-password'
dotnet run --project src/Tausi.Api -- seed --demo
dotnet run --project src/Tausi.Api
```

The seed prints each user's 4-digit PIN **once**. Write them down. Running it again changes
nothing and prints no PINs. To start over, drop the database
(`docker compose -f docker-compose.dev.yml down -v`) and seed again.

| User | Role | Signs in with |
| --- | --- | --- |
| Administrator | Admin | PIN, and the admin site as `admin` with `TAUSI_ADMIN_PASSWORD` |
| MAU | Supervisor | PIN |
| CAROL | Cashier | PIN |
| edna, CYNTHIA, Ruth, WAMBOO | Waiter | PIN |

The server listens on <http://localhost:5080>. It applies database migrations and the base seed
(roles, permissions, ledger accounts) by itself on start.

### Web UI with hot reload

```bash
cd web && npm run dev     # http://localhost:5173, proxies /api, /hubs and /files to :5080
```

## 3. Try it

### Pair the browser as a terminal

1. Open <http://localhost:5173/pos> (or `:5080/pos`). A browser that is not paired shows a
   6-digit **pairing code**.
2. An admin enters the code. The admin screen for this comes in a later task; for now use the API:

   ```bash
   curl -c /tmp/nx.cookies -H 'content-type: application/json' \
     -d "{\"userName\":\"admin\",\"password\":\"$TAUSI_ADMIN_PASSWORD\"}" \
     http://localhost:5080/api/admin/login
   curl -b /tmp/nx.cookies -H 'content-type: application/json' \
     -d '{"code":"123456","terminalName":"Counter A"}' \
     http://localhost:5080/api/terminals/pair
   ```

3. Within two seconds the browser moves on to the PIN pad for **Counter A**. Log in with a PIN
   from the seed (tap the keypad or type it; it submits by itself at 4 digits).
4. If no day is open, MAU (Supervisor) or Administrator gets **Open the day**; a waiter is told to
   ask a supervisor. Once the day is open you reach the table map placeholder. Other terminals'
   PIN screens show "Work period open since …" straight away.

The device token is kept in the browser's `localStorage` (`tausi.device`). Clear it to pair again.

### Staff and PINs (admin site)

Sign in to <http://localhost:5173/admin> as `admin` and open **Staff and PINs**:

- **Add staff** with a starting PIN. The PIN is shown once; the person must choose their own at
  first login (entered twice; 1234-style PINs and other people's PINs are refused).
- **Reset PIN** (type one or press Generate): the person is logged out on every terminal at once.
- **Deactivate / Reactivate**: nobody is ever deleted.
- To sign in as a supervisor, edit MAU and set an admin-site password, then sign in as `mau`.
  Supervisors see and manage only waiters and cashiers.

From the command line (development), a user can also be made to choose a new PIN at their next
login:

```bash
dotnet run --project src/Tausi.Api -- require-pin-change edna
```

### Idle logout

Terminals log out after 10 minutes without use. To try it quickly, set it to 1 minute (admin
cookie from the pairing step):

```bash
curl -s -b /tmp/nx.cookies http://localhost:5080/api/settings/business > /tmp/settings.json
# edit otherJson in /tmp/settings.json to {"pos":{"idleLogoutMinutes":1,"logoutAfterSettle":false}}
curl -b /tmp/nx.cookies -X PUT -H 'content-type: application/json' --data @/tmp/settings.json \
  http://localhost:5080/api/settings/business
```

### Menu and prices

`seed --demo` adds the demo menu when the menu is empty; `seed --demo-menu` adds only the menu.
Open **Menu and prices** on the admin site to edit items, prices (with the price log), option
groups and price lists, and to import or export CSV. A logged-in terminal shows a temporary
"Menu (for testing)" list under the table-map placeholder; it refreshes when the menu changes.

For now these are API only (admin cookie from the pairing step):

```bash
# A terminal sells from another price list (null: back to the default)
curl -b /tmp/nx.cookies -X PUT -H 'content-type: application/json' \
  -d '{"priceListId":"<price list id>"}' http://localhost:5080/api/admin/terminals/<terminal id>/price-list
# Change one cell of the permission grid, e.g. give Supervisors menu.edit
curl -b /tmp/nx.cookies -X PUT -H 'content-type: application/json' \
  -d '{"permissionKey":"menu.edit","value":"Allowed"}' http://localhost:5080/api/admin/roles/<role id>/permissions
```

Ids: price lists in `GET /api/admin/menu`, terminals in `GET /api/admin/terminals`, roles in
`GET /api/admin/staff` (`roleId`, `assignableRoles`).

### Admin site

<http://localhost:5173/admin>: sign in as `admin`. All sidebar items are present; the pages
come in later tasks.

### Lockout, approvals and idempotency (with curl)

```bash
D=<device token from localStorage 'tausi.device'>
# 5 wrong PINs within 2 minutes: the 5th answers 429 and the terminal is locked for 60 s.
curl -H "X-Device-Token: $D" -H 'content-type: application/json' -d '{"pin":"0000"}' http://localhost:5080/api/auth/pin

# Approval flow (temporary dev-only endpoint guarded by order.void):
T=$(curl -s -H "X-Device-Token: $D" -H 'content-type: application/json' -d '{"pin":"<edna PIN>"}' \
  http://localhost:5080/api/auth/pin | jq -r .token)
curl -X POST -H "X-Device-Token: $D" -H "Authorization: Bearer $T" http://localhost:5080/api/dev/void-probe
#   -> 403 with "approvalRequired": true
A=$(curl -s -H "X-Device-Token: $D" -H "Authorization: Bearer $T" -H 'content-type: application/json' \
  -d '{"pin":"<MAU PIN>","permissionKey":"order.void"}' http://localhost:5080/api/auth/approve | jq -r .approvalToken)
curl -X POST -H "X-Device-Token: $D" -H "Authorization: Bearer $T" -H "X-Approval-Token: $A" \
  http://localhost:5080/api/dev/void-probe     # 200 once; the same token again -> 403
```

Any write with an `Idempotency-Key` header returns the original answer when repeated
(header `Idempotency-Replayed: true`), and 422 if the same key comes with a different body.

### Printing

The demo has four printers, one per terminal (Ticket Printer 1, Pillar 1, Ticket Printer 2,
Pillar 2). Each terminal dispenses on its own printer; receipts print at the counter (Counter A
and Pillar 1 on Ticket Printer 1, Counter B and Pillar 2 on Ticket Printer 2). Running
`seed --demo` again converts a database seeded with an earlier printer setup. In development the demo printers use the **File** connection, so every slip is written as text
to `server/src/Tausi.Api/print-output/`. (A database seeded before this change: run
`seed --demo` again in Development and the untouched demo printers move to File.) Test print:

```bash
curl -s http://localhost:5080/api/health      # printer ids
curl -b /tmp/nx.cookies -X POST http://localhost:5080/api/printers/<printer id>/test
ls server/src/Tausi.Api/print-output/
```

`Tausi__Printing__Transport=File` sends every printer's jobs to files, whatever its connection.

### Terminal app (Tauri)

```bash
cd terminal && npm ci
npm run tauri dev
```

This starts the Vite dev server and opens the terminal window at 1280×800 (set
`TAUSI_FULLSCREEN=1` for full screen without a title bar, as on the counters). Pair it as above.
In development the window always loads the Vite dev server; the device token is kept in the
app's store (`tausi.json` in the app data folder).

### Upgrading from the Nexagen POS names

The product was renamed from Nexagen POS to Tausi POS. Installs set up with the old names keep
working:

- **Settings and environment variables.** `NEXAGEN_*` variables (e.g. `NEXAGEN_ADMIN_PASSWORD`),
  the `Nexagen` settings section (`Nexagen__DataDirectory`) and the `ConnectionStrings__Nexagen`
  connection string are still read when the new name (`TAUSI_*`, `Tausi__…`,
  `ConnectionStrings__Tausi`) is not set. The server logs a warning once for each old name it uses;
  rename them when convenient.
- **Existing dev databases named NexagenPOS:** either rename the database or point the
  connection string at the old name. The dev database is now `tausipos` (user `tausi`), in new
  Docker volumes (`tausi-pgdata`, `tausi-mssqldata`), so `docker compose up` starts an empty one
  and the old volumes are left as they were. To keep working on the old data instead:

  ```bash
  export ConnectionStrings__Tausi='Host=localhost;Port=5432;Username=nexagen;Password=nexagen_dev;Database=nexagen'
  ```

  (with the old `pgdata` volume running), or recreate the new one with `seed --demo`.
- **Dev data folder.** Server secrets (the PIN pepper and signing keys) live in
  `server/src/Tausi.Api/data/`. A checkout from before the rename has them in
  `server/src/Nexagen.Api/data/`, which git leaves behind; on start the server copies that folder
  once if the new one is missing, so existing PINs keep working. The old folder can then be deleted.
- **Windows service.** The service is now `TausiPOSServer`. Replace an old "Nexagen Server"
  service: `sc.exe stop "Nexagen Server"`, `sc.exe delete "Nexagen Server"`, then create the new
  one as above.
- **Terminal app.** The app identifier changed (`com.nexagen.terminal` →
  `com.nexagensolutions.tausipos`). On first start the new app copies the old app's settings, so
  the terminal stays paired. Uninstall the old "Nexagen Terminal" app afterwards so only one
  starts with Windows.

## 4. Tests

```bash
cd server && dotnet test        # unit + integration
cd web && npm test && npm run lint
```

The integration tests start PostgreSQL 16 with Testcontainers, so Docker must be running. To use
an existing server instead, set `TAUSI_TEST_POSTGRES` (each run creates and drops its own
database):

```bash
TAUSI_TEST_POSTGRES='Host=localhost;Port=5432;Username=tausi;Password=tausi_dev' dotnet test
```

The SQL Server migration test starts SQL Server 2022 with Testcontainers, or uses
`TAUSI_TEST_SQLSERVER`; without Docker or that variable it is skipped.

## 5. SQL Server

```bash
docker compose -f docker-compose.dev.yml --profile sqlserver up -d
export Database__Provider=SqlServer
export ConnectionStrings__Tausi='Server=localhost,1433;Database=tausipos;User Id=sa;Password=Tausi_dev_123;TrustServerCertificate=True'
dotnet run --project src/Tausi.Api -- seed --demo
```

## 6. Changing the database model

Each provider has its own migrations. Add both:

```bash
cd server
dotnet ef migrations add <Name> --project src/Tausi.Infrastructure --startup-project src/Tausi.Infrastructure \
  --context PostgresTausiDbContext --output-dir Migrations/Postgres
dotnet ef migrations add <Name> --project src/Tausi.Infrastructure --startup-project src/Tausi.Infrastructure \
  --context SqlServerTausiDbContext --output-dir Migrations/SqlServer
```

## 7. Production

### Build

```bash
cd web && npm ci && npm run build && cd ../server
dotnet publish src/Tausi.Api -c Release -r linux-x64 --self-contained false -o /opt/tausi   # Linux
dotnet publish src/Tausi.Api -c Release -r win-x64   --self-contained false -o out\tausi    # Windows
```

Publishing copies `web/dist` into `wwwroot`, so the server alone serves the terminal screens and
the admin site.

Settings come from `appsettings.json` next to the program, or environment variables:

| Setting | Example |
| --- | --- |
| `ConnectionStrings__Tausi` | `Host=localhost;Database=tausipos;Username=tausi;Password=...` |
| `Database__Provider` | `Postgres` or `SqlServer` |
| `Tausi__DataDirectory` | `/var/lib/tausi` (uploads and server secrets; **back it up with the database**) |
| `Urls` | `http://0.0.0.0:5080` |

Open port 5080 in the firewall and give the server a fixed IP.

The data directory holds `secrets/pin-pepper.key`. Without it no PIN works and every PIN must be
reset, so it is part of the backup.

### Linux (systemd)

`/etc/systemd/system/tausi.service`:

```ini
[Unit]
Description=Tausi POS Server by NexaGen Solutions
After=network-online.target postgresql.service

[Service]
Type=notify
WorkingDirectory=/opt/tausi
ExecStart=/usr/bin/dotnet /opt/tausi/Tausi.Api.dll
Environment=ASPNETCORE_ENVIRONMENT=Production
Environment=Tausi__DataDirectory=/var/lib/tausi
EnvironmentFile=-/etc/tausi/env
User=tausi
Restart=always

[Install]
WantedBy=multi-user.target
```

```bash
sudo useradd --system tausi && sudo mkdir -p /var/lib/tausi && sudo chown tausi /var/lib/tausi
sudo systemctl daemon-reload && sudo systemctl enable --now tausi
```

### Windows (service)

```powershell
sc.exe create TausiPOSServer binPath= "C:\Tausi\Tausi.Api.exe" start= auto DisplayName= "Tausi POS Server"
sc.exe description TausiPOSServer "Tausi POS server by NexaGen Solutions"
setx /M ConnectionStrings__Tausi "Server=.\SQLEXPRESS;Database=TausiPOS;Trusted_Connection=True;TrustServerCertificate=True"
setx /M Database__Provider SqlServer
setx /M Tausi__DataDirectory "C:\ProgramData\Tausi"
sc.exe start TausiPOSServer
```

The service account needs rights on the SQL Server database (for example add
`NT SERVICE\TausiPOSServer` as a login with `db_owner` on `TausiPOS`).

### Terminal installer

Push a tag `terminal-v<version>` (e.g. `terminal-v0.1.0`). GitHub Actions builds the Windows
installer (NSIS) on `windows-latest` and attaches it to the release as `TausiPOS-Setup.exe`.
It installs as "Tausi POS" (publisher NexaGen Solutions). On first start the app asks
for the server address (e.g. `http://POSSERVER2:5080`), shows a pairing code, and after pairing
opens full screen on the PIN pad. It starts with Windows.
