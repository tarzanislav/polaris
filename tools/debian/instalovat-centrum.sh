#!/bin/bash
# POLARIS – inštalácia alebo aktualizácia centra na Debiane (aj na systéme od neho odvodenom).
#
# Do jedného priečinka na serveri (napr. cez WinSCP do domovského priečinka) nakopíruj z balíka polaris-centrum-linux:
#   polaris-centrum  polaris-centrum.service  instalovat-centrum.sh
# a voliteľne verejné kľúče konzol (MENO.verejny.json), ktorým má centrum dôverovať. Potom cez PuTTY:
#   sudo bash instalovat-centrum.sh
# Skript sa dá spustiť znova kedykoľvek: aktualizuje program, certifikát a dáta nechá, pridá nové kľúče konzol.
set -euo pipefail

port=8443
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
data=/srv/polaris
prog=/opt/polaris/polaris-centrum
unit=/etc/systemd/system/polaris-centrum.service

say() { printf '%s\n' "$*"; }
step() { printf '\n== %s\n' "$*"; }
fail() { printf '\nCHYBA: %s\n' "$*" >&2; exit 1; }
as_polaris() { (cd "$data" && runuser -u polaris -- "$prog" --subor "$data/centrum.json" "$@"); }

step "Kontrola servera"
[ "$(id -u)" = 0 ] || fail "spusti skript so sudo: sudo bash $0"
[ -r /etc/os-release ] || fail "chýba /etc/os-release – neviem, aký je to systém"
. /etc/os-release
case " ${ID:-} ${ID_LIKE:-} " in
  *" debian "*) say "Systém: ${PRETTY_NAME:-$ID}" ;;
  *) fail "toto nie je Debian ani systém od neho odvodený (${PRETTY_NAME:-neznámy})" ;;
esac
[ "$(uname -m)" = x86_64 ] || fail "centrum je zostavené pre 64-bitové procesory x86 (x86_64); tento server má $(uname -m)"
command -v systemctl >/dev/null || fail "chýba systemd (služba centra sa nedá nainštalovať)"
[ -f "$here/polaris-centrum" ] && [ -f "$here/polaris-centrum.service" ] \
  || fail "vedľa skriptu musia byť súbory polaris-centrum a polaris-centrum.service (z balíka polaris-centrum-linux)"
chmod 755 "$here/polaris-centrum"
if ! check=$("$here/polaris-centrum" pomoc 2>&1 >/dev/null); then
  fail "program centra sa na tomto serveri nespustí: ${check:-neznámy dôvod} – pošli tento výpis"
fi
say "Program centra sa tu spustí ($(ldd --version 2>/dev/null | head -1 | awk '{print $NF}' || echo '?') glibc)."

step "Používateľ a priečinky"
if id polaris >/dev/null 2>&1; then
  say "Používateľ polaris už existuje."
else
  useradd --system --home-dir "$data" --shell /usr/sbin/nologin polaris
  say "Vytvorený systémový používateľ polaris (bez prihlásenia, bez práv správcu)."
fi
install -d -o polaris -g polaris -m 750 "$data" "$data/zverejnene" "$data/pc" "$data/zasoba" "$data/konzoly"
install -d -m 755 /opt/polaris
say "Dáta: $data (len pre používateľa polaris), program: /opt/polaris."

step "Program a služba"
was_running=no
if systemctl is-active --quiet polaris-centrum; then
  was_running=yes
  systemctl stop polaris-centrum
  say "Služba zastavená kvôli aktualizácii."
fi
install -m 755 "$here/polaris-centrum" "$prog"
install -m 644 "$here/polaris-centrum.service" "$unit"
systemctl daemon-reload
say "Nainštalované: $prog, $unit."

step "Certifikát centra"
if [ -f "$data/server.pem" ]; then
  say "Certifikát už existuje – ostáva (nový by zneplatnil pripnutie na všetkých PC)."
else
  names=("$(hostname)")
  full="$(hostname -f 2>/dev/null || true)"
  if [ -n "$full" ] && [ "$full" != "${names[0]}" ]; then names+=("$full"); fi
  for ip in $(hostname -I 2>/dev/null || true); do
    case "$ip" in *:*) ;; *) names+=("$ip") ;; esac
  done
  as_polaris server certifikat "${names[@]}" >/dev/null
  say "Vytvorený certifikát pre: ${names[*]}"
