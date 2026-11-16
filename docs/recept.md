# Recept POLARIS

Recept opisuje, čo má mať PC jedného profilu (napríklad KANCELÁRSKY). Je to textový súbor JSON. Má verziu
a **odtlačok SHA-256**, takže každé PC vie preukázať, že má presne schválenú verziu.

Agent recept nikdy nepoužije, kým nie je bez problémov (`polaris-agent overit`). Každú zmenu po vykonaní **overí
opätovným prečítaním stavu**. Chyba jedného prvku nezastaví ostatné.

## Príkazy (etapy K1 a K2: spúšťa správca na PC, bez siete)

```
polaris-agent overit RECEPT          kontrola receptu – nič nemení, funguje aj na Linuxe
polaris-agent odtlacok RECEPT        SHA-256 receptu (na schválenie verzie profilu); --kanonicky: text, z ktorého sa počíta
polaris-agent kontrola RECEPT        čo sa na tomto PC líši od receptu – nič nemení
polaris-agent pouzi RECEPT [--ano]   urobí zmeny a každú overí (len správca)
polaris-agent dennik [POČET]         posledné behy na tomto PC
polaris-agent doverovat KLUC         PC prijme recepty podpísané touto konzolou (pozri Podpis receptu)
polaris-agent instalovat-sluzbu      služba PolarisAgent (čistí relácie), z Program Files; odinstalovat-sluzbu ju odstráni
polaris-agent vycistit KONTO         hneď zmaže profil zdieľaného konta (nie správcu, nie keď je niekto prihlásený)
polaris-agent obchod                 Školský obchod pre žiaka (spúšťa žiak, nie správca)
```

Návratový kód: `0` = v poriadku / PC je podľa receptu, `1` = rozdiely alebo chyby, `2` = zlý vstup, recept
s problémami alebo chýbajúce práva.

## Podpis receptu

Na PC sa má dostať len recept, ktorý schválil správca. Preto má každá konzola **vlastný kľúč** (ECDSA P-256) a recept
podpíše:

```
polaris-centrum kluc novy spravca                       vznikne spravca.kluc (súkromný, chránený heslom)
                                                        a spravca.verejny.json (verejný)
polaris-centrum podpisat recept.json spravca.kluc       vznikne recept.json.podpis
polaris-agent doverovat spravca.verejny.json            na PC (správca): odteraz len podpísané recepty
polaris-agent konzoly | nedoverovat ODTLACOK            zoznam a zrušenie dôvery
```

- Podpis platí pre **odtlačok receptu**. Ktorákoľvek zmena receptu (aj jedna hodnota) ho zneplatní.
- Kým PC nedôveruje žiadnej konzole, recept sa použije aj bez podpisu a agent na to upozorní. Keď dôveruje aspoň jednej,
  **nepodpísaný, zmenený alebo cudzí recept odmietne**.
- Dôverované konzoly sú v časti registra `HKLM\SOFTWARE\POLARIS\Agent`, ktorú môžu meniť len správcovia a recept ju
  meniť nesmie.
- Súkromný kľúč je zašifrovaný heslom (aspoň 12 znakov; AES-256, PBKDF2 600 000 kôl). Drž ho len u seba, napríklad na
  USB v trezore, nikdy nie na PC žiakov ani na GitHube. Heslo sa dá zadať aj premennou prostredia `POLARIS_HESLO`.

## Tvar receptu

```json
{
  "schema": 1,
  "name": "KANCELARSKY",
  "version": 3,
  "note": "november: nový LibreOffice",
  "elements": [ … ]
}
```

- Komentáre `//` a `/* */` sú povolené. Odtlačok ich ani formátovanie nezapočítava
  ([kanonický tvar](kanonicky-tvar.md)).
- **Neznáme pole je chyba**: agent povie riadok, znak a najbližšie známe meno. Preklep nikdy neprejde potichu.
- Cesty Windows sa v JSON píšu s dvojitým `\\` (`"C:\\Users\\Public"`).
- Prvky sa používajú v poradí, v akom sú zapísané. Odstraňovanie ide pred pridávaním.
- Každý prvok má `type`, `id` (malé písmená bez diakritiky, číslice, `-`, `.`, `_`), voliteľne `note` a `present`
  (`true` = má tam byť, predvolené; `false` = má sa odstrániť).
