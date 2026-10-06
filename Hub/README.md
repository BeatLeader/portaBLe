# portaBLe Hub

A small web app that deploys portaBLe branches to test subdomains on one Ubuntu box and redeploys them when the branch is pushed.

* Lists the repository's branches (blobless git mirror, fetched every minute, or instantly from a GitHub push webhook).
* **New deployment** = branch + name → `https://portable-<name>.beatleader.pro`, with:
  * the database from portaBLe's S3 bucket (`portabledbs`), either the branch default (`wwwroot/current_db_name.txt`) or any key;
  * an optional comparison database (`Comparison.db`, for the DatabaseComparison page);
  * optional extra command-line arguments, notes, auto-redeploy on push, and an optional login (the hub's) for the site.
* Each deploy: shallow checkout with submodules → `dotnet publish` into a new release folder → databases hard-linked into its
  `wwwroot` (downloaded only when the key changes) → switch the `current` link → restart → health check. If the new release
  does not answer, the previous one is restored, so a broken push never takes a running deployment down.
* Cloudflare: a proxied `A` record per deployment (created and deleted by the hub; records it did not create are never
  touched). TLS at the origin uses a Cloudflare origin certificate for `*.beatleader.pro`, trusted only by Cloudflare.

## Install / update (on the box, as root)

```bash
# first run: portaBLe's S3 keys go into /etc/portable-hub/hub.json
S3_ACCESS_KEY=... S3_SECRET_KEY=... bash setup.sh
# later updates (pulls the portable-hub branch, rebuilds and restarts the hub; deployments keep running)
curl -fsSL https://raw.githubusercontent.com/BeatLeader/portaBLe/portable-hub/Hub/deploy/setup.sh | bash
```

`setup.sh` installs the .NET 9 SDK, nginx, a 4 GB swap file (if there is no swap), the `portable` user (deployments are its
systemd user services, with lingering so they start at boot), the nginx site, the hub service and a login
(`/root/portable-hub-login.txt`). It never touches other nginx sites.

**Cloudflare token** (DNS records + origin certificate): dash.cloudflare.com → My Profile → API Tokens → Create Token →
template *Edit zone DNS*, add *Zone / SSL and Certificates / Edit*, zone resources = the zone. Save it as `/root/cloudflare.token`
and re-run `setup.sh` (it moves the token to `/etc/portable-hub/cloudflare.token` and replaces the temporary self-signed
certificate with an origin certificate). Without a token everything works except automatic DNS.

**GitHub webhook** (optional, otherwise pushes are picked up within a minute): repository → Settings → Webhooks → Add:
payload URL `https://portable.beatleader.pro/api/github-webhook`, content type `application/json`, secret =
`jq -r .GitHubWebhookSecret /etc/portable-hub/hub.json`, just the push event.

## Where things are

| | |
|---|---|
| config | `/etc/portable-hub/hub.json` (zone, host pattern, S3 keys, webhook secret, ports, memory cap) |
| login | `/etc/portable-hub/htpasswd` — `htpasswd -B /etc/portable-hub/htpasswd <user>` to add or change users |
| hub | `systemctl status portable-hub`, `journalctl -u portable-hub`, state in `/srv/portable/hub/state.json` |
| a deployment | `/srv/portable/deployments/<name>/{src,releases,current,data,logs}`; service `portable-<name>` (user unit): `sudo -u portable XDG_RUNTIME_DIR=/run/user/$(id -u portable) systemctl --user status portable-<name>` |
| nginx | `/etc/nginx/sites-available/portable-hub` (root) + `/etc/portable-hub/maps/*.map` (written by the hub) |

Uploading a database for a deployment: put it in the bucket like portaBLe does (`Program.UploadDatabaseAsync`, or
`aws s3 cp my.db s3://portabledbs/db-<date>-<label>.db`), then pick the key in the hub. Uploads don't go through the hub
because Cloudflare limits request bodies to 100 MB.

## Development

`dotnet run --project Hub/PortableHub.csproj` with `PORTABLE_HUB_CONFIG=path/to/hub.json` (see `deploy/hub.example.json`).
The hub itself only shells out to `git`, `dotnet`, `systemctl --user`, `ln`/`mv` and `sudo nginx -t` / `sudo systemctl reload nginx`
(the only sudo rights it has).
