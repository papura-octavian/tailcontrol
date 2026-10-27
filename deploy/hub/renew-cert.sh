#!/bin/sh
# Obtine/reinnoieste certificatul HTTPS al Hub-ului prin `tailscale cert`.
# Ruleaza ca root (din tailcontrol-hub-cert.timer). Hub-ul recitesc fisierele automat,
# fara repornire. tailscale cert reinnoieste efectiv doar cand certificatul e aproape de expirare.
set -eu

CONFIG_DIR=/etc/tailcontrol/hub
CERT_DIR="$CONFIG_DIR/certs"

if [ ! -f "$CONFIG_DIR/cert.env" ]; then
    echo "Lipseste $CONFIG_DIR/cert.env (CERT_DOMAIN=...)." >&2
    exit 1
fi
. "$CONFIG_DIR/cert.env"

case "${CERT_DOMAIN:-}" in
    *exemplu*|"")
        echo "Seteaza CERT_DOMAIN in $CONFIG_DIR/cert.env (numele MagicDNS al Pi-ului)." >&2
        exit 1 ;;
esac

install -d -m 2750 -o root -g tailcontrol-hub "$CERT_DIR"

tailscale cert --cert-file "$CERT_DIR/hub.crt.new" --key-file "$CERT_DIR/hub.key.new" "$CERT_DOMAIN"

chown root:tailcontrol-hub "$CERT_DIR/hub.crt.new" "$CERT_DIR/hub.key.new"
chmod 0640 "$CERT_DIR/hub.crt.new" "$CERT_DIR/hub.key.new"
mv -f "$CERT_DIR/hub.key.new" "$CERT_DIR/hub.key"
mv -f "$CERT_DIR/hub.crt.new" "$CERT_DIR/hub.crt"

echo "Certificat actualizat pentru $CERT_DOMAIN."
