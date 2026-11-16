# POLARIS

Otvorený, slobodný softvér na **zmrazenie systému** (reboot-to-restore) a nasadzovanie obrazov – pre Windows 11,
neskôr Linux a Android. Počas práce sa s počítačom pracuje naplno; po reštarte (alebo podľa plánu) sa vráti do čistého
stavu aj s nastaveniami.

Princíp: Windows sa spúšťa z **vrstveného virtuálneho disku VHDX**. Vrstva profilu sa počas práce nikdy nezapisuje,
všetky zmeny idú do tenkej vrstvy relácie, ktorá sa pri reštarte zahodí. Tri vrstvy relácie sa striedajú v kruhu tak,
aby predvolený štart nikdy neukazoval na vrstvu, ktorá už bežala.

## Stav

**Smer v2 (od 16. 11. 2026): POLARIS nezasahuje pod systém, ale riadi zabudované mechanizmy Windows a Linuxu podľa
podpísaných receptov.** Etapy **K1 a K2** sú vo vývoji: `polaris-agent` porovná PC s receptom (programy cez WinGet
alebo overený inštalátor, hodnoty registra, záložky, služby, súčasti Windows, klávesnice a predloha profilov), zmeny
urobí a každú overí. Služba POLARIS agent čistí relácie spoločného konta žiaka a prevádzkuje **Školský obchod**
(žiak si nainštaluje schválený program, po relácii zmizne). Na plochu dáva **šablóny Pieskoviska** (Windows Sandbox). Žiak má **ikonu POLARIS** pri hodinách (obchod na klik,
upozornenie na čistenie profilu); služba ju udržiava v chode a žiak ju ukončiť nemôže.
Popis formátu: [docs/recept.md](docs/recept.md), ukážky: [recepty/](recepty/).

Pôvodný smer v1 (vrstvy disku) je pozastavený:

**Etapa E0 – laboratórium vrstiev (vo vývoji, zatiaľ nie na použitie).** `polaris-lab.exe` prevedie bežiaci Windows do
vrstiev (bez USB a bez WinPE, pôvodný Windows ostáva ako poistka), striedá čisté vrstvy, spúšťa automatickú skúšku
reštartov a píše správu.

## Zásady

- Iba vlastný originálny kód, žiadne cudzie knižnice ani balíky NuGet; rozhrania systému Windows sa volajú priamo.
  Aj JSON je vlastný (`Polaris.Json`): odtlačky a podpisy receptov nezávisia od žiadnej knižnice
  ([kanonický tvar](docs/kanonicky-tvar.md)). Z cudzieho sa berie len to, čo sa inak nedá: kryptografia a súčasti
  systému.
- Konfigurácia štartu sa číta len zo štruktúrovaného tvaru (nikdy z preloženého textu nástrojov).
- Žiadna telemetria.

## Zostavenie

```
dotnet build Polaris.slnx -c Release
dotnet run --project tests/Polaris.Tests -c Release
```

`polaris-lab.exe` sa zostavuje natívne (NativeAOT) pre Windows x64 v GitHub Actions.

## Licencia

Apache-2.0 – pozri [LICENSE](LICENSE).
