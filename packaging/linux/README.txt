Tausi POS Server for Linux, by NexaGen Solutions
================================================

Install (Ubuntu 22.04 or 24.04), as root:

    tar xzf tausi-server-<version>-linux-x64.tar.gz
    cd tausi-server-<version>-linux-x64
    sudo ./install.sh --with-postgres

It creates the tausi user and the folders (/opt/tausi, /var/lib/tausi, /var/log/tausi, /etc/tausi),
installs PostgreSQL with a random password when asked, opens the port in ufw for the local network
only, starts the tausi service, and prints the server's address and the setup token.

To upgrade, unpack the new version and run ./install.sh again: it backs up first, and goes back
to the previous version by itself if the new one doesn't start.

Settings: /etc/tausi/tausi.env (then: sudo systemctl restart tausi).
Logs: /var/log/tausi and `journalctl -u tausi`.
Backups: /var/lib/tausi/backups (Settings → Backups in the admin site).

Run the server on the bar's own network. Don't expose port 5080 to the internet.

See docs/INSTALL.md in the source for the full guide.
