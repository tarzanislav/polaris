# Centrum POLARIS

`polaris-centrum` je nástroj správcu pre nastavenia učebne. Pracuje len so súborom nastavení (`centrum.json`)
a so súbormi, ktoré mu zadáš. Nič neposiela po sieti a žiadne PC nemení. Beží vo Windows aj na Linuxe.

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

## Kľúče, podpisy a Zásoba

Podpis receptov: [recept.md – Podpis receptu](recept.md#podpis-receptu). Zásoba balíkov:
[recept.md – Zásoba balíkov](recept.md#zásoba-balíkov).

## Pripravuje sa

Zverejnenie receptov a rozvrhu pre skupiny PC tak, aby si ich agent na PC vedel sám prevziať a použiť v správnej
hodine; konzola s prehľadom učebne.
