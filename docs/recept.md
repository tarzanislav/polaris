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
polaris-agent nespat 3h              údržba: PC nezaspí daný čas (90m, 1h30m …, najviac 24h); koniec ju skončí; bez času stav
polaris-agent spustanie              programy, ktoré sa spúšťajú samy po prihlásení, a ktoré vypol POLARIS (vratit ich zapne)
polaris-agent meranie [POČET]        merania prihlásení: čas do pracovnej plochy, pamäť PC a programov po 2 minútach
```

**Údržba bez spánku.** `nespat` zapíše koniec údržby do registra; služba POLARIS agent do pol minúty požiada Windows,
aby PC nezaspalo (požiadavka „POLARIS: údržba – PC nezaspí do …“ je vidno v `powercfg /requests`), a po čase ju pustí.
Rovnako drží PC hore, kým sama pracuje: počas receptu, inštalácie zo Školského obchodu a ovládačov. Zastaví len
zaspanie po nečinnosti – PC, ktoré niekto uspí sám, zaspí.

**Meranie prihlásenia.** Keď ikona žiaka dostane miesto na paneli úloh, povie službe, že pracovná plocha je hotová.
Služba si od Windows zistí čas prihlásenia relácie a po 2 minútach zmeria pamäť: celé PC, programy relácie (10
najväčších) a služby. Jeden záznam denníka na prihlásenie – podklad na porovnanie pred a po trezore profilu.

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

- `kind`: `String`, `ExpandString`, `DWord`, `QWord`, `MultiString` (riadky oddelené `\n`), `Binary` (bajty
  šestnástkovo, napr. `"90 12 03 80 10 00 00 00"`; medzery a čiarky medzi bajtmi sú dovolené, najviac 4096 bajtov).
  Čísla sa píšu desiatkovo alebo `0x…`.
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
  používateľov, sieť, POLARIS, DWAgent – vzdialený prístup DWService – a ďalšie) sa receptom nedajú vypnúť ani
  zastaviť, zapnúť áno.
- `name` s `*` na konci = **všetky služby, ktorých meno sa tak začína** (napr. `GoogleUpdater*` – takéto služby majú
  v mene verziu); začiatok aspoň 4 znaky, len `Disabled` alebo `Manual`, nesmie zahŕňať chránenú službu.
- `physicalOnly: true` = len na fyzickom PC; na virtuálnom (podľa výrobcu a modelu vo firmvéri) sa prvok vynechá –
  napr. integračné služby Hyper-V pre hosťa (`vmic*`).
- Služba, ktorú daný Windows nemá, sa pri `Disabled` ráta ako vypnutá – jeden recept pre všetky PC.
- **Pôvodný stav:** pred prvou zmenou si POLARIS zapamätá, ako sa služba spúšťala. `polaris-agent sluzby` ho vypíše,
  `polaris-agent sluzby vratit MENO | --vsetky` ho vráti (kým je služba v recepte, recept ju pri ďalšom použití zmení
  znova).

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
- **`Restore` – profil z trezoru:** po každom odhlásení (a pri vypínaní PC) sa profil porovná s trezorom konta a vráti
  sa len to, čo sa líši – súbory, priečinky, prepojenia, register konta, so svojimi oprávneniami. Pár sekúnd, profil
  ostáva, takže ďalšie prihlásenie je rýchle (bez „Prípravy systému“) a programy sa otvárajú bez úvodných okien.
  Bez platného trezoru sa profil zmaže ako pri `AtSignOut`.

**Trezor profilu** (príprava raz, správca):

```
polaris-agent trezor priprav ziak    profil sa nečistí, kým ho neuložíš
  → prihlás sa ako žiak, spusti programy, prejdi ich úvodné okná, nastav, čo treba, odhlás sa
