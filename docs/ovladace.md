# Ovládače (etapa K5a)

Agent vie zistiť, ktorým zariadeniam chýba ovládač, **zálohovať ovládače hardvérovej zostavy** z referenčného PC
a **nainštalovať ich zo zálohy** na čerstvo nainštalovanom PC tej istej zostavy. Všetko vlastným kódom nad
systémovými rozhraniami Windows (tie isté, aké používa Správca zariadení): zariadenia cez `cfgmgr32`, úložisko
ovládačov cez `setupapi`, inštalácia cez `newdev` (`DiInstallDriver`), podpis cez `WinVerifyTrust`.

## Príkazy

```powershell
polaris-agent ovladace                       # zariadenia bez ovládača (kód 28 v Správcovi zariadení)
polaris-agent ovladace --vsetky              # všetky zariadenia, výrobca, verzia a dátum ovládača
polaris-agent ovladace zaloha D:\zalohy\MINI-PC --meno MINI-PC
polaris-agent ovladace overit D:\zalohy\MINI-PC
polaris-agent ovladace obnov  D:\zalohy\MINI-PC
```

- **`zaloha`** (správca): do nového alebo prázdneho priečinka skopíruje balíky ovládačov tretích strán, ktoré
  zariadenia tohto PC naozaj používajú (`--vsetky` = všetky balíky v úložisku, aj pre odpojené zariadenia).
  Ovládače, ktoré sú priamo súčasťou Windows, sa nezálohujú – nová inštalácia ich má. Meno zostavy dáva správca
  (`--meno`), z hardvéru sa nečíta.
- **`overit`**: skontroluje každý súbor zálohy podľa odtlačku SHA-256 (nič nemení, funguje kdekoľvek).
- **`obnov`** (správca): vyberie len balíky pre zariadenia tohto PC (podľa identifikátorov hardvéru z INF),
  ukáže ich a opýta sa (`--ano` bez otázky, `--vsetky` všetky balíky). Výsledok ide do denníka (`dennik`).

## Záloha

```
MINI-PC\
  ovladace.json        popis: meno zostavy, Windows, balíky, súbory s odtlačkami
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