- Dva prvky nesmú meniť to isté. Kontrola to odhalí aj vtedy, keď ide o záložky a hodnotu registra pre ten istý
  prehliadač.

## Typy prvkov

### `program`: program pre celé PC

```json
{ "type": "program", "id": "vlc", "winget": "VideoLAN.VLC", "displayName": "VLC media player" }
```

| Pole | Význam |
|---|---|
| `displayName` | meno v zozname nainštalovaných programov. **Podľa neho agent program rozpozná.** |
| `match` | `Exact` (predvolené) alebo `Prefix`, keď meno obsahuje verziu (napr. `7-Zip 25.01 (x64)` → `7-Zip`) |
| `winget` | balík WinGet (`Vydavateľ.Názov`), inštaluje sa pre celé PC a potichu |
| `version` | verzia z WinGet (prázdne = najnovšia a stačí hocijaká nainštalovaná). S verziou sa iná verzia aktualizuje (`winget upgrade`), a keď sa tam WinGet nedostane (staršia verzia), odinštaluje sa a nainštaluje sa želaná. Verzia sa porovnáva so zoznamom programov: `25.8.3` sedí aj na `25.8.3.2` |
| `installer` | alebo inštalátor vedľa receptu: `file` (cesta vnútri priečinka receptu), `sha256`, `arguments`, `uninstallArguments` |
| `package` | alebo **balík zo Zásoby balíkov**: `{ "id": "…", "fingerprint": "…" }` – presne to vypíše `polaris-centrum zasoba pridat`. `displayName` je pri balíku nepovinné |

- Inštalátor s iným odtlačkom, než je v recepte, sa **nikdy nespustí**.
- Balíky MSI inštaluje a odinštaluje **priamo Windows Installer** (msi.dll), bez okna a bez reštartu; záznam každého behu
  je v `ProgramData\POLARIS\agent\msi`.
- Odstránenie: cez WinGet, alebo tichou odinštaláciou, ktorú pozná Windows (Windows Installer, `QuietUninstallString`).
  Ak ju program nemá, doplň `installer.uninstallArguments` (napr. `/S`).
- Kódy 3010 a 1641 (hotovo, treba reštart) sa berú ako úspech. O výsledku rozhoduje vždy opätovné prečítanie stavu.

### Zásoba balíkov

Zásoba je priečinok (na PC alebo zdieľaný priečinok školského servera, pre PC len na čítanie). Každý balík má v nej
priečinok `baliky/ID` s inštalátorom a popisom `balik.json`: ako inštalovať, ako spoznať, že je nainštalovaný, ako ho
odstrániť a kto ho podpísal.

```
polaris-centrum zasoba pridat \\server\zasoba Audacity-3.7.4.exe --argumenty "/VERYSILENT /NORESTART" --meno Audacity --zaciatok
polaris-centrum zasoba pridat \\server\zasoba LibreOffice_25.8.3_Win_x86-64.msi      (MSI sa prečíta sám, vo Windows)
polaris-centrum zasoba zoznam \\server\zasoba
polaris-centrum zasoba overit \\server\zasoba                                        (odtlačky všetkých inštalátorov)
polaris-agent zasoba \\server\zasoba                                                  (na PC, správca; inak priečinok zasoba vedľa receptu)
```

- `pridat` vypíše úryvok do receptu: `"package": {"id":"…","fingerprint":"…"}`. **Odtlačok popisu obsahuje aj odtlačok
  inštalátora**, takže podpísaný recept pripne každý bajt, ktorý sa na PC spustí, aj jeho argumenty.
- MSI: ProductCode, názov a verziu prečíta Windows Installer. Agent potom pozná balík **podľa ProductCode**, nie podľa
  mena v zozname programov.
- `.exe` potrebuje `--argumenty` tichej inštalácie a `--meno` (ako ho Windows ukazuje v zozname programov,
  `--zaciatok` = meno začína týmto textom); pre odinštalovanie prípadne `--odinstalovanie`.
- **Podpis vydavateľa** (Authenticode) overí pri pridaní Windows. Podpísaný balík si pamätá vydavateľa a agent pri
  inštalácii trvá na platnom podpise toho istého vydavateľa. Neplatne podpísaný inštalátor sa do zásoby nedostane.
