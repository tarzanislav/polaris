# Ovládače (etapa K5a)

Agent vie zistiť, ktorým zariadeniam chýba ovládač, **zálohovať ovládače hardvérovej zostavy** z referenčného PC
a **nainštalovať ich zo zálohy** na čerstvo nainštalovanom PC tej istej zostavy. Všetko vlastným kódom nad
systémovými rozhraniami Windows (tie isté, aké používa Správca zariadení): zariadenia cez `cfgmgr32`, úložisko
ovládačov cez `setupapi`, inštalácia cez `newdev` (`DiInstallDriver`), podpis cez `WinVerifyTrust`.

## Kam idú zálohy

Ovládače sa **nekopírujú na disk PC** – sú pre iné PC tej istej zostavy a disk PC je práve to, čo nová inštalácia
zmaže. Zálohy idú do **priečinka záloh**:

- nastaveného správcom: `polaris-agent ovladace priecinok E:\Ovladace` alebo zdieľaný priečinok
  `\\server\zdielanie\Ovladace` (`--zrusit` nastavenie zruší),
- inak predvolene do `POLARIS-ovladace` na pripojenom **USB kľúči**.

V priečinku záloh si záloha sama vytvorí podpriečinok s menom **hardvérovej zostavy**: výrobca a model z firmvéru
PC a vydanie Windows, napr. `Výrobca Model - Windows 11 Pro`. Ďalšia záloha tej istej zostavy staršiu neprepíše:
dostane k menu deň, napr. `Výrobca Model - Windows 11 Pro (2026-11-19)`. Disk tohto PC priečinok záloh byť nemôže;
výnimka je `--aj-na-tomto-pc` (napr. externý USB disk, ktorý sa Windows hlási ako pevný disk).

Neskôr (K5b) zálohy robí a drží **konzola na svojom disku** (predvolený priečinok, dá sa zmeniť) a ovládače sama
pošle na PC tej istej zostavy – aj aktuálnejšie, ak ich nájde.

## Príkazy

```powershell
polaris-agent ovladace                       # zariadenia bez ovládača (kód 28 v Správcovi zariadení)
polaris-agent ovladace --vsetky              # všetky zariadenia, výrobca, verzia a dátum ovládača
polaris-agent ovladace priecinok             # priečinok záloh a meno tejto zostavy
polaris-agent ovladace priecinok \\server\zdielanie\Ovladace
polaris-agent ovladace zaloha                # do priečinka záloh, podpriečinok podľa zostavy
polaris-agent ovladace overit                # overí všetky zálohy v priečinku záloh
polaris-agent ovladace obnov                 # nájde zálohu tejto zostavy a nainštaluje, čo PC chýba
```

- **`zaloha`** (správca): do nového priečinka skopíruje balíky ovládačov tretích strán, ktoré zariadenia tohto PC
  naozaj používajú (`--vsetky` = všetky balíky v úložisku, aj pre odpojené zariadenia). Ovládače, ktoré sú priamo
  súčasťou Windows, sa nezálohujú – nová inštalácia ich má. Meno zostavy je z firmvéru a Windows; iné dá `--meno`.
  S cestou (`ovladace zaloha CESTA`) ide záloha presne do toho (prázdneho) priečinka.
- **`overit`**: skontroluje každý súbor zálohy podľa odtlačku SHA-256 (nič nemení, funguje kdekoľvek); s priečinkom
  záloh všetky zálohy v ňom.
- **`obnov`** (správca): v priečinku záloh (alebo v zadanom) nájde zálohu **tej istej zostavy** – rovnaký model,
  prednostne to isté vydanie Windows, najnovšiu. Vyberie len balíky pre zariadenia tohto PC (podľa identifikátorov
  hardvéru z INF), ukáže ich a opýta sa (`--ano` bez otázky, `--vsetky` všetky balíky). Výsledok ide do denníka.

## Záloha

```
Výrobca Model - Windows 11 Pro\
  ovladace.json        popis: meno zostavy, model, Windows, balíky, súbory s odtlačkami
  001-heci\            jeden priečinok = jeden balík ovládača (INF, katalóg .cat, súbory)
  002-rt640x64\
```

Pri každom balíku je v `ovladace.json` trieda, výrobca, verzia a dátum (z INF), kto podpísal katalóg,
identifikátory hardvéru, pre ktoré je (64-bit Windows), zariadenia referenčného PC a **odtlačok každého súboru**.
`overit` vypíše aj odtlačok celej zálohy.

## Bezpečnosť obnovy

1. Popis zálohy sa skontroluje: žiadna cesta mimo priečinka balíka, platné odtlačky.
2. Balík sa skopíruje do chráneného priečinka agenta (`ProgramData\POLARIS\agent\ovladace`, len systém
   a správcovia) – **len súbory zo zoznamu**.
3. Tam sa každý súbor znova overí podľa odtlačku a **podpis katalógu** musí byť platný.
4. Až potom ho dostane Windows (ten sám tiež odmietne nepodpísaný ovládač). Kópia sa potom zmaže.

Zmenený, chýbajúci alebo nepodpísaný súbor = balík sa nenainštaluje, ostatné pokračujú.

## Pripravuje sa (K5b)

Zálohy v Zásobe pre skupiny PC, prvok receptu `drivers`, hľadanie aktuálnejších a vhodnejších ovládačov
(Windows Update vrátane voliteľných, katalóg Microsoft Update), stav ovládačov v hláseniach a v konzole.
Dovtedy pomôže `tools/windows/doplnit-ovladace.cmd` (doplní chýbajúce ovládače z katalógu).
