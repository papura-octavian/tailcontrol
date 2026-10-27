#!/bin/sh
# Dezinstaleaza TailControl Hub.  sudo sh uninstall.sh [--purge]
# Fara --purge pastreaza configurarea (/etc/tailcontrol/hub), baza de date si utilizatorul.
set -eu

if [ "$(id -u)" -ne 0 ]; then
    echo "Ruleaza cu sudo: sudo sh uninstall.sh [--purge]" >&2
    exit 1
fi

systemctl disable --now tailcontrol-hub.service tailcontrol-hub-cert.timer 2>/dev/null || true
rm -f /etc/systemd/system/tailcontrol-hub.service \
      /etc/systemd/system/tailcontrol-hub-cert.service \
      /etc/systemd/system/tailcontrol-hub-cert.timer
systemctl daemon-reload
rm -rf /opt/tailcontrol-hub /opt/tailcontrol-hub.old /opt/tailcontrol-hub.new

if [ "${1:-}" = "--purge" ]; then
    rm -rf /etc/tailcontrol/hub /var/lib/tailcontrol-hub
    rmdir /etc/tailcontrol 2>/dev/null || true
    userdel tailcontrol-hub 2>/dev/null || true
    echo "TailControl Hub dezinstalat complet (inclusiv configurare si date)."
else
    echo "TailControl Hub dezinstalat. Configurarea si datele au ramas (foloseste --purge pentru a le sterge)."
fi
