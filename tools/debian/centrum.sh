#!/bin/bash
# POLARIS – centrum na Debiane jedným príkazom. Do jedného priečinka na serveri (WinSCP, napr. /root/polaris) nakopíruj
# balík polaris-centrum-linux (rozbalený alebo ako .zip) a verejné kľúče konzol (MENO.verejny.json). Potom v PuTTY:
#   bash /root/polaris/centrum.sh
# Skript nainštaluje alebo aktualizuje centrum, uloží celý výpis do instalacia.txt a adresu s odtlačkom do centrum.txt –
# ten súbor skopíruj na PC s konzolou vedľa konzola.cmd (nemusíš nič opisovať).
set -uo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$here" || exit 1
say() { printf '%s\n' "$*"; }
fail() { printf '\nCHYBA: %s\n' "$*" >&2; exit 1; }

if [ "$(id -u)" != 0 ]; then
  command -v sudo >/dev/null || fail "prihlás sa ako root (alebo nainštaluj sudo) a spusti znova"
  exec sudo bash "$0" "$@"
fi

# balík ako .zip rozbalí sám (unzip alebo python3 – jedno z nich Debian má takmer vždy)
if [ ! -f polaris-centrum ] && ! ls polaris-centrum_*_amd64.deb >/dev/null 2>&1; then
  zip="$(ls -t polaris-centrum-linux*.zip 2>/dev/null | head -1 || true)"
  [ -n "$zip" ] || fail "v $here nie je polaris-centrum ani polaris-centrum-linux.zip – nakopíruj balík z GitHubu"
  say "Rozbaľujem $zip …"
  if command -v unzip >/dev/null; then unzip -o -q "$zip" -d "$here"
  elif command -v python3 >/dev/null; then python3 -I -m zipfile -e "$zip" "$here"
  else fail "na rozbalenie chýba unzip aj python3 – rozbaľ balík vo Windows a nakopíruj súbory"; fi
fi
ls polaris-centrum_*_amd64.deb >/dev/null 2>&1 || [ -f instalovat-centrum.sh ] || fail "chýba balík .deb aj instalovat-centrum.sh (sú v balíku polaris-centrum-linux)"

keys=$(ls ./*.verejny.json 2>/dev/null | wc -l)
if [ "$keys" -eq 0 ]; then
  say ""
  say "POZOR: vedľa skriptu nie je žiadny kľúč konzoly (MENO.verejny.json)."
  say "Bez neho centrum nebude dôverovať žiadnej konzole a kód na registráciu PC sa nedá vydať."
  read -r -p "Pokračovať aj tak? [a/N] " answer
  case "$answer" in a|A|ano|áno) ;; *) say "Nakopíruj ucitel.verejny.json z PC s konzolou sem a spusti znova."; exit 1 ;; esac
else
  say "Kľúče konzol: $(ls ./*.verejny.json | xargs -n1 basename | tr '\n' ' ')"
fi

rm -f centrum.txt
deb="$(ls -t polaris-centrum_*_amd64.deb 2>/dev/null | head -1 || true)"
if [ -n "$deb" ]; then
  # balík Debianu: kľúče konzol tam, kde ich balík hľadá, potom apt (aktualizácia rovnako)
  install -d -m 755 /etc/polaris/konzoly
  for key in ./*.verejny.json; do [ -f "$key" ] && install -m 644 "$key" /etc/polaris/konzoly/; done
  apt-get install -y "./$deb" 2>&1 | tee instalacia.txt
  result=${PIPESTATUS[0]}
  [ "$result" = 0 ] && cp /etc/polaris/centrum.txt centrum.txt
else
  bash instalovat-centrum.sh 2>&1 | tee instalacia.txt
  result=${PIPESTATUS[0]}
fi
[ "$result" = 0 ] || fail "inštalácia sa nedokončila – pošli súbor $here/instalacia.txt"

address=$(sed -n 's/^Adresa centra: *\(https:[^ ]*\).*/\1/p' instalacia.txt | tail -1)
pin=$(sed -n 's/^Odtlačok centra: *\([0-9a-f]\{64\}\).*/\1/p' instalacia.txt | tail -1)
[ -n "$address" ] && [ -n "$pin" ] || fail "vo výpise som nenašiel adresu alebo odtlačok – pošli $here/instalacia.txt"
[ -f centrum.txt ] || printf '%s %s\r\n' "$address" "$pin" > centrum.txt
chmod 644 centrum.txt

say ""
say "=============================================================="
say " CENTRUM POLARIS BEŽÍ:  $address"
say " Pre konzolu je pripravený súbor:  $here/centrum.txt"
say " Skopíruj ho cez WinSCP na PC s konzolou do C:\\POLARIS\\konzola"
say " (vedľa konzola.cmd) a konzolu spusti dvojklikom na konzola.cmd."
say " Celý výpis je v $here/instalacia.txt"
say "=============================================================="
