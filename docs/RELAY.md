# The Tausi relay (NexaGen hosting)

The relay receives Safaricom's M-Pesa messages (STK results, and till and paybill payments, including QR
payments) on the internet, and holds them until each bar's server collects them. The bar's server
connects **out** to the relay, so nothing is opened in a bar's firewall. Safaricom can't reach a bar's
server, which is on the bar's own network.

The relay is **not** part of the bar installers. NexaGen hosts one relay for every customer, for example
at `https://pay.nexagensolutions.com`.

Code: `relay/` (`relay/src/Tausi.Relay`, tests in `relay/tests`). CI builds it as a tarball
(`tausi-relay-linux.tar.gz`) and a container image (`tausi-relay-image.tar.gz`), in the `tausi-relay`
artifact.

## What it stores

- **Businesses:** a name, an **API key** (shown once, kept only as a SHA-256 hash), and an unguessable
  **path token** used in its callback addresses.
- **Payments:** one row per business and M-Pesa transaction id. A repeated callback is stored once. Each
  is held until the bar's server **acknowledges** it, then kept 30 days, then deleted. A payment that is
  never acknowledged is kept until it is.
- **Phone numbers are stored masked** (`0712 ••• 678`). Matching never needs the full number, so it
  isn't kept anywhere. No card or PIN data passes through the relay.

A business can only ever receive and acknowledge its own payments (tests prove it).

## Hosting

You need a Linux server with a public address and a domain name (for example `pay.nexagensolutions.com`),
**PostgreSQL**, **nginx** for TLS, and either the .NET 10 ASP.NET Core runtime or Docker.

1. **Database:**
   ```bash
   sudo -u postgres createuser tausi_relay --pwprompt
   sudo -u postgres createdb tausi_relay -O tausi_relay
   ```
2. **The app, either way:**
   - **Tarball:** unpack `tausi-relay-linux.tar.gz` to `/opt/tausi-relay` and run it as a systemd service
     (`ExecStart=/usr/bin/dotnet /opt/tausi-relay/TausiRelay.dll`, `User=tausi-relay`,
     `Environment=ASPNETCORE_URLS=http://127.0.0.1:5090`).
   - **Container:** `docker load < tausi-relay-image.tar.gz`, then run it with `-p 127.0.0.1:5090:8080`.
3. **Settings**, as environment variables (or `/opt/tausi-relay/appsettings.Production.json`):
   ```
   ConnectionStrings__Relay=Host=localhost;Database=tausi_relay;Username=tausi_relay;Password=<the password>
   Relay__PublicUrl=https://pay.nexagensolutions.com
   Relay__AdminUser=nexagen
   Relay__AdminPasswordHash=<from the hash-password command below>
   ```
   Make the admin password hash with `echo '<a long password>' | dotnet TausiRelay.dll hash-password`.
   Never commit it or the database password.
4. **nginx with TLS** (certbot), forwarding to the app. The relay trusts `X-Forwarded-For` only from
   `127.0.0.1` and `::1` (`Relay__TrustedProxies__0=...` to change it), so put nginx on the same host:
   ```nginx
   server {
       server_name pay.nexagensolutions.com;
       location / {
           proxy_pass http://127.0.0.1:5090;
           proxy_set_header X-Forwarded-For $remote_addr;
           proxy_set_header X-Forwarded-Proto $scheme;
           proxy_read_timeout 60s;
       }
   }
   ```
   `proxy_read_timeout` must be over 25 seconds: servers wait up to 25 seconds for new payments.
5. The database is created and updated when the app starts.

**Health:** `GET /health` answers `{"status":"ok"}` when the database is reachable. Point an uptime
monitor at it.

**Logs:** the service's standard output (journald or `docker logs`). Refused callbacks are logged with
the caller's address. API keys and phone numbers are never logged.

**Backups:** back up the PostgreSQL database daily (`pg_dump tausi_relay`). Losing it loses only payments
not yet collected; Safaricom's till statement still has them.

## Adding a business

1. Sign in at `https://pay.nexagensolutions.com/admin` with the NexaGen admin user.
2. Under **Add a business**, type the business's name and press **Add**.
3. Copy the **API key** shown. It is shown only once. Give it to whoever sets up the bar's server.
4. In the bar's admin site, **Settings → M-Pesa**: type the relay address and the API key, save, and
   press **Test connection**. The bar's server asks the relay for its callback addresses and registers
   them with Safaricom itself.

The admin page lists each business with when its server last collected, how many payments are waiting,
and how many arrived in the last 30 days.

## Rotating a key

Press **New key** next to the business. The old key stops working at once. Type the new key into the
bar's **Settings → M-Pesa** and press **Test connection**. Payments that arrived meanwhile wait on the relay
and are collected with the new key.

**Switch off** stops a business from collecting and from receiving callbacks (for example when it stops
paying). **Switch on** restores it.

## Safaricom's addresses

Callbacks are accepted only from Safaricom's published callback addresses **and** with a known path
token. The list is in `Relay:SafaricomAddresses` (single addresses or ranges such as `196.201.214.0/24`).
When Safaricom publishes a change, update the list (an environment variable per entry,
`Relay__SafaricomAddresses__0=...`, or the settings file) and restart the service. Refused callbacks show
in the log with their address, which helps spot a new Safaricom address.

Daraja refuses callback addresses that contain the word "mpesa", so the relay's paths are
`/pay/<token>/stk`, `/pay/<token>/c2b/validation` and `/pay/<token>/c2b/confirmation`.
