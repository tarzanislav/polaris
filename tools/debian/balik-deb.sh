#!/bin/bash
# Zostaví balík Debianu polaris-centrum_VERZIA_amd64.deb (len dpkg-deb, nič iné).
#   bash tools/debian/balik-deb.sh VERZIA CESTA/polaris-centrum VÝSTUPNÝ-PRIEČINOK
# Na serveri:  apt install ./polaris-centrum_VERZIA_amd64.deb
#              polaris-centrum-nastavit /root/polaris/ucitel.verejny.json   (konzola, ktorej centrum dôveruje)
set -euo pipefail
version="$1"; binary="$2"; out="$3"
tools="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
root="$(mktemp -d)"; chmod 755 "$root"
trap 'rm -rf "$root"' EXIT

install -D -m 755 "$binary" "$root/opt/polaris/polaris-centrum"
install -D -m 644 "$tools/polaris-centrum.service" "$root/usr/lib/systemd/system/polaris-centrum.service"
install -D -m 755 "$tools/instalovat-centrum.sh" "$root/usr/sbin/polaris-centrum-nastavit"
install -d -m 755 "$root/etc/polaris/konzoly"
install -d -m 755 "$root/usr/share/doc/polaris-centrum"
cat > "$root/usr/share/doc/polaris-centrum/copyright" <<'TEXT'
Format: https://www.debian.org/doc/packaging-manuals/copyright-format/1.0/
Upstream-Name: POLARIS
Source: https://github.com/tarzanislav/polaris

Files: *
License: Apache-2.0
 Celý text licencie: /usr/share/common-licenses/Apache-2.0
TEXT

install -d -m 755 "$root/DEBIAN"
size=$(du -sk --exclude=DEBIAN "$root" | cut -f1)
cat > "$root/DEBIAN/control" <<TEXT
Package: polaris-centrum
Version: $version
Architecture: amd64
Maintainer: POLARIS <tarzanislav@users.noreply.github.com>
Section: admin
Priority: optional
Depends: libc6 (>= 2.34), libssl3t64 | libssl3, systemd, passwd, iproute2
Recommends: curl
Installed-Size: $size
Homepage: https://github.com/tarzanislav/polaris
Description: POLARIS centrum - správa školskej učebne
 Centrum učebne POLARIS: odovzdáva PC plány a recepty podpísané konzolou,
 registruje PC kódom z konzoly a drží ich technické hlásenia (30 dní).
 Beží ako služba systemd pod vlastným používateľom bez práv, HTTPS na porte 8443.
TEXT

cat > "$root/DEBIAN/postinst" <<'TEXT'
#!/bin/sh
set -e
case "$1" in
  configure) /usr/sbin/polaris-centrum-nastavit --z-balika ;;
esac
exit 0
TEXT

cat > "$root/DEBIAN/prerm" <<'TEXT'
#!/bin/sh
set -e
case "$1" in
  remove|deconfigure)
    systemctl stop polaris-centrum >/dev/null 2>&1 || true
    systemctl disable polaris-centrum >/dev/null 2>&1 || true ;;
esac
exit 0
TEXT

cat > "$root/DEBIAN/postrm" <<'TEXT'
#!/bin/sh
set -e
case "$1" in
  remove|purge)
    systemctl daemon-reload >/dev/null 2>&1 || true
    echo "Dáta centra (/srv/polaris – certifikát, PC, hlásenia) ostávajú. Zmazať ich môžeš ručne, PC ich potom nespoznajú." ;;
esac
exit 0
TEXT
chmod 755 "$root/DEBIAN/postinst" "$root/DEBIAN/prerm" "$root/DEBIAN/postrm"

mkdir -p "$out"
dpkg-deb --root-owner-group -Zxz --build "$root" "$out/polaris-centrum_${version}_amd64.deb" >/dev/null
echo "$out/polaris-centrum_${version}_amd64.deb"