fi
pin="$(as_polaris server odtlacok)"

step "Konzoly, ktorým centrum dôveruje"
shopt -s nullglob
keys=("$here"/*.verejny.json)
if [ ${#keys[@]} -eq 0 ]; then
  say "Vedľa skriptu nie je žiadny verejný kľúč konzoly (MENO.verejny.json) – pridáš ho neskôr spustením skriptu znova."
fi
for key in "${keys[@]}"; do
  copy="$data/.dovera-$(basename "$key")"
  install -o polaris -g polaris -m 640 "$key" "$copy"
  if as_polaris server doverovat "$copy"; then :; else say "Kľúč $(basename "$key") centrum neprijalo (je to naozaj verejný kľúč konzoly?)."; fi
  rm -f "$copy"
done
as_polaris server konzoly || true

step "Port $port vo firewalle"
if command -v ufw >/dev/null && ufw status 2>/dev/null | grep -q "Status: active"; then
  ufw allow "$port/tcp" comment "POLARIS centrum" >/dev/null
  say "ufw: port $port/tcp povolený."
elif systemctl is-active --quiet firewalld 2>/dev/null; then
  firewall-cmd --quiet --permanent --add-port="$port/tcp" && firewall-cmd --quiet --reload
  say "firewalld: port $port/tcp povolený natrvalo."
elif command -v nft >/dev/null && nft list ruleset 2>/dev/null | grep -Eq "hook input.*policy drop"; then
  say "nftables má pravidlá s odmietnutím na vstupe – sem skript nezasahuje (súbor pravidiel je tvoj)."
  say "Pridaj do reťazca input v /etc/nftables.conf riadok:   tcp dport $port accept"
  say "a potom: sudo systemctl reload nftables"
elif command -v iptables >/dev/null && iptables -S INPUT 2>/dev/null | grep -q -- "-P INPUT DROP"; then
  iptables -C INPUT -p tcp --dport "$port" -j ACCEPT 2>/dev/null || iptables -I INPUT -p tcp --dport "$port" -j ACCEPT
  if command -v netfilter-persistent >/dev/null; then netfilter-persistent save >/dev/null; say "iptables: port $port povolený a uložený."
  else say "iptables: port $port povolený (do reštartu – pravidlá sa neukladajú, chýba netfilter-persistent)."; fi
else
  say "Firewall na serveri nie je zapnutý – port $port je v sieti dostupný (zvonku školy ho chráni router)."
fi

step "Spustenie"
systemctl enable --now polaris-centrum >/dev/null 2>&1 || true
ok=no
for _ in $(seq 30); do
  if systemctl is-active --quiet polaris-centrum && ss -ltn 2>/dev/null | grep -q ":$port "; then ok=yes; break; fi
  sleep 1
done
if [ "$ok" != yes ]; then
  journalctl -u polaris-centrum -n 20 --no-pager || true
  fail "služba polaris-centrum nebeží – pošli výpis vyššie"
fi
if command -v curl >/dev/null && ! curl -sk --max-time 5 "https://127.0.0.1:$port/v1/stav" | grep -q '"polaris":"centrum"'; then
  fail "centrum počúva, ale neodpovedá ako centrum – pošli: journalctl -u polaris-centrum -n 30"
fi
[ "$was_running" = yes ] && say "Centrum aktualizované a znova beží." || say "Centrum beží a spustí sa samo aj po reštarte servera."

address="$(hostname -I 2>/dev/null | awk '{print $1}')"
step "Hotovo"
say "Adresa centra:   https://${address:-ADRESA-SERVERA}:$port"
say "Odtlačok centra: $pin"
say ""
say "Na konzole (PC správcu):  polaris-centrum kod SKUPINA spravca.kluc --server https://${address:-ADRESA}:$port $pin"
say "Na každom PC (správca):   polaris-agent registrovat https://${address:-ADRESA}:$port $pin KÓD"
say "Stav centra:              sudo systemctl status polaris-centrum   ·   záznam: sudo journalctl -u polaris-centrum -f"