polaris-agent trezor ulozit ziak     uloží profil, ako je (nikto nesmie byť prihlásený)
polaris-agent trezor                 stav trezorov; trezor zmazat KONTO ho zmaže
```

- Trezor je jeden archív agenta (`ProgramData\POLARIS\agent\trezor`, len systém a správcovia) cez zálohovacie
  rozhranie Windows (BackupRead/BackupWrite), každá položka s odtlačkom SHA-256. Dočasné súbory a vyrovnávacie pamäte
  prehliadačov sa neukladajú – pri obnove sa len zmažú.
- Počas obnovy je konto na chvíľu vypnuté (nikto sa neprihlási do rozpracovaného profilu); po výpadku prúdu ho služba
  pri štarte zapne. Keď obnova zlyhá, profil sa zmaže (čistý štart) – nikdy neostane polovičný.
- Trezor neplatí, keď profil vznikol znova (zmazaný a vytvorený) alebo konto je iné s tým istým menom – vtedy sa profil
  maže a `polaris-agent trezor` povie, že treba uložiť znova. Po aktualizácii Windows odporučí uložiť znova.

### Politiky cez miestnu skupinovú politiku

Niektoré politiky (napr. miniaplikácie `HKLM\SOFTWARE\Policies\Microsoft\Dsh`) chráni ovládač Windows **UCPD** – zapísať
ich smie len program podpísaný Microsoftom. Keď Windows zápis politiky odmietne, POLARIS ju zapíše do **miestnej
skupinovej politiky** (`%windir%\System32\GroupPolicy\Machine\Registry.pol` a `gpt.ini`, ako `gpedit.msc`) a spustí
`gpupdate` – hodnotu potom nastaví sám Windows. Ochranu POLARIS nevypína ani neobchádza.

Prvok `user-registry` s cestou `Software\Policies\…` ide **aj do miestnej politiky používateľov**
(`…\GroupPolicy\User\Registry.pol`): Windows ju nastaví pri každom prihlásení každého konta – aj v profiloch, ktoré
už existujú (trezor žiaka), kam predloha nových profilov nedosiahne.

### `folder`: priečinok a práva

```json
{ "type": "folder", "id": "data-ziaci", "path": "C:\\Data\\Ziaci", "access": [ { "who": "Users", "rights": "Modify" } ] }
```

- Priečinok vznikne, ak chýba; práva sa **pridajú** k tým, čo má (dedia ich súbory a priečinky vnútri), nič iné sa
  neberie. `who`: `Users` (všetci používatelia PC), `AuthenticatedUsers` alebo meno lokálneho konta; `rights`: `Read`
  alebo `Modify` (čítať, zapisovať, mazať – nie meniť práva).
- Nikdy v systémových priečinkoch, v profiloch (len pod `C:\Users\Public`) ani v priečinkoch programov – žiak by mohol
  zmeniť program, ktorý neskôr spustí správca. Priečinok sa nikdy nemaže: `present: false` vezme len práva od receptu.

### Softvér: len stabilné verzie

Kontrola receptu odmietne balík WinGet z testovacieho kanála (`…Beta`, `…Nightly`, `…Preview`, `…Insiders`, `…Canary`,
`…Dev` …) aj testovaciu verziu (`3.0-beta2`, `1.2rc1` …) – v programoch aj v Školskom obchode. Programy sa inštalujú pre
celé PC ako systém; žiak práva správcu nepotrebuje ani dočasne. Ukážkové sady: `ukazka-softver-zaklad.json` (Firefox ESR
po slovensky, Chrome, Brave s politikami, 7-Zip, Double Commander, Java Temurin 21, PDF24 bez online nástrojov,
SumatraPDF, Notepad++, VLC, IrfanView s pluginmi, Freeplane, priečinok `C:\Data\Ziaci`), `ukazka-softver-multimedia.json`,
`ukazka-softver-kancelaria.json` (LibreOffice pre PC bez Office), `ukazka-softver-ucitel.json` (PDFgear len pre učiteľov).

### `account`: konto žiaka

```json
{ "type": "account", "id": "konto-ziak", "name": "ziak", "noPassword": true, "autoLogon": true }
```

- Konto vznikne, ak chýba (štandardné, nikdy správca – konto správcu recept odmietne). **Heslo si používateľ nemôže
  zmeniť a nevyprší** – žiak nezamkne spoločné konto ostatným.
- `noPassword: true` – **bez hesla**: prihlásenie jedným klikom. Windows potom také konto pustí **len priamo pri PC,
  nikdy cez sieť** (POLARIS drží `LimitBlankPasswordUse` zapnuté). Heslo, ktoré by mu niekto neskôr nastavil, POLARIS
  zistí (podľa času zmeny hesla, nie skúšaním prihlásenia) a vráti.
  Aby to Windows dovolil, POLARIS na takom PC vypne **zložitosť hesiel a najmenšiu dĺžku hesla** (nástrojom Windows
  secedit, len tieto dve hodnoty; rozhodnutie správcu 17. 11. 2026). Heslá, ktoré už existujú, ostávajú; jednoduchšie
  môžu byť len heslá nastavené odteraz. Späť to POLARIS sám nedáva.
- `autoLogon: true` – **po zapnutí sa PC prihlási samo** (len konto bez hesla; žiadne heslo sa nikam neukladá; v recepte
  najviac jedno). Kým je na konte niekto prihlásený alebo jeho profil čaká na vrátenie z trezoru, automatické
  prihlásenie je vypnuté – PC sa nikdy neprihlási do rozpracovaného profilu. Správca sa dostane k PC odhlásením žiaka.
- Kontá recept nemaže; prvok vynechaný = POLARIS konto prestane spravovať (automatické prihlásenie vypne).

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

- Hodnota v časti registra používateľa (bez `HKCU\`), zapísaná do predlohy profilov. Údaje ako pri `registry`,
  aj `Binary` – napríklad vizuálne efekty (`Control Panel\Desktop`, `UserPreferencesMask`).

### `sounds`: zvuková schéma

```json
{ "type": "sounds", "id": "ticho" }
```

- Predloha profilov dostane schému **Bez zvukov**: POLARIS prejde každú udalosť Windows a programov (to isté, čo
  dialóg Zvuk). Zvuk pri spustení Windows vypína politika `DisableStartupSound` (ukážka Základ).

### `computer`: pracovná skupina a časové pásmo

```json
{ "type": "computer", "id": "pocitac", "workgroup": "SKOLA", "timeZone": "Central Europe Standard Time" }
```

- Nastaví sa len to, čo je vyplnené. Prvok sa nedá odstrániť (PC má vždy nejakú skupinu a pásmo).
- `workgroup`: 1 až 15 znakov A-Z, 0-9, `-`, `_` (nie len číslice), rovnaká ako pracovná skupina Samby na serveri.
  Zmena sa prejaví po reštarte. **PC v doméne recept z domény nikdy nevyberie** – len to ohlási.
- `timeZone`: meno pásma, ako ho pozná Windows (`Central Europe Standard Time`, `GMT Standard Time` …). Pravidlá
  pásma (letný čas) berie agent z Windows, nie z receptu.

### `updates`: Windows Update cez politiky

```json
{ "type": "updates", "id": "aktualizacie", "activeHoursStart": 7, "activeHoursEnd": 17, "deferFeatureDays": 30 }
```

- `activeHoursStart`, `activeHoursEnd`: aktívne hodiny (0 – 23), v nich Windows PC kvôli aktualizáciám nereštartuje.
  Najviac 18 hodín (obmedzenie Windows), môžu ísť aj cez polnoc.
- `deferFeatureDays` (0 – 365): nová verzia Windows príde o toľko dní neskôr; `deferQualityDays` (0 – 30): mesačné
  aktualizácie neskôr.
- `targetRelease` (napr. `25H2`): PC ostane na tejto verzii Windows 11, kým recept nepovie inú.
- `excludeDrivers: true`: ovládače nechodia cez Windows Update.
- Zapisujú sa politiky v `HKLM\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate` (rovnaké ako editor skupinovej
  politiky). **Nevyplnené pole = politika sa odstráni** a rozhoduje Windows sám; `present: false` odstráni všetky.

### `power`: napájanie cez politiky

```json
{ "type": "power", "id": "napajanie", "sleepMinutes": 60, "displayMinutes": 15, "hibernateMinutes": 0,
  "powerButton": "ShutDown", "fastStartup": false }