- Agent si inštalátor najprv skopíruje do chráneného priečinka `ProgramData\POLARIS\agent\balicky`, tam overí
  odtlačok (a podpis) a spustí **len túto kópiu**. Kópia popisu tam ostane, takže program zo zásoby sa dá odinštalovať
  aj vtedy, keď server nie je dostupný.
- Balík v zásobe sa nikdy neprepíše: nová verzia = nové id (napr. `libreoffice-25.8.4`).
- Balíky zo zásoby môže ponúkať aj Školský obchod (položka s `package`).

### `registry`: hodnota v časti registra pre počítač (HKLM)

```json
{ "type": "registry", "id": "edge-bez-uvitania", "path": "HKLM\\SOFTWARE\\Policies\\Microsoft\\Edge",
  "name": "HideFirstRunExperience", "kind": "DWord", "data": "1" }
```

- `kind`: `String`, `ExpandString`, `DWord`, `QWord`, `MultiString` (riadky oddelené `\n`). Čísla sa píšu desiatkovo
  alebo `0x…`.
- `name` prázdne = predvolená hodnota kľúča. Odstránenie zmaže len hodnotu, kľúče nikdy.
- **Chránené časti sa receptom meniť nedajú:** `SAM`, `SECURITY`, `BCD00000000`, `SOFTWARE\POLARIS`, služby,
  `Control\Lsa`, `SYSTEM\Setup` a `Winlogon`. Chyba v nich by mohla zablokovať štart alebo prihlásenie. Na takéto
  zmeny budú samostatné typy prvkov.

### `bookmarks`: záložky pre Edge a Chrome

```json
{ "type": "bookmarks", "id": "skolske-zalozky", "browsers": ["Edge", "Chrome"], "folder": "Škola", "showBar": true,
  "items": [
    { "name": "Wikipédia", "url": "https://sk.wikipedia.org/" },
    { "name": "Programovanie", "children": [ { "name": "Python", "url": "https://docs.python.org/3/" } ] }
  ] }
```

- Zapíšu sa ako politika prehliadača **ManagedBookmarks**: každý používateľ PC ich vidí v priečinku `folder`, žiak ich
  nezmaže a vlastné záložky si môže pridávať ďalej.
- `showBar` zapne lištu záložiek pod riadkom adresy.
- Adresy len `http` a `https`, priečinky najviac 4 úrovne, spolu najviac 500 záložiek.

### `service`: služba Windows

```json
{ "type": "service", "id": "tlac", "name": "Spooler", "start": "Automatic", "running": true }
```

- `name` je meno kľúča služby (nie meno zobrazené v zozname služieb).
- `start`: `Automatic`, `AutomaticDelayed`, `Manual`, `Disabled`. `running`: `true`, `false`, alebo vynechať, keď na
  tom nezáleží.
- Službu recept nevytvára ani neodstraňuje. Ak služba neexistuje, plán to oznámi.
- **Chránené služby** (Defender, firewall, aktualizácie Windows, RPC, denník udalostí, plánovač úloh, profily
  používateľov, sieť, POLARIS a ďalšie) sa receptom nedajú vypnúť ani zastaviť, zapnúť áno.

### `feature`: súčasť Windows

```json
{ "type": "feature", "id": "pieskovisko", "name": "Containers-DisposableClientVM" }
```

- `present: true` súčasť zapne, `false` vypne. Volá sa priamo rozhranie DISM Windows, žiadny text nástrojov sa nečíta.
- Zmena, ktorá čaká na reštart, sa počíta ako hotová a výsledok to povie („dokončí sa po reštarte“).
- Užitočné mená: `Containers-DisposableClientVM` (Windows Sandbox, potrebuje zapnutú virtualizáciu v UEFI),
  `Microsoft-Hyper-V-All`, `TelnetClient`, `SMB1Protocol` (v škole vypnuté).

### `session`: relácia spoločného konta žiaka

```json
{ "type": "session", "id": "relacia-ziak", "account": "ziak", "clean": "AtSignOut" }
```

- Služba POLARIS agent zmaže **profil** konta (plochu, dokumenty, nastavenia, cookies, programy nainštalované „len pre
  mňa“). Pri ďalšom prihlásení Windows vytvorí nový profil z predlohy. Konto samo ostáva.
