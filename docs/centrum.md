# Centrum POLARIS

`polaris-centrum` je nástroj správcu (konzola) pre nastavenia učebne a zároveň server centra. Beží vo Windows aj na
Linuxe. Ako konzola pracuje len so súborom nastavení (`centrum.json`) a so súbormi, ktoré mu zadáš; ako server
(`polaris-centrum server`) odovzdáva PC zverejnené plány – nič neprijíma a žiadne PC nemení.

## Učebňa

```
polaris-centrum profil KANCELARSKY recepty/kancelarsky.json kancelárske programy
polaris-centrum profil ZAKLADNY recepty/zakladny.json mimo vyučovania
polaris-centrum skupina ucebna ZAKLADNY          skupina PC a jej predvolený profil
polaris-centrum pc PC01 ucebna                   PC v skupine (pc PC01 - ho vyradí)
polaris-centrum zvonenie 1 8:00 8:45
polaris-centrum hodina ut 1 ucebna KANCELARSKY   rozvrh: v utorok na 1. hodine beží KANCELARSKY
polaris-centrum zmena 18.11. 3 ucebna ZAKLADNY   jednorazová zmena
polaris-centrum volno 23.12.2026 7.1.2027
polaris-centrum rozvrh | den [DÁTUM] | profily | skupiny | pc
polaris-centrum kontrola
```

- **Profil je pomenovaný recept.** Recept bez problémov sa dá zapísať ako profil. Recept vedľa súboru nastavení sa
  zapíše relatívnou cestou, takže priečinok centra sa dá presunúť alebo zálohovať celý.
- `profily` ukáže pri každom profile verziu receptu, odtlačok a podpis. `kontrola` nájde chyby v rozvrhu, chýbajúce
  alebo pokazené recepty, recepty zmenené po podpísaní a PC v neznámej skupine.
- PC sa v centre volá menom, ktoré mu dáš. Do súboru sa nezapisuje nič o hardvéri.
- Súbor nastavení je vlastný JSON, čítaný prísne (preklep v poli centrum povie aj s riadkom). Každé uloženie
  najprv zapíše nový súbor a až potom nahradí starý; predchádzajúci ostane v `zalohy` (desať posledných). Poškodený
  súbor centrum nahradí poslednou dobrou zálohou a povie to.

## Zverejnenie pre PC

```
polaris-centrum podpisat recepty/kancelarsky.json spravca.kluc     každý recept profilu musí byť podpísaný
polaris-centrum zverejnit spravca.kluc                              plány všetkých skupín, podpísané konzolou
```

- `zverejnit` pripraví pre každú skupinu **plán**: jej rozvrh (zvonenie, jej hodiny, jednorazové zmeny, voľné dni),
  predvolený profil a ku každému profilu **odtlačok presne schváleného receptu**. Plán podpíše kľúč konzoly a spolu
  s receptami ho zapíše do priečinka `zverejnene` vedľa nastavení.
- Nezverejní nič, kým má niektorý profil problém: chýbajúci, pokazený, nepodpísaný alebo po podpísaní zmenený recept.
- Každé zverejnenie má vyššie poradové číslo (čas v milisekundách). PC prijme len novší plán, než má. Starší plán sa
  nedá podstrčiť ani keby ho niekto mal uložený.
- **Konzol môže byť viac** a môžu bežať vo Windows aj na Linuxe: každá má svoj kľúč a PC dôveruje každej, ktorú správca
  pridal (`polaris-agent doverovat`). Plán podpisuje konzola, nie server – kľúč konzoly na serveri nie je.

## Centrum na serveri (Debian)

Server odovzdáva PC len to, čo konzola zverejnila: plány skupín, recepty a Zásobu balíkov. Je **len na čítanie**
(prijme iba GET), cez **HTTPS** s vlastným certifikátom. PC nepotrebujú certifikačnú autoritu: centrum poznajú podľa
**odtlačku jeho kľúča**, ktorý im zadá správca. Iný server s iným kľúčom PC odmietnu skôr, než od neho niečo prečítajú.

```
sudo useradd --system --home /srv/polaris polaris
sudo install -d -o polaris -g polaris /srv/polaris /opt/polaris
sudo install -m 755 polaris-centrum /opt/polaris/
sudo -u polaris /opt/polaris/polaris-centrum --subor /srv/polaris/centrum.json server certifikat polaris-server 192.168.1.10
sudo install -m 644 polaris-centrum.service /etc/systemd/system/
sudo systemctl enable --now polaris-centrum
```

- `server certifikat` vytvorí `server.pem` a `server.key` (čitateľný len pre používateľa polaris) a vypíše **odtlačok**.
  Mená a adresy za `certifikat` sú tie, ktorými PC server oslovujú.
- Služba beží pod vlastným používateľom bez práv, s celým systémom len na čítanie (pozri `tools/debian`). Port 8443
  stačí otvoriť do siete učebne.
- `zverejnit` a ostatné príkazy spúšťaj ako používateľ polaris (`sudo -u polaris …`), alebo z inej konzoly a priečinok
  `zverejnene` skopíruj na server.

Na každom PC (správca):

```
polaris-agent doverovat spravca.verejny.json
polaris-agent centrum https://192.168.1.10:8443 ODTLAČOK ucebna
polaris-agent zasoba https://192.168.1.10:8443          (voliteľne: balíky zo Zásoby cez centrum)
polaris-agent centrum                                    stav; centrum teraz = stiahne plán a použije profil hneď
```

Služba POLARIS agent sa centra pýta **každých 5 minút**. Každú minútu podľa rozvrhu určí profil, ktorý má bežať,
a použije jeho recept, keď sa profil alebo recept zmenil; inak raz za hodinu (vráti, čo sa medzičasom zmenilo).
Plán a recepty si PC drží v chránenom priečinku, takže podľa rozvrhu pracuje aj vtedy, keď server nie je dostupný.
Recepty z centra majú brať programy z WinGet alebo zo Zásoby (`package`), nie inštalátor vedľa receptu.

## Kľúče, podpisy a Zásoba

Podpis receptov: [recept.md – Podpis receptu](recept.md#podpis-receptu). Zásoba balíkov:
[recept.md – Zásoba balíkov](recept.md#zásoba-balíkov).

## Pripravuje sa

Odovzdanie zverejnenia z konzoly na server cez HTTPS (podpísané, server prijme len od konzol, ktorým dôveruje),
hlásenia PC centru a konzola s prehľadom učebne.
