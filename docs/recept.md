# Recept POLARIS

Recept opisuje, čo má mať PC jedného profilu (napríklad KANCELÁRSKY). Je to textový súbor JSON. Má verziu
a **odtlačok SHA-256**, takže každé PC vie preukázať, že má presne schválenú verziu.

Agent recept nikdy nepoužije, kým nie je bez problémov (`polaris-agent overit`). Každú zmenu po vykonaní **overí
opätovným prečítaním stavu**. Chyba jedného prvku nezastaví ostatné.

## Príkazy (etapa K1: spúšťa správca na PC, bez siete)

```
polaris-agent overit RECEPT          kontrola receptu – nič nemení, funguje aj na Linuxe
polaris-agent odtlacok RECEPT        SHA-256 receptu (na schválenie verzie profilu)
polaris-agent kontrola RECEPT        čo sa na tomto PC líši od receptu – nič nemení
polaris-agent pouzi RECEPT [--ano]   urobí zmeny a každú overí (len správca)
polaris-agent dennik [POČET]         posledné behy na tomto PC
```

Návratový kód: `0` = v poriadku / PC je podľa receptu, `1` = rozdiely alebo chyby, `2` = zlý vstup, recept
s problémami alebo chýbajúce práva.

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

- Komentáre `//` sú povolené. Odtlačok ich ani formátovanie nezapočítava.
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
| `version` | verzia z WinGet (prázdne = najnovšia). K1 rozpoznáva len, či program je, nie jeho verziu |
| `installer` | alebo inštalátor zo zásoby: `file` (cesta vnútri priečinka receptu), `sha256`, `arguments`, `uninstallArguments` |

- Inštalátor s iným odtlačkom, než je v recepte, sa **nikdy nespustí**.
- Súbor `.msi` dostane `/qn /norestart`, ak `arguments` chýba.
- Odstránenie: cez WinGet, alebo tichou odinštaláciou, ktorú pozná Windows (Windows Installer, `QuietUninstallString`).
  Ak ju program nemá, doplň `installer.uninstallArguments` (napr. `/S`).
- Kódy 3010 a 1641 (hotovo, treba reštart) sa berú ako úspech. O výsledku rozhoduje vždy opätovné prečítanie stavu.

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

## Ukážka

`recepty/ukazka-kancelarsky.json`: LibreOffice, 7-Zip, VLC, dve nastavenia Edge a školské záložky. Je to len otvorený
softvér, vhodný pre školy.

## Pripravuje sa

Ďalšie typy prvkov: klávesnice a jazyky, služby, tlačiarne, Wi-Fi, súbory, kontá s heslom ako tajomstvom. Priradenia
receptov skupinám a PC s životnosťou (do konca hodiny, dňa, týždňa) sú už naprogramované v `Polaris.Recipes`
(`Assignments`). Do prevádzky ich uvedie centrum v etape K3.
