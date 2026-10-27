#!/bin/sh
# Instaleaza sau actualizeaza TailControl Hub pe Raspberry Pi (Raspbian 12, armv7l).
# Utilizare, din folderul dezarhivat:  sudo sh install.sh
#
# Ce face:
#   - utilizator de sistem `tailcontrol-hub` (fara shell, fara home)
#   - aplicatia in /opt/tailcontrol-hub (root:root, doar citire pentru serviciu)
#   - configurarea in /etc/tailcontrol/hub (root:tailcontrol-hub, 2750; nu se suprascrie la actualizare)
#   - unitatile systemd: serviciul si timer-ul de reinnoire a certificatului
# Nu modifica firewall-ul, Tailscale sau Pi-hole (pasi manuali, vezi docs/setup-checklist.md).
set -eu

if [ "$(id -u)" -ne 0 ]; then
    echo "Ruleaza cu sudo: sudo sh install.sh" >&2
    exit 1
fi

HERE=$(cd "$(dirname "$0")" && pwd)
USER_NAME=tailcontrol-hub
APP_DIR=/opt/tailcontrol-hub
CONFIG_DIR=/etc/tailcontrol/hub

case "$(uname -m)" in
    armv7l|armv6l) ;;
    *) echo "Avertisment: arhitectura $(uname -m); pachetul e compilat pentru linux-arm (32 biti)." >&2 ;;
esac

if [ ! -x "$HERE/app/TailControl.Hub" ] && [ ! -f "$HERE/app/TailControl.Hub" ]; then
    echo "Nu gasesc $HERE/app/TailControl.Hub. Ruleaza scriptul din folderul pachetului." >&2
    exit 1
fi

if ! id "$USER_NAME" >/dev/null 2>&1; then
    useradd --system --no-create-home --home-dir /nonexistent --shell /usr/sbin/nologin "$USER_NAME"
    echo "Utilizator creat: $USER_NAME"
fi

systemctl stop tailcontrol-hub.service 2>/dev/null || true

# Aplicatia: copiem intr-un folder nou, apoi il mutam peste cel vechi.
rm -rf "$APP_DIR.new"
mkdir -p "$APP_DIR.new"
cp -a "$HERE/app/." "$APP_DIR.new/"
cp "$HERE/deploy/renew-cert.sh" "$APP_DIR.new/renew-cert.sh"
chown -R root:root "$APP_DIR.new"
chmod -R u=rwX,go=rX "$APP_DIR.new"
chmod 0755 "$APP_DIR.new/TailControl.Hub" "$APP_DIR.new/renew-cert.sh"
rm -rf "$APP_DIR.old"
if [ -d "$APP_DIR" ]; then
    mv "$APP_DIR" "$APP_DIR.old"
fi
mv "$APP_DIR.new" "$APP_DIR"

# Configurarea: setgid pe folder, ca fisierele create de root sa fie citibile de grupul serviciului.
install -d -m 0755 -o root -g root /etc/tailcontrol
install -d -m 2750 -o root -g "$USER_NAME" "$CONFIG_DIR"
if [ ! -f "$CONFIG_DIR/hub.json" ]; then
    install -m 0640 -o root -g "$USER_NAME" "$HERE/deploy/hub.example.json" "$CONFIG_DIR/hub.json"
    echo "Creat $CONFIG_DIR/hub.json (de completat)."
fi
if [ ! -f "$CONFIG_DIR/cert.env" ]; then
    install -m 0600 -o root -g root "$HERE/deploy/cert.env.example" "$CONFIG_DIR/cert.env"
    echo "Creat $CONFIG_DIR/cert.env (de completat)."
fi

install -m 0644 -o root -g root "$HERE/deploy/tailcontrol-hub.service" /etc/systemd/system/
install -m 0644 -o root -g root "$HERE/deploy/tailcontrol-hub-cert.service" /etc/systemd/system/
install -m 0644 -o root -g root "$HERE/deploy/tailcontrol-hub-cert.timer" /etc/systemd/system/
systemctl daemon-reload
systemctl enable tailcontrol-hub.service tailcontrol-hub-cert.timer >/dev/null

if grep -q "exemplu" "$CONFIG_DIR/hub.json" || grep -q "exemplu" "$CONFIG_DIR/cert.env"; then
    cat <<EOF

Instalare terminata. Serviciul NU a fost pornit, pentru ca configurarea are inca valori de exemplu.
  1. sudo nano $CONFIG_DIR/hub.json    (AllowedHosts, WakeBroadcastAddress)
  2. sudo nano $CONFIG_DIR/cert.env    (CERT_DOMAIN)
  3. sudo systemctl start tailcontrol-hub-cert.service    (obtine certificatul)
  4. sudo systemctl start tailcontrol-hub.service
  5. sudo $APP_DIR/TailControl.Hub authorize-client --key <cheie> --label "Telefon" --admin
EOF
else
    systemctl start tailcontrol-hub.service
    echo "Serviciul a fost (re)pornit: systemctl status tailcontrol-hub"
fi