- `clean`: `AtSignOut` (po každom odhlásení a pri štarte PC), `EndOfDay` (profil vydrží do konca dňa), `EndOfWeek`
  (do konca týždňa), `Never`.
- Nikdy sa nečistí, keď je na konte niekto prihlásený, a **nikdy konto správcu** (ani keby to recept chcel).
- Konto musí na PC existovať. Pozor: pri `EndOfDay` a `EndOfWeek` ďalší žiak uvidí prácu predchádzajúceho, práca
  patrí do Úschovne.
- Čistí služba: `polaris-agent instalovat-sluzbu` (skopíruje agenta do Program Files, spustí službu PolarisAgent).
  Ručne hneď: `polaris-agent vycistit ziak`.

### `keyboards`: klávesnice a jazyky pre nové profily

```json
{ "type": "keyboards", "id": "klavesnice", "languages": ["sk-SK", "cs-CZ", "en-US", "de-DE"],
  "layouts": { "sk-SK": "0001041B" } }
```

- Zapíšu sa do **predlohy profilov**, takže ich má každý nový profil, aj žiak po každom vyčistení. Prvá je predvolená.
- Známe jazyky: sk-SK, cs-CZ, en-US, en-GB, de-DE, de-AT, fr-FR, ru-RU, hu-HU, pl-PL, es-ES, it-IT. Iný jazyk alebo iné
  rozloženie sa zapíše do `layouts` (napr. `"uk-UA": "0422:00020422"`, slovenská QWERTY `"sk-SK": "0001041B"`).
- Najviac 9 jazykov, jeden jazyk = jedna klávesnica. Overí sa pri pilote, že Windows 11 jazyky z predlohy prevezme.

### `user-registry`: nastavenie pre nové profily

```json
{ "type": "user-registry", "id": "pripony-suborov", "path": "Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced",
  "name": "HideFileExt", "kind": "DWord", "data": "0" }
```

