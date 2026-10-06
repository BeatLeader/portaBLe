#!/usr/bin/env bash
# portaBLe Hub: install or update on Ubuntu 24.04. Run as root; safe to re-run (it also updates the hub to the branch tip).
#
#   curl -fsSL https://raw.githubusercontent.com/BeatLeader/portaBLe/portable-hub/Hub/deploy/setup.sh | sudo bash
#
# First run only: S3_ACCESS_KEY / S3_SECRET_KEY (portaBLe's bucket keys) must be set in the environment, they go into
# /etc/portable-hub/hub.json. Optional: ZONE (beatleader.pro), HUB_HOST, HOST_PATTERN, PUBLIC_IP, HUB_BRANCH, HUB_SRC (local
# checkout instead of cloning). A Cloudflare token placed in /root/cloudflare.token is moved into place.
set -euo pipefail

REPO="${REPO:-https://github.com/BeatLeader/portaBLe.git}"
HUB_BRANCH="${HUB_BRANCH:-portable-hub}"
ETC=/etc/portable-hub
DATA=/srv/portable
HUB_PORT=5100
say() { printf '\n== %s\n' "$*"; }
[ "$(id -u)" = 0 ] || { echo "run as root"; exit 1; }
export DEBIAN_FRONTEND=noninteractive

say "packages"
if ! command -v dotnet >/dev/null || ! dotnet --list-sdks | grep -q '^9\.'; then
  add-apt-repository -y ppa:dotnet/backports
  apt-get update -qq
  apt-get install -y -qq dotnet-sdk-9.0
fi
apt-get install -y -qq nginx git jq openssl curl apache2-utils >/dev/null
dotnet --list-sdks | head -3

say "swap"
if [ -z "$(swapon --noheadings)" ]; then
  fallocate -l 4G /swapfile && chmod 600 /swapfile && mkswap /swapfile >/dev/null && swapon /swapfile
  grep -q '^/swapfile' /etc/fstab || echo '/swapfile none swap sw 0 0' >> /etc/fstab
  echo 'vm.swappiness=10' > /etc/sysctl.d/90-portable-swap.conf && sysctl -q -p /etc/sysctl.d/90-portable-swap.conf
fi
swapon --show

say "user and directories"
id portable >/dev/null 2>&1 || useradd --system --create-home --home-dir "$DATA" --shell /bin/bash portable
loginctl enable-linger portable
PUID=$(id -u portable)
install -d -o portable -g portable -m 755 "$DATA" "$DATA/deployments" "$DATA/hub"
install -d -o root -g root -m 755 "$ETC" "$ETC/nginx"
install -d -o root -g root -m 700 "$ETC/tls"
install -d -o portable -g portable -m 755 "$ETC/maps"
for f in hosts.map auth.map; do [ -f "$ETC/maps/$f" ] || install -o portable -g portable -m 644 /dev/null "$ETC/maps/$f"; done
# the hub may test and reload nginx, nothing else
echo 'portable ALL=(root) NOPASSWD: /usr/sbin/nginx -t, /usr/bin/systemctl reload nginx' > /etc/sudoers.d/portable-hub
chmod 440 /etc/sudoers.d/portable-hub && visudo -cqf /etc/sudoers.d/portable-hub

say "hub source"
if [ -n "${HUB_SRC:-}" ]; then
  SRC="$HUB_SRC"
else
  SRC="$DATA/hub/src"
  if [ -d "$SRC/.git" ]; then
    sudo -u portable -H git -C "$SRC" fetch -q --depth 1 "$REPO" "+refs/heads/$HUB_BRANCH:refs/remotes/origin/$HUB_BRANCH"
    sudo -u portable -H git -C "$SRC" reset -q --hard "origin/$HUB_BRANCH"
  else
    sudo -u portable -H git clone -q --depth 1 --branch "$HUB_BRANCH" "$REPO" "$SRC"
  fi
  git -C "$SRC" log -1 --format='%h %s'
fi
DEPLOY="$SRC/Hub/deploy"

say "configuration"
if [ ! -f "$ETC/hub.json" ]; then
  : "${S3_ACCESS_KEY:?first run: set S3_ACCESS_KEY and S3_SECRET_KEY}" "${S3_SECRET_KEY:?}"
  ZONE="${ZONE:-beatleader.pro}"
  DEFAULT_PATTERN='portable-{name}.'"$ZONE"
  PUBLIC_IP="${PUBLIC_IP:-$(curl -fsS4 --max-time 10 https://api.ipify.org || hostname -I | awk '{print $1}')}"
  jq -n --arg zone "$ZONE" --arg hub "${HUB_HOST:-portable.$ZONE}" --arg pattern "${HOST_PATTERN:-$DEFAULT_PATTERN}" \
        --arg ip "$PUBLIC_IP" --arg secret "$(openssl rand -hex 20)" --arg ak "$S3_ACCESS_KEY" --arg sk "$S3_SECRET_KEY" '{
    RepoUrl: "https://github.com/BeatLeader/portaBLe.git", RepoWebUrl: "https://github.com/BeatLeader/portaBLe",
    Zone: $zone, HubHost: $hub, HostPattern: $pattern, PublicIp: $ip,
    CloudflareTokenFile: "/etc/portable-hub/cloudflare.token", GitHubWebhookSecret: $secret,
    DataDir: "/srv/portable", NginxDir: "/etc/portable-hub/maps", FirstPort: 5101, PollSeconds: 60, KeepReleases: 2,
    S3: { Bucket: "portabledbs", Region: "us-east-1", AccessKey: $ak, SecretKey: $sk } }' > "$ETC/hub.json"
