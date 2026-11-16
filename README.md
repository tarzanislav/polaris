# POLARIS

Otvorený, slobodný softvér na **zmrazenie systému** (reboot-to-restore) a nasadzovanie obrazov – pre Windows 11,
neskôr Linux a Android. Počas práce sa s počítačom pracuje naplno; po reštarte (alebo podľa plánu) sa vráti do čistého
stavu aj s nastaveniami.

Princíp: Windows sa spúšťa z **vrstveného virtuálneho disku VHDX**. Vrstva profilu sa počas práce nikdy nezapisuje,
všetky zmeny idú do tenkej vrstvy relácie, ktorá sa pri reštarte zahodí. Tri vrstvy relácie sa striedajú v kruhu tak,
aby predvolený štart nikdy neukazoval na vrstvu, ktorá už bežala.

## Stav

**Smer v2 (od 16. 11. 2026): POLARIS nezasahuje pod systém, ale riadi zabudované mechanizmy Windows a Linuxu podľa
podpísaných receptov.** Etapa **K1** je vo vývoji: `polaris-agent` porovná PC s receptom (programy cez WinGet alebo
overený inštalátor, hodnoty registra v HKLM, záložky pre Edge a Chrome), zmeny urobí a každú overí. Popis formátu:
[docs/recept.md](docs/recept.md), ukážka: [recepty/ukazka-kancelarsky.json](recepty/ukazka-kancelarsky.json).

Pôvodný smer v1 (vrstvy disku) je pozastavený:

**Etapa E0 – laboratórium vrstiev (vo vývoji, zatiaľ nie na použitie).** `polaris-lab.exe` prevedie bežiaci Windows do
vrstiev (bez USB a bez WinPE, pôvodný Windows ostáva ako poistka), striedá čisté vrstvy, spúšťa automatickú skúšku
reštartov a píše správu.

## Zásady

- Iba vlastný originálny kód, žiadne cudzie knižnice ani balíky NuGet; rozhrania systému Windows sa volajú priamo.
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