- Hodnota v časti registra používateľa (bez `HKCU\`), zapísaná do predlohy profilov. Údaje ako pri `registry`.

### `sandbox`: šablóna Pieskoviska na ploche

```json
{ "type": "sandbox", "id": "pieskovisko-web", "name": "Web a programovanie", "networking": true, "memoryMb": 6144,
  "folders": [ { "hostFolder": "C:\\Users\\Public\\Documents\\Ucivo" } ] }
```

- Na spoločnú plochu (všetci používatelia ju vidia, meniť ju môže len správca) sa zapíše súbor
  `Pieskovisko – MENO.wsb`. Dvojklik spustí Windows Sandbox s týmito nastaveniami. Čo žiak v Pieskovisku urobí, po
  zatvorení zmizne.
- Sandbox musí byť zapnutý prvkom `feature` (`Containers-DisposableClientVM`).
- **Predvolene prísne:** bez siete, bez schránky, bez mikrofónu, kamery a tlačiarne, s chráneným klientom
  (`protectedClient`), 4096 MB pamäte. Zapína sa len to, čo hodina potrebuje: `networking`, `clipboard`, `microphone`,
  `camera`, `printer`, `gpu`, `memoryMb` (2048 až 65536).
- `folders`: priečinky PC v Pieskovisku. `hostFolder` je celá cesta, `sandboxFolder` miesto vnútri (prázdne = na
  ploche Pieskoviska), `readOnly` je predvolene `true`. **Koreň disku, Windows, Program Files, ProgramData a celé
  profily** sa dajú dať len na čítanie.
- `logonCommand`: príkaz, ktorý sa spustí vnútri po štarte (napríklad inštalácia nástrojov hodiny).

### `store`: Školský obchod

```json
{ "type": "store", "id": "skolsky-obchod", "accounts": ["ziak"], "maxPerSession": 2,
  "items": [
    { "id": "audacity", "title": "Audacity", "description": "nahrávanie a strih zvuku",
      "program": { "id": "audacity", "winget": "Audacity.Audacity", "displayName": "Audacity", "match": "Prefix" } }
  ] }
```

- Žiak spoločného konta si zo zoznamu, ktorý schválil správca, **sám nainštaluje program bez hesla správcu**:
  `polaris-agent obchod` (zoznam a číslo programu), alebo `polaris-agent obchod zoznam` a
  `polaris-agent obchod instalovat ID`.
- Inštaluje služba POLARIS agent pre celé PC. **Pri čistení relácie konta program odinštaluje**, ešte pred zmazaním
  profilu. Keď odinštalovanie zlyhá, záznam ostane a služba to skúša znova.
- Kto žiada, zistí služba z Windows (identita klienta pomenovanej rúry `\\.\pipe\polaris-obchod`), nie z
  požiadavky. Požiadavka nesie len `id` z katalógu, takže žiak nemôže podstrčiť vlastný program ani parametre.
- Obchod odmietne: správcu (ten inštaluje receptom), konto mimo `accounts`, konto, ktorého relácia sa nečistí
  (program by sa nemal kedy odstrániť), program, ktorý je už na PC natrvalo, a viac ako `maxPerSession` programov
  (1 až 10) v jednej relácii.
- Programy z WinGet alebo **balíky zo Zásoby** (`package`). Program v obchode nesmie byť zároveň trvalým prvkom
  `program` toho istého receptu.
- Každá inštalácia sa zapíše do denníka (`polaris-agent dennik`). Katalóg a záznamy sú v
  `HKLM\SOFTWARE\POLARIS\Agent\Obchod`.
- Žiak obchod používa cez **ikonu POLARIS** pri hodinách (pozri nižšie), alebo príkazom v termináli.

## Ikona pre žiaka (`polaris-ziak.exe`)

Ikona POLARIS v oblasti oznámení (pri hodinách) v každej prihlásenej relácii:

- **Školský obchod na klik:** ponuka programov, nainštalované so značkou ✓; po kliknutí oznámenie „Inštalujem…“
  a potom výsledok.
- **Upozornenie na čistenie:** pri prihlásení a v popise ikony („Po odhlásení sa tento profil vymaže – prácu si
  ukladaj do Úschovne alebo na USB“).
- **Pieskovisko:** spustenie šablón z plochy.

Spúšťa ju **služba POLARIS agent** (nie zoznam programov po prihlásení, ten by žiak vedel obísť):

- Každých 5 sekúnd skontroluje všetky prihlásené relácie. **Ikonu, ktorá chýba alebo padla, spustí znova.** Keď ikona
  padá hneď po štarte, po troch pádoch počká 5 minút a zapíše to do denníka (raz, nie stále dookola).
- Ikona beží ako prihlásený žiak, **bez akýchkoľvek práv navyše**. Všetko rozhoduje služba.
- **Žiak ju nemôže ukončiť:** ikona dostane ochranu, ktorá jej používateľovi dovolí len vidieť ju. Ukončiť ju zo
  Správcu úloh, cez `taskkill` alebo PowerShell, pozastaviť, ladiť či zmeniť jej ochranu skončí hlásením „prístup
  odmietnutý“. To isté platí pre vlákna, ktoré ikona vytvorí neskôr. Zatvoriť ju nedá ani správa oknu. Overené testom
  na Windows: to isté konto bez skupiny Správcovia ikonu ukončiť nemôže, správca áno.
- Službu samu žiak zastaviť nemôže (beží ako systém). Správca môže ikonu aj službu ukončiť vždy.
- Inštaluje sa spolu so službou (`polaris-agent instalovat-sluzbu`, keď je `polaris-ziak.exe` vedľa agenta).
- Obrázok ikony (polárka na tmavomodrom kruhu) kreslí kód, žiadny obrázkový súbor.

## Ukážky

- `recepty/ukazka-kancelarsky.json`: LibreOffice, 7-Zip, VLC, dve nastavenia Edge a školské záložky. Je to len
  otvorený softvér, vhodný pre školy.
- `recepty/ukazka-pieskovisko.json`: Windows Sandbox, dve šablóny na ploche (prísna a so sieťou) a služba tlače.
- `recepty/ukazka-relacia.json`: čistenie relácie konta `ziak`, klávesnice SK, CZ, EN, DE, viditeľné prípony súborov
  a Školský obchod s Audacity a Inkscape.

## Pripravuje sa

Ďalšie typy prvkov: tlačiarne, Wi-Fi, súbory, kontá s heslom ako tajomstvom.
Priradenia receptov skupinám a PC s životnosťou (do konca hodiny, dňa, týždňa) sú už naprogramované
v `Polaris.Recipes` (`Assignments`). Do prevádzky ich uvedie centrum v etape K3.