```

- Časy v minútach, `0` = nikdy, najviac týždeň: `sleepMinutes` (spánok), `displayMinutes` (vypnutie obrazovky),
  `hibernateMinutes` (hibernácia).
- `powerButton`, `lidClose` (zatvorenie veka notebooku): `Nothing`, `Sleep`, `Hibernate`, `ShutDown`.
- `wakePassword`: pýtať heslo po prebudení.
- Platí v sieti aj na batérii. Politiky (`HKLM\SOFTWARE\Policies\Microsoft\Power\PowerSettings`) používateľ
  zmeniť nemôže. Nevyplnené pole = politika sa odstráni.
- `hibernation: false` vypne hibernáciu úplne (jej súbor zmizne pri ďalšom štarte; bez nej nie je ani rýchle
  spustenie). Ako `fastStartup` je to hodnota Windows – zapíše sa len, keď je v recepte.
- `fastStartup: false` vypne rýchle spustenie Windows: vypnuté PC potom naozaj začína odznova a Wake on LAN funguje
  spoľahlivejšie. Toto je hodnota samotného Windows, nie politika – zapíše sa len, keď je v recepte, a nikdy sa
  neodstraňuje.

- `networkFullPower: true` = **sieť naplno**: Wi-Fi bez úspory energie (najvyšší výkon) a linky PCI Express (kde
  sedia sieťové karty) bez úspory – práca na diaľku nečaká, kým sa karta prebudí. Wake on LAN to nemení: ten je
  v ovládači karty a vo firmvéri. Vynechané = rozhoduje plán napájania Windows.

### `startup`: programy, ktoré sa spúšťajú samy

```json
{ "type": "startup", "id": "spustanie", "allow": ["DWAgent*", "dwag*", "RtkAud*", "SynTP*", "ETD*"] }
```

- Týka sa programov **pre všetkých používateľov**: `Run` počítača, jeho 32-bitový `Run` a spoločný priečinok
  „Po spustení“. Všetky okrem povolených sa **vypnú ako v Správcovi úloh** (položka ostane, len jej značka
  v `…\Explorer\StartupApproved` povie „vypnuté“) – Správca úloh ich ukáže ako vypnuté.
- `allow`: meno položky (ako ho ukáže `polaris-agent spustanie`) alebo meno programu bez `.exe`; s `*` na konci
  začiatok mien (aspoň 3 znaky). Ikona Zabezpečenia Windows (`SecurityHealth`) sa spúšťa vždy. Prázdny zoznam =
  spúšťa sa len ona.
- Služba si zoznam drží a **každých 5 minút ho skontroluje znova** – vypne aj program, ktorý pribudol neskôr
  (inštaláciou receptom, zo Školského obchodu alebo ručne). Zápis v denníku `(spúšťanie)`.
- **Pôvodný stav:** pred vypnutím si POLARIS zapamätá značku položky. `polaris-agent spustanie vratit MENO | --vsetky`
  ju vráti (kým je prvok v recepte, služba program o chvíľu vypne znova – pridaj ho do `allow`). `present: false`
  zapne všetko, čo POLARIS vypol, a služba prestane zoznam strážiť.
- Programy jednotlivých kont (ich vlastný `Run`, napr. OneDrive či Teams) sem nepatria – tie sa nastavia v šablóne
  profilu (trezor, K6b).

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
- Po inštalácii vytvorí služba **odkaz v ponuke Štart pre všetkých** (priečinok „Školský obchod“) – inštalátor
  spustený systémom si odkaz často dá len do systémovej ponuky Štart, kde ho žiak nevidí. Program nájde podľa jeho
  záznamu v nainštalovaných programoch (ikona programu, inak .exe s jeho menom v priečinku inštalácie). Odkaz zapíše
  samotný shell Windows (IShellLink, volaný priamo) a pri odinštalovaní ho služba zmaže.
- Žiak obchod používa cez **ikonu POLARIS** pri hodinách (pozri nižšie), alebo príkazom v termináli.

## Ikona pre žiaka (`polaris-ziak.exe`)

Ikona POLARIS v oblasti oznámení (pri hodinách) v každej prihlásenej relácii:

- **Školský obchod na klik:** ponuka programov; po kliknutí oznámenie „Inštalujem…“ a potom výsledok. Nainštalovaný
  program má značku ✓ a klik ho **spustí**.
- **Oznámenia vo vlastnom okienku** nad hodinami (8 sekúnd, klik ho zavrie): Windows bublinové oznámenia ikon
  v prvej hodine nového profilu stlmí („quiet time“) – a profil žiaka je vždy nový.
- **Upozornenie na čistenie:** pri prihlásení a v popise ikony („Po odhlásení sa tento profil vymaže – prácu si
  ukladaj do Úschovne alebo na USB“).
- **Pieskovisko:** spustenie šablón z plochy.

Spúšťa ju **služba POLARIS agent** (nie zoznam programov po prihlásení, ten by žiak vedel obísť):

- Každých 5 sekúnd skontroluje všetky prihlásené relácie. **Ikonu, ktorá chýba alebo padla, spustí znova.** Keď ikona
  padá hneď po štarte, po troch pádoch počká 5 minút a zapíše to do denníka (raz, nie stále dookola).
- Ikona beží ako prihlásený žiak, **bez akýchkoľvek práv navyše**. Všetko rozhoduje služba.
- **Žiak ju nemôže ukončiť:** ikona dostane ochranu, ktorá jej používateľovi dovolí len vidieť ju (vláknam, ktoré
  vytvorí neskôr, všetko okrem ukončenia, pozastavenia a zmeny ochrany – prísnejšia ochrana zastavila ikonu pri štarte;
  `polaris-agent skuska-ikony` to overí v postavení žiaka). Ukončiť ju zo
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
- `recepty/ukazka-relacia.json` (v3): konto `ziak` bez hesla s automatickým prihlásením, profil z trezoru po odhlásení, klávesnice SK, CZ, EN, DE, viditeľné prípony súborov
  a Školský obchod s Audacity a Inkscape.
- `recepty/ukazka-zaklad.json`: pracovná skupina SKOLA, stredoeurópsky čas, aktualizácie mimo vyučovania
  (7 – 17 h, nová verzia Windows o 30 dní neskôr), napájanie pre učebňu (tlačidlo vypne PC, bez rýchleho spustenia),
  Edge bez behu na pozadí a bez uvítania, OneDrive vypnutý politikou a nové profily si ho neinštalujú, prihlásenie
  žiaka bez otázok o súkromí a bez úvodnej animácie, vizuálne efekty pre výkon podľa voľby správcu (vyhladené písmo,
  miniatúry, Peek, priesvitný výber a tiene popisov ikon ostávajú; animácie a tiene okien nie) (profil žiaka sa maže, bez toho by sa pýtali pri každom prihlásení).

- `recepty/ukazka-zaklad.json` v6 navyše: napájanie bez spánku (PC ostane dostupné na diaľku) a bez hibernácie,
  aplikácie Store bez behu na pozadí, bez miniaplikácií, Game Baru, reklám a návrhov, aktualizácie zdieľané v učebni,
  telemetria na minimum, hlásenie chýb bez okien a bez odosielania, bez automatického prehrávania z USB, ticho,
  ikony na ploche a Prieskumník podľa správcu.
- `recepty/ukazka-zaklad.json` v7 navyše: sieť naplno a po prihlásení sa samy spúšťajú len povolené programy
  (vzdialená správa DWAgent, zvuk Realtek, touchpady Synaptics a ELAN; vždy Zabezpečenie Windows).
- `recepty/ukazka-sluzby.json`: služby pre PC žiakov – vypnuté, čo v učebni nemá čo bežať (telemetria, Xbox, mapy,
  hotspot, vzdialený register, ohlasovanie v sieti, indexovanie …), ručne Pomocník IP a asistent kompatibility.
- `recepty/ukazka-slabe-pc.json`: navyše pre staršie PC – bez efektov priehľadnosti.

Ovládače (zistenie, záloha zostavy, obnova) sú v `docs/ovladace.md`.

## Pripravuje sa

Ďalšie typy prvkov: Microsoft Defender, tlačiarne, Wi-Fi, súbory, kontá s heslom ako tajomstvom.
Priradenia receptov skupinám a PC s životnosťou (do konca hodiny, dňa, týždňa) sú už naprogramované
v `Polaris.Recipes` (`Assignments`). Do prevádzky ich uvedie centrum v etape K3.
