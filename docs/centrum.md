# Centrum POLARIS

`polaris-centrum` je nástroj správcu (konzola) pre nastavenia učebne a zároveň server centra. Beží vo Windows aj na
Linuxe. Ako konzola pracuje len so súborom nastavení (`centrum.json`) a so súbormi, ktoré mu zadáš; ako server
(`polaris-centrum server`) odovzdáva PC zverejnené plány – prijíma len podpísané odovzdania od dôverovaných konzol
a žiadne PC nemení.

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

Server odovzdáva PC len to, čo konzola zverejnila: plány skupín, recepty a Zásobu balíkov. PC od neho len čítajú;
jediné, čo prijme, je podpísané odovzdanie od dôverovanej konzoly. Beží cez **HTTPS** s vlastným certifikátom. PC nepotrebujú certifikačnú autoritu: centrum poznajú podľa
**odtlačku jeho kľúča**, ktorý im zadá správca. Iný server s iným kľúčom PC odmietnu skôr, než od neho niečo prečítajú.

**Inštalácia jedným skriptom.** Z balíka `polaris-centrum-linux` nakopíruj (napr. WinSCP) do jedného priečinka na
serveri `polaris-centrum`, `polaris-centrum.service`, `instalovat-centrum.sh` a verejný kľúč konzoly
(`MENO.verejny.json`), potom cez SSH (PuTTY):

```
sudo bash instalovat-centrum.sh
```

Skript skontroluje systém (Debian alebo odvodený, x86_64, či sa program spustí), vytvorí používateľa `polaris` bez
prihlásenia, priečinky `/srv/polaris` (dáta) a `/opt/polaris` (program), certifikát centra pre meno a adresy servera,
nainštaluje službu systemd, pridá dôveru konzolám, **otvorí port 8443** (ufw, firewalld, iptables; nftables len
poradí), spustí centrum, overí odpoveď a vypíše adresu a **odtlačok**. Spustiť sa dá znova kedykoľvek: aktualizuje
program, certifikát a dáta nechá, pridá nové kľúče konzol. Odtlačok neskôr: `sudo runuser -u polaris --
/opt/polaris/polaris-centrum --subor /srv/polaris/centrum.json server odtlacok`.

- `server certifikat` vytvorí `server.pem` a `server.key` (čitateľný len pre používateľa polaris) a vypíše **odtlačok**.
  Mená a adresy za `certifikat` sú tie, ktorými PC server oslovujú.
- Služba beží pod vlastným používateľom bez práv, s celým systémom len na čítanie (pozri `tools/debian`). Port 8443
  stačí otvoriť do siete učebne.
- Server **zapisuje len do `zverejnene`** (odovzdania od konzol, ktorým dôveruje) **a `pc`** (kódy, registrácie
  a hlásenia PC).

### Odovzdanie z konzoly na server

Konzola (Windows alebo Linux, kdekoľvek v sieti) zverejní a rovno odovzdá centru:

```
sudo -u polaris /opt/polaris/polaris-centrum --subor /srv/polaris/centrum.json server doverovat spravca.verejny.json
polaris-centrum zverejnit spravca.kluc --server https://192.168.1.10:8443 ODTLAČOK          (na konzole)
```

- Na server patrí **len verejný kľúč** konzoly; súkromný server odmietne. `server konzoly` vypíše, komu centrum verí.
- Odovzdanie má **obálku podpísanú konzolou**: platí len pre tieto bajty, pre toto centrum (jeho odtlačok) a len
  10 minút. Cudzia konzola, nepodpísané, zmenené alebo staré odovzdanie centrum odmietne.
- Centrum potom **každý plán a recept overí znova** a prijme len plán novší, než už má. Odmietnutý kúsok nič
  nepokazí – zverejnené zostáva, ako bolo.
- Konzola pozná server rovnako ako PC: podľa odtlačku jeho kľúča.

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

## Registrácia PC a hlásenia (K7)

Nové PC sa do centra dostane **kódom z konzoly** – nikto nemusí na PC prepisovať odtlačky skupiny ani kľúče:

```
polaris-centrum kod ucebna spravca.kluc --server https://192.168.1.10:8443 ODTLAČOK      (konzola)
polaris-agent registrovat https://192.168.1.10:8443 ODTLAČOK 7K3Q-9XHD-M2PA [--meno PC-07]   (PC, správca)
polaris-centrum stav spravca.kluc --server https://192.168.1.10:8443 ODTLAČOK [PC [odobrat]]  (konzola)
```

- **Kód** platí 1 deň (`--dni` najviac 7) pre jednu skupinu a najviac 30 PC (`--pocet`). Konzola ho podpíše svojím
  kľúčom; centrum si nechá **len odtlačok** hodnoty z neho odvodenej, samotný kód nikde nie je. Pri písaní sa odpustia malé písmená,
  medzery a zámeny O/0, I/1.
- **Viac konzol** (napr. dva notebooky a učiteľské PC): každá má **vlastný kľúč** (`kluc novy notebook1` …) – stratený
  notebook sa tak odoberie bez výmeny ostatných. Verejné kľúče všetkých konzol daj vedľa kľúča konzoly, ktorá kód
  vydáva, a vedľa inštalačného skriptu na serveri (centrum im potom dôveruje). **Kód nesie zoznam konzol, ktorým má PC
  dôverovať, zapečatený samotným kódom:** PC ho prijme bez ďalšieho kroku (žiadne `doverovat` na každom PC) a centrum
  ho nemôže zmeniť – kód pozná len konzola a PC; centru PC ukáže iba hodnotu z kódu odvodenú. Po registrácii PC prijíma
  len recepty a plány podpísané týmito konzolami.
- **PC si pri registrácii vytvorí dva vlastné kľúče** (ECDSA P-256): jedným podpisuje všetko, čo posiela centru,
  druhým raz dostane zašifrované tajomstvá (heslo správcu, licencie). Súkromné časti nikdy neopustia PC: sú v chránenom
  priečinku agenta (len systém a správcovia), zašifrované Windows pre toto PC (kópia súboru na inom PC nefunguje).
  Identifikátor PC je odvodený z jeho kľúča – iné PC sa zaň nevydá.
- **Hlásenie** posiela služba po každom recepte, čistení a meraní a každých 15 minút: verzia agenta, Windows, model,
  skupina a profil, posledných 50 záznamov denníka, merania, zariadenia bez ovládača, trezory, miesto na disku, čas od
  zapnutia. Nikdy súbory žiakov, prehliadač ani obrazovka (dohodnuté 24. 11. 2026). Centrum drží posledné hlásenie
  a 30 dní histórie. Hneď: `polaris-agent hlasit`.
- **Stav** vidí len konzola, ktorej centrum dôveruje (otázka podpísaná jej kľúčom): ✓ v poriadku, ! chyby receptov
  alebo chýbajúci ovládač, ✗ neozvalo sa 40 minút. S menom PC alebo začiatkom identifikátora ukáže detail (merania,
  denník s chybami); `odobrat` PC z centra vymaže – jeho hlásenia sa potom odmietajú.
- Pre **dve centrá** (Debian aj Windows 11, návrh 4.16): záznam PC nesie dôkaz kódu, ktorý vie overiť len konzola, čo
  kód vydala – tá PC potvrdí aj druhému centru. Centrum beží rovnako aj na Windows 11 (CI to overuje).

## Pripravuje sa

Druhé centrum na Windows 11 ako služba a dorovnávanie centier, webová konzola s prehľadom učebne.
