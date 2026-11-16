# Kanonický tvar receptu a odtlačok

Odtlačok receptu je **SHA-256 jeho kanonického tvaru**. Podpis konzoly podpisuje tento odtlačok. Kanonický tvar je
preto pevný a nezávisí od žiadnej knižnice ani verzie .NET. Píše ho vlastný kód `Polaris.Json`, pravidlá sú tieto:

1. Kódovanie **UTF-8 bez značky BOM**, žiadne medzery ani nové riadky mimo textov, žiadne komentáre.
2. **Poradie polí** je pevné, dané formátom (`schema`, `name`, `version`, `note`, `elements`; v prvku `type`, `id`,
   polia typu, `present`, `note`). Neurčuje ho poradie v súbore, ktorý napísal človek.
3. **Pole s predvolenou hodnotou sa nepíše** (napr. `"present": true`, `"memoryMb": 4096`, prázdny text). Pole pridané
   v novšej verzii s predvolenou hodnotou preto nezmení odtlačok staršieho receptu. Zmena predvolenej hodnoty by
   zmenila význam receptu bez zmeny odtlačku, preto je dovolená len s novým číslom schémy.
4. **Texty:** v úvodzovkách, znaky sa píšu priamo v UTF-8 („Škola“, nie `\u0160kola`). Skratkou sa píšu len `\"`,
   `\\` a riadiace znaky: `\b \t \n \f \r`, ostatné ako `\u00xx` (malé písmená). Text s osamotenou polovicou dvojice
   UTF-16 sa zapísať nedá.
5. **Čísla:** celé čísla desiatkovo, bez nuly na začiatku a bez znamienka `+`.
6. **Odtlačok:** SHA-256 bajtov UTF-8 kanonického tvaru, 64 malých šestnástkových znakov.

Dva recepty s rovnakým obsahom majú vždy rovnaký odtlačok, nech sú napísané akokoľvek (komentáre, poradie, medzery,
`\u0160` alebo `Š`). Test `JsonChecks.TheFingerprintOfARecipeIsPinned` drží pevný odtlačok jedného receptu. Ak
niekedy zlyhá, zmenil sa kanonický tvar a všetky podpisy v školách by prestali platiť: chybná je zmena, nie test.

Odtlačok sa dá overiť aj bez POLARIS: `polaris-agent odtlacok RECEPT` vypíše odtlačok,
`polaris-agent odtlacok RECEPT --kanonicky` kanonický text. Ten (bez nového riadku na konci) dá rovnaké číslo cez
`sha256sum` (Linux) alebo `Get-FileHash` (Windows).

## Čítanie

- Súbory písané ľuďmi (recepty) môžu mať komentáre `//` a `/* */` a čiarku za poslednou položkou.
- Správy medzi programami (Školský obchod, denník) sú čistý JSON podľa RFC 8259.
- **Neznáme pole je chyba.** Agent povie riadok, znak a najbližšie známe meno (`"netwroking"` → „nemyslel si
  networking?“). Preklep tak nikdy neprejde potichu.
- Odmietne sa aj: pole dvakrát v jednom objekte, súbor, ktorý nie je platné UTF-8, číslo s nulou na začiatku, text
  v apostrofoch, cesta Windows s jednoduchým `\`, viac ako 64 vnorených úrovní.