fi
chown root:portable "$ETC/hub.json" && chmod 640 "$ETC/hub.json"
ZONE=$(jq -r .Zone "$ETC/hub.json"); HUB_HOST=$(jq -r .HubHost "$ETC/hub.json"); PATTERN=$(jq -r .HostPattern "$ETC/hub.json")
if [ -s /root/cloudflare.token ]; then
  install -o portable -g portable -m 600 /root/cloudflare.token "$ETC/cloudflare.token" && rm -f /root/cloudflare.token
  echo "Cloudflare token installed"
fi
CREDS=/root/portable-hub-login.txt
if [ ! -f "$ETC/htpasswd" ]; then
  PASS=$(openssl rand -base64 24 | tr -dc 'A-Za-z0-9' | cut -c1-20)
  htpasswd -bcB "$ETC/htpasswd" admin "$PASS" 2>/dev/null
  printf 'https://%s\nuser: admin\npassword: %s\n' "$HUB_HOST" "$PASS" > "$CREDS" && chmod 600 "$CREDS"
fi
chown root:www-data "$ETC/htpasswd" && chmod 640 "$ETC/htpasswd"

say "TLS (Cloudflare origin certificate)"
TLS="$ETC/tls"
if [ ! -s "$TLS/origin.pem" ] || { [ -f "$TLS/self-signed" ] && [ -s "$ETC/cloudflare.token" ]; }; then
  ISSUED=0
  if [ -s "$ETC/cloudflare.token" ]; then
    openssl req -new -newkey ec -pkeyopt ec_paramgen_curve:prime256v1 -nodes -keyout "$TLS/origin.key.new" \
      -out "$TLS/origin.csr" -subj "/CN=*.$ZONE" 2>/dev/null
    BODY=$(jq -n --arg csr "$(cat "$TLS/origin.csr")" --arg host "*.$ZONE" \
      '{hostnames: [$host], requested_validity: 5475, request_type: "origin-ecc", csr: $csr}')
    RESP=$(curl -sS -X POST https://api.cloudflare.com/client/v4/certificates \
      -H "Authorization: Bearer $(cat "$ETC/cloudflare.token")" -H 'Content-Type: application/json' --data "$BODY" || true)
    if echo "$RESP" | jq -e '.success == true' >/dev/null 2>&1; then
      echo "$RESP" | jq -r .result.certificate > "$TLS/origin.pem"
      mv "$TLS/origin.key.new" "$TLS/origin.key" && rm -f "$TLS/self-signed" "$TLS/origin.csr"
      ISSUED=1; echo "issued a Cloudflare origin certificate for *.$ZONE (15 years)"
    else
      echo "origin certificate not issued (token needs Zone / SSL and Certificates / Edit): $(echo "$RESP" | jq -c .errors 2>/dev/null || echo "$RESP")"
      rm -f "$TLS/origin.key.new" "$TLS/origin.csr"
    fi
  fi
  if [ "$ISSUED" = 0 ] && [ ! -s "$TLS/origin.pem" ]; then
    openssl req -x509 -newkey ec -pkeyopt ec_paramgen_curve:prime256v1 -nodes -days 3650 -subj "/CN=*.$ZONE" \
      -addext "subjectAltName=DNS:*.$ZONE" -keyout "$TLS/origin.key" -out "$TLS/origin.pem" 2>/dev/null
    touch "$TLS/self-signed"
    echo "using a self-signed certificate (fine for Cloudflare SSL mode Full, not Full (strict)) until a token is available"
  fi
fi
chmod 600 "$TLS/origin.key"

say "nginx"
# e.g. ~^portable-[a-z0-9-]+[.]beatleader[.]pro$  ([.] rather than \. so no backslash has to survive the sed below)
DEPLOY_RE="~^$(printf '%s' "$PATTERN" | sed 's/[.]/[.]/g; s/{name}/[a-z0-9-]+/')\$"
install -m 644 "$DEPLOY/proxy.conf" "$ETC/nginx/proxy.conf"
sed -e "s|__HUB_HOST__|$HUB_HOST|g" -e "s|__HUB_PORT__|$HUB_PORT|g" -e "s|__DEPLOY_HOST_RE__|$DEPLOY_RE|g" \
  "$DEPLOY/nginx-site.conf" > /etc/nginx/sites-available/portable-hub
ln -sfn /etc/nginx/sites-available/portable-hub /etc/nginx/sites-enabled/portable-hub
nginx -t -q && systemctl reload nginx

say "build hub"
sudo -u portable -H env DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 \
  dotnet publish "$SRC/Hub/PortableHub.csproj" -c Release -o "$DATA/hub/app.new" -nologo -v:q
rm -rf "$DATA/hub/app.old"
[ -d "$DATA/hub/app" ] && mv "$DATA/hub/app" "$DATA/hub/app.old"
mv "$DATA/hub/app.new" "$DATA/hub/app"

say "service"
sed -e "s|__UID__|$PUID|g" -e "s|__HUB_PORT__|$HUB_PORT|g" "$DEPLOY/portable-hub.service" > /etc/systemd/system/portable-hub.service
systemctl daemon-reload
systemctl enable -q portable-hub
systemctl restart portable-hub
for i in $(seq 1 30); do curl -fsS -o /dev/null "http://127.0.0.1:$HUB_PORT/" && break; sleep 1; done
systemctl is-active portable-hub

say "done"
echo "hub:          https://$HUB_HOST  (login in $CREDS)"
echo "deployments:  https://$(printf '%s' "$PATTERN" | sed 's/{name}/<name>/')"
[ -s "$ETC/cloudflare.token" ] || echo "Cloudflare:   no token yet: put one (Zone/DNS/Edit + Zone/SSL and Certificates/Edit for $ZONE) in /root/cloudflare.token and re-run"
echo "webhook:      https://$HUB_HOST/api/github-webhook  secret: jq -r .GitHubWebhookSecret $ETC/hub.json"
