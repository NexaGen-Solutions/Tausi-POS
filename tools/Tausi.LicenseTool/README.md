# Tausi licence tool (NexaGen internal)

Issues Tausi POS licence keys. The server checks keys offline with the **public** key built into
`Tausi.Application` (`LicenceService.PublicKeys`). Only this tool, with the **private** key, can
issue them. This tool is not part of the product and is never given to customers.

## The private key

- Make it once: `dotnet run -- generate-keypair <folder>`. It writes `private.key` and
  `public.key`, and prints the public key for the server.
- **Keep `private.key` offline** (an encrypted USB drive in a safe, plus one other copy in a
  different place). Anyone with it can issue licences.
- **Never commit it** and never put it on a server. CI builds this tool but never runs it with a
  real key.
- If it is lost, no new keys can be issued for the existing public key: a new pair and a server
  update are needed.

## Issuing a key

```bash
dotnet run -- issue /path/to/private.key --business "Mau's Village" --terminals 4 \
  --edition Standard --expires never --support-until 2027-10-31
```

The business name must match the name entered in the setup wizard (case and spaces don't
matter). `--expires` takes `yyyy-mm-dd` or `never` (perpetual).

## Checking a key

```bash
dotnet run -- verify /path/to/public.key TAUSI-XXXXX-...
```

The tests use their own key pair, made in the tests; the server accepts that test key only in
the Development environment.
