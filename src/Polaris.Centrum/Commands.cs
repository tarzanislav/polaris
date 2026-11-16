using System.Globalization;
using System.Text;
using Polaris.Core;
using Polaris.Net;
using Polaris.Recipes;

namespace Polaris.Centrum;

/// <summary>A mistake in what the person typed; shown as one Slovak line, nothing is saved.</summary>
public sealed class UsageException(string message) : Exception(message);

/// <summary>The commands of polaris-centrum. Every command loads the settings file; commands that change something save
/// it (safely, with a numbered backup). Nothing else is touched.</summary>
public static partial class Commands
{
    public const string DefaultFile = "centrum.json";

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public const string Help = """
        polaris-centrum – nastavenia centra POLARIS (zvonenie, skupiny, PC, profily, rozvrh, voľné dni, kľúče, zásoba)
        Pracuje len so súborom nastavení a súbormi, ktoré mu zadáš – nič neposiela po sieti a žiadne PC nemení.

          [--subor CESTA]                     súbor nastavení (inak centrum.json v aktuálnom priečinku)

          zvonenie                            vypíše zvonenie
          zvonenie ČÍSLO OD DO                nastaví hodinu, napr. zvonenie 1 8:00 8:45
          zvonenie zrus ČÍSLO                 zruší hodinu zo zvonenia
          profily                             vypíše profily (recepty) s verziou, odtlačkom a podpisom
          profil MENO RECEPT.json [POPIS…]    profil = pomenovaný recept (napr. profil KANCELARSKY recepty/kancelarsky.json)
          skupiny                             vypíše skupiny PC
          skupina MENO PROFIL                 pridá skupinu alebo jej zmení predvolený profil
          pc                                  vypíše PC
          pc MENO SKUPINA | pc MENO -         pridá PC do skupiny, presunie ho, alebo ho vyradí
          hodina DEŇ ČÍSLO SKUPINA PROFIL     zapíše hodinu do rozvrhu, napr. hodina po 1 ucebna KANCELARSKY
          hodina DEŇ ČÍSLO SKUPINA -          vymaže hodinu z rozvrhu
          zmena DÁTUM ČÍSLO SKUPINA PROFIL    jednorazová zmena (PROFIL - ju zruší)
          volno OD [DO]                       voľný deň alebo voľné dni (celý deň predvolený profil)
          volno zrus OD [DO]                  zruší voľné dni
          rozvrh [SKUPINA]                    týždenný rozvrh
          den [DÁTUM] [SKUPINA]               kedy sa PC v daný deň prepnú (inak dnes)
          kontrola                            nájde chyby v nastaveniach a receptoch (návratový kód 1, keď nejaké sú)
          zverejnit KLUC [--server https://SERVER:8443 ODTLAČOK]
                                              podpíše a zverejní plány skupín a ich recepty (priečinok zverejnene);
                                              so --server ich aj odovzdá centru na serveri
          server doverovat KONZOLA.verejny.json   centrum na serveri prijme odovzdania od tejto konzoly; server konzoly
          server certifikat NÁZOV…            certifikát centra (vypíše odtlačok, ktorý sa zadá na PC)
          server [--port 8443] [--zasoba CESTA]   centrum na sieti: HTTPS, len na čítanie (zverejnené plány, recepty, zásoba)
          kluc novy MENO                      nový kľúč konzoly: MENO.kluc (súkromný, chránený heslom) a MENO.verejny.json
          podpisat RECEPT KLUC                podpíše recept (vznikne RECEPT.podpis); PC s dôverou k tejto konzole ho prijmú

          zasoba pridat ZÁSOBA SÚBOR [voľby]  pridá inštalátor (.msi alebo .exe) do Zásoby balíkov a vypíše, čo dať do receptu
              --id ID  --nazov NÁZOV  --verzia V  --argumenty "…"  --odinstalovanie "…"  --meno MENO [--zaciatok]
              (.exe potrebuje --argumenty tichej inštalácie a --meno z ponuky Programy; .msi sa číta sám, len vo Windows)
          zasoba zoznam ZÁSOBA                balíky v zásobe
          zasoba overit ZÁSOBA                overí popisy a odtlačky všetkých inštalátorov (kód 1 pri probléme)

        DEŇ: po ut st št pi so ne (alebo 1–7)   DÁTUM: dnes, zajtra, 16.11.2026, 16.11., 2026-11-16
        """;

    /// <summary>Runs one command. Returns 0 = done, 1 = the check found problems, 2 = wrong input or unreadable file.</summary>
    public const int MinPassword = 12;

    /// <summary>The password from POLARIS_HESLO, or typed without showing it.</summary>
    public static string? ReadSecret(string prompt)
    {
        if (Environment.GetEnvironmentVariable("POLARIS_HESLO") is { Length: > 0 } fromEnvironment)
        {
            return fromEnvironment;
        }

        Console.Write(prompt);
        if (Console.IsInputRedirected)
        {
            return Console.ReadLine();
        }

        var typed = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                break;
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                typed.Length = Math.Max(0, typed.Length - 1);
            }
            else if (!char.IsControl(key.KeyChar))
            {
                typed.Append(key.KeyChar);
            }
        }

        Console.WriteLine();
        return typed.ToString();
    }

    public static int Run(string[] args, TextWriter output, DateTime localNow, DateTimeOffset utcNow, Func<string, string?>? secret = null, CancellationToken stop = default)
    {
        var words = args.ToList();
        var file = DefaultFile;
        var at = words.IndexOf("--subor");
        if (at >= 0)
        {
            if (at + 1 >= words.Count)
            {
                output.WriteLine("Chyba: po --subor chýba cesta k súboru.");
                return 2;
            }

            file = words[at + 1];
            words.RemoveRange(at, 2);
        }

        if (words.Count == 0 || words[0] is "pomoc" or "-h" or "--help" or "/?")
        {
            output.Write(Help);
            return 0;
        }

        try
        {
            if (words[0] is "kluc" or "podpisat")
            {
                return Keys(words, output, utcNow, secret ?? ReadSecret);
            }

            if (words[0] is "zasoba")
            {
                return Supply(words, output, utcNow);
            }

            var store = new ClassroomStore(file);
            var (data, note) = store.Load();
            if (note is not null)
            {
                output.WriteLine("Pozor: " + note);
            }

            var command = words[0];
            var rest = words.Skip(1).ToList();
            var today = DateOnly.FromDateTime(localNow);
            if (command == "kontrola")
            {
                return Check(data, store, rest, output);
            }

            if (command == "zverejnit")
            {
                return Publish(data, store, rest, output, utcNow, secret ?? ReadSecret);
            }

            if (command == "server")
            {
                return Server(store, rest, output, utcNow, stop);
            }

            var changed = command switch
            {
                "zvonenie" => Bells(data, rest, output),
                "profily" => ListProfiles(data, store, rest, output),
                "profil" => SetProfile(data, store, rest, output),
                "pc" => Pcs(data, rest, output),
                "skupiny" => ListGroups(data, rest, output),
                "skupina" => SetGroup(data, rest, output),
                "hodina" => SetLesson(data, rest, output),
                "zmena" => SetOverride(data, rest, today, output),
                "volno" => DaysOff(data, rest, today, output),
                "rozvrh" => Week(data, rest, output),
                "den" => Day(data, rest, today, output),
                _ => throw new UsageException($"Neznámy príkaz „{command}“. Zoznam príkazov: polaris-centrum pomoc"),
            };

            if (changed)
            {
                store.Save(data, utcNow);
                output.WriteLine($"Uložené do {file}.");
            }

            return 0;
        }
        catch (UsageException e)
        {
            output.WriteLine("Chyba: " + e.Message);
            return 2;
        }
        catch (InvalidDataException e)
        {
            output.WriteLine("Chyba: " + e.Message);
            return 2;
        }
        catch (IOException e)
        {
            output.WriteLine("Chyba pri práci so súborom: " + e.Message);
            return 2;
        }
        catch (UnauthorizedAccessException e)
        {
            output.WriteLine("Chyba – prístup k súboru odmietnutý: " + e.Message);
            return 2;
        }
    }

    private static bool Bells(Classroom data, List<string> a, TextWriter o)
    {
        var periods = data.Timetable.Periods;
        if (a.Count == 0)
        {
            if (periods.Count == 0)
            {
                o.WriteLine("Zvonenie je prázdne. Pridaj hodinu: zvonenie 1 8:00 8:45");
            }

            foreach (var p in periods.OrderBy(p => p.Start))
            {
                o.WriteLine($"{p.Number,2}. hodina  {Time(p.Start),5} – {Time(p.End),5}");
            }

            return false;
        }

        if (a[0] == "zrus")
        {
            Need(a, 2, "zvonenie zrus ČÍSLO");
            var number = Number(a[1], "číslo hodiny");
            if (periods.RemoveAll(p => p.Number == number) == 0)
            {
                throw new UsageException($"{number}. hodina v zvonení nie je.");
            }

            o.WriteLine($"{number}. hodina zrušená zo zvonenia (zápisy v rozvrhu ostali – kontrola ich ukáže).");
            return true;
        }

        Need(a, 3, "zvonenie ČÍSLO OD DO");
        var n = Number(a[0], "číslo hodiny");
        var start = ParseTime(a[1]);
        var end = ParseTime(a[2]);
        if (end <= start)
        {
            throw new UsageException("Hodina musí skončiť neskôr, než začne.");
        }

        periods.RemoveAll(p => p.Number == n);
        periods.Add(new Period(n, start, end));
        periods.Sort((x, y) => x.Start.CompareTo(y.Start));
        o.WriteLine($"{n}. hodina: {Time(start)} – {Time(end)}.");
        return true;
    }

    private static bool ListProfiles(Classroom data, ClassroomStore store, List<string> a, TextWriter o)
    {
        Need(a, 0, "profily");
        if (data.Profiles.Count == 0)
        {
            o.WriteLine("Žiadne profily. Pridaj: profil KANCELARSKY recepty/kancelarsky.json kancelárske programy");
        }

        foreach (var p in data.Profiles.OrderBy(p => p.Name, StringComparer.Ordinal))
        {
            var description = p.Description.Length > 0 ? " – " + p.Description : "";
            o.WriteLine($"{p.Name}: {p.File} ({RecipeState(store, p).Line}){description}");
        }

        return false;
    }

    /// <summary>What a profile's recipe is like now: version, fingerprint, signature; problems for the check.</summary>
    private static (string Line, List<string> Problems) RecipeState(ClassroomStore store, ProfileRecipe p)
    {
        var file = store.RecipeFile(p);
        if (!File.Exists(file))
        {
            return ("recept chýba", [$"Profil {p.Name}: recept {p.File} neexistuje."]);
        }

        Recipe recipe;
        try
        {
            recipe = RecipeText.Load(file);
        }
        catch (InvalidDataException e)
        {
            return ("recept sa nedá prečítať", [$"Profil {p.Name}: {e.Message}"]);
        }

        var problems = RecipeCheck.Problems(recipe).Select(x => $"Profil {p.Name}: {x}").ToList();
        var fingerprint = RecipeText.Fingerprint(recipe);
        var signatureFile = RecipeSigning.SignatureFileOf(file);
        string signed;
        if (!File.Exists(signatureFile))
        {
            signed = "nepodpísaný";
        }
        else
        {
            var signature = RecipeSigning.SignatureFromJson(File.ReadAllText(signatureFile));
            if (string.Equals(signature.Fingerprint, fingerprint, StringComparison.OrdinalIgnoreCase))
            {
                signed = "podpísal " + signature.Console;
            }
            else
            {
                signed = "PODPIS NESEDÍ";
                problems.Add($"Profil {p.Name}: recept sa zmenil po podpísaní – podpíš ho znova (podpisat {p.File} KLUC).");
            }
        }

        var name = recipe.Name != p.Name ? $", v recepte {recipe.Name}" : "";
        return ($"v{recipe.Version}{name}, odtlačok {fingerprint[..12]}…, {signed}{(problems.Count > 0 ? $", problémy: {problems.Count}" : "")}", problems);
    }

    private static bool SetProfile(Classroom data, ClassroomStore store, List<string> a, TextWriter o)
    {
        if (a.Count < 2)
        {
            throw new UsageException("Použitie: profil MENO RECEPT.json [POPIS…]");
        }

        var name = ProfileName(a[0]);
        var recipe = RecipeText.Load(a[1]);
        if (RecipeCheck.Problems(recipe) is { Count: > 0 } problems)
        {
            throw new UsageException($"Recept má problémy ({problems.Count}), profil z neho nebude. Prvý: {problems[0]}");
        }

        var profile = data.FindProfile(name);
        var created = profile is null;
        if (profile is null)
        {
            profile = new ProfileRecipe { Name = name };
            data.Profiles.Add(profile);
        }

        profile.File = store.Relative(a[1]);
        if (a.Count > 2)
        {
            profile.Description = string.Join(' ', a.Skip(2));
        }

        o.WriteLine($"Profil {name} {(created ? "pridaný" : "upravený")}: recept {recipe.Name} v{recipe.Version} ({profile.File}).");
        return true;
    }

    private static bool Pcs(Classroom data, List<string> a, TextWriter o)
    {
        if (a.Count == 0)
        {
            if (data.Pcs.Count == 0)
            {
                o.WriteLine("Žiadne PC. Pridaj: pc PC01 ucebna");
            }

            foreach (var pc in data.Pcs.OrderBy(p => p.Group, StringComparer.Ordinal).ThenBy(p => p.Name, StringComparer.Ordinal))
            {
                o.WriteLine($"{pc.Name} – skupina {pc.Group}");
            }

            return false;
        }

        Need(a, 2, "pc MENO SKUPINA | pc MENO -");
        var pcName = a[0];
        if (pcName.Length is 0 or > 40 || pcName.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
        {
            throw new UsageException($"Neplatné meno PC „{pcName}“.");
        }

        var existing = data.Pcs.FirstOrDefault(p => string.Equals(p.Name, pcName, StringComparison.OrdinalIgnoreCase));
        if (a[1] == "-")
        {
            if (existing is null)
            {
                throw new UsageException($"PC {pcName} v zozname nie je.");
            }

            data.Pcs.Remove(existing);
            o.WriteLine($"PC {existing.Name} vyradené.");
            return true;
        }

        var group = KnownGroup(data, a[1]);
        if (existing is null)
        {
            data.Pcs.Add(new ClassroomPc { Id = Guid.NewGuid(), Name = pcName, Group = group });
            o.WriteLine($"PC {pcName} pridané do skupiny {group}.");
        }
        else
        {
            existing.Group = group;
            o.WriteLine($"PC {existing.Name} je teraz v skupine {group}.");
        }

        return true;
    }

    private static bool ListGroups(Classroom data, List<string> a, TextWriter o)
    {
        Need(a, 0, "skupiny");
        if (data.Groups.Count == 0)
        {
            o.WriteLine("Žiadne skupiny. Pridaj: skupina ucebna ZAKLADNY");
        }

        foreach (var g in data.Groups.OrderBy(g => g.Name, StringComparer.Ordinal))
        {
            o.WriteLine($"{g.Name} – predvolený profil {g.DefaultProfile}, PC: {data.InGroup(g.Name).Count()}");
        }

        return false;
    }

    private static bool SetGroup(Classroom data, List<string> a, TextWriter o)
    {
        Need(a, 2, "skupina MENO PROFIL");
        var profile = KnownProfile(data, a[1]);
        var group = data.Groups.FirstOrDefault(g => g.Name == a[0]);
        if (group is null)
        {
            data.Groups.Add(new ClassroomGroup { Name = a[0], DefaultProfile = profile });
            o.WriteLine($"Skupina {a[0]} pridaná, predvolený profil {profile}.");
        }
        else
        {
            group.DefaultProfile = profile;
            o.WriteLine($"Skupina {a[0]}: predvolený profil {profile}.");
        }

        return true;
    }

    private static bool SetLesson(Classroom data, List<string> a, TextWriter o)
    {
        Need(a, 4, "hodina DEŇ ČÍSLO SKUPINA PROFIL");
        var day = Weekday(a[0]);
        var period = KnownPeriod(data, a[1]);
        var group = KnownGroup(data, a[2]);
        var lessons = data.Timetable.Lessons;
        var where = $"{Timetable.WeekdayName(day)}, {period}. hodina, skupina {group}";
        lessons.RemoveAll(l => l.Weekday == day && l.Period == period && l.Group == group);
        if (a[3] == "-")
        {
            o.WriteLine($"{where}: vymazané (beží predvolený profil).");
            return true;
        }

        var profile = KnownProfile(data, a[3]);
        lessons.Add(new Lesson(day, period, group, profile));
        lessons.Sort((x, y) => (x.Weekday, x.Period).CompareTo((y.Weekday, y.Period)));
        o.WriteLine($"{where}: {profile}.");
        return true;
    }

    private static bool SetOverride(Classroom data, List<string> a, DateOnly today, TextWriter o)
    {
        Need(a, 4, "zmena DÁTUM ČÍSLO SKUPINA PROFIL");
        var date = ParseDate(a[0], today);
        var period = KnownPeriod(data, a[1]);
        var group = KnownGroup(data, a[2]);
        var list = data.Timetable.Overrides;
        list.RemoveAll(x => x.Date == date && x.Period == period && x.Group == group);
        var where = $"{Date(date)}, {period}. hodina, skupina {group}";
        if (a[3] == "-")
        {
            o.WriteLine($"{where}: jednorazová zmena zrušená.");
            return true;
        }

        var profile = KnownProfile(data, a[3]);
        list.Add(new Override(date, period, group, profile));
        list.Sort((x, y) => (x.Date, x.Period).CompareTo((y.Date, y.Period)));
        if (date < today)
        {
            o.WriteLine("Pozor: dátum je v minulosti.");
        }

        o.WriteLine($"{where}: jednorazovo {profile}.");
        return true;
    }

    private static bool DaysOff(Classroom data, List<string> a, DateOnly today, TextWriter o)
    {
        var remove = a.Count > 0 && a[0] == "zrus";
        if (remove)
        {
            a = a.Skip(1).ToList();
        }

        if (a.Count == 0)
        {
            var list = data.Timetable.DaysOff.Where(d => d >= today).Order().ToList();
            o.WriteLine(list.Count == 0 ? "Žiadne voľné dni od dnes." : "Voľné dni od dnes: " + string.Join(", ", list.Select(Date)));
            return false;
        }

        if (a.Count > 2)
        {
            throw new UsageException("Použitie: volno [zrus] OD [DO]");
        }

        var from = ParseDate(a[0], today);
        var to = a.Count == 2 ? ParseDate(a[1], today) : from;
        if (to < from)
        {
            throw new UsageException("Koniec voľna je pred začiatkom.");
        }

        if (to.DayNumber - from.DayNumber > 366)
        {
            throw new UsageException("Voľno dlhšie ako rok – asi preklep v dátume.");
        }

        var count = 0;
        for (var d = from; d <= to; d = d.AddDays(1))
        {
            count += (remove ? data.Timetable.DaysOff.Remove(d) : data.Timetable.DaysOff.Add(d)) ? 1 : 0;
        }

        var range = from == to ? Date(from) : $"{Date(from)} – {Date(to)}";
        o.WriteLine(remove ? $"Voľno zrušené: {range} ({count} dní)." : $"Voľno: {range} ({count} nových dní).");
        return count > 0;
    }

    private static bool Week(Classroom data, List<string> a, TextWriter o)
    {
        if (a.Count > 1)
        {
            throw new UsageException("Použitie: rozvrh [SKUPINA]");
        }

        var tt = data.Timetable;
        if (tt.Periods.Count == 0)
        {
            o.WriteLine("Zvonenie je prázdne – najprv: zvonenie 1 8:00 8:45");
            return false;
        }

        foreach (var group in Groups(data, a))
        {
            var fallback = data.Groups.First(g => g.Name == group).DefaultProfile;
            var days = Enumerable.Range(1, 5)
                .Concat(tt.Lessons.Where(l => l.Group == group && l.Weekday > 5).Select(l => l.Weekday).Distinct().Order())
                .ToList();
            var cells = tt.Periods.OrderBy(p => p.Start).Select(p => days
                .Select(d => tt.Lessons.FirstOrDefault(l => l.Weekday == d && l.Period == p.Number && l.Group == group)?.Profile ?? "·")
                .ToList()).ToList();
            var width = Math.Max(9, cells.SelectMany(r => r).Append(fallback).Max(c => c.Length) + 2);
            o.WriteLine($"Rozvrh skupiny {group} (· = predvolený profil {fallback})");
            var head = new StringBuilder(new string(' ', 17));
            foreach (var d in days)
            {
                head.Append(Timetable.WeekdayName(d).PadRight(width));
            }

            o.WriteLine(head.ToString().TrimEnd());
            var row = 0;
            foreach (var p in tt.Periods.OrderBy(p => p.Start))
            {
                var line = new StringBuilder($"{p.Number,2}. {Time(p.Start),5}–{Time(p.End),5}  ");
                foreach (var c in cells[row])
                {
                    line.Append(c.PadRight(width));
                }

                o.WriteLine(line.ToString().TrimEnd());
                row++;
            }

            o.WriteLine();
        }

        return false;
    }

    private static bool Day(Classroom data, List<string> a, DateOnly today, TextWriter o)
    {
        if (a.Count > 2)
        {
            throw new UsageException("Použitie: den [DÁTUM] [SKUPINA]");
        }

        var date = today;
        var groupArgs = a;
        if (a.Count > 0 && TryParseDate(a[0], today, out var d))
        {
            date = d;
            groupArgs = a.Skip(1).ToList();
        }

        var tt = data.Timetable;
        foreach (var group in Groups(data, groupArgs))
        {
            var fallback = data.Groups.First(g => g.Name == group).DefaultProfile;
            o.WriteLine($"{Timetable.WeekdayName(Timetable.IsoWeekday(date))} {Date(date)} – skupina {group} (predvolený {fallback})");
            if (tt.DaysOff.Contains(date))
            {
                o.WriteLine("  voľný deň – celý deň " + fallback);
                continue;
            }

            var switches = tt.SwitchesOf(date, group, fallback);
            if (switches.Count == 0)
            {
                o.WriteLine("  bez prepnutia – celý deň " + fallback);
                continue;
            }

            foreach (var s in switches)
            {
                o.WriteLine($"  {Time(TimeOnly.FromDateTime(s.At)),5}  → {s.Profile}  ({s.Reason})");
            }

            var last = switches[^1].Profile;
            if (last != fallback)
            {
                o.WriteLine($"  {last} ostáva do konca dňa; po reštarte na druhý deň začne {fallback}.");
            }
        }

        return false;
    }

    private static int Check(Classroom data, ClassroomStore store, List<string> a, TextWriter o)
    {
        Need(a, 0, "kontrola");
        var profiles = data.Profiles.Select(p => p.Name).ToHashSet();
        var groups = data.Groups.Select(g => g.Name).ToHashSet();
        var problems = data.Timetable.Problems(profiles, groups);
        foreach (var p in data.Profiles)
        {
            problems.AddRange(RecipeState(store, p).Problems);
        }

        foreach (var pc in data.Pcs.Where(p => !groups.Contains(p.Group)))
        {
            problems.Add($"PC {pc.Name}: neznáma skupina {pc.Group}.");
        }
        foreach (var g in data.Groups.Where(g => !profiles.Contains(g.DefaultProfile)))
        {
            problems.Add($"Skupina {g.Name}: neznámy predvolený profil {g.DefaultProfile}.");
        }

        foreach (var o2 in data.Timetable.Overrides.Where(x => !groups.Contains(x.Group)))
        {
            problems.Add($"Jednorazová zmena {Date(o2.Date)}: neznáma skupina {o2.Group}.");
        }

        if (data.Timetable.Periods.Count == 0)
        {
            problems.Add("Zvonenie je prázdne.");
        }

        if (groups.Count == 0)
        {
            problems.Add("Nie je žiadna skupina PC.");
        }

        if (problems.Count == 0)
        {
            o.WriteLine($"V poriadku. Hodiny v zvonení: {data.Timetable.Periods.Count}, skupiny: {groups.Count}, PC: {data.Pcs.Count}, profily: {profiles.Count}, " +
                $"zápisy v rozvrhu: {data.Timetable.Lessons.Count}.");
            return 0;
        }

        o.WriteLine($"Nájdené problémy ({problems.Count}):");
        foreach (var p in problems)
        {
            o.WriteLine("  – " + p);
        }

        return 1;
    }

    // ---- keys and signatures ----

    private static int Keys(List<string> w, TextWriter o, DateTimeOffset utcNow, Func<string, string?> secret)
    {
        if (w[0] == "kluc")
        {
            var name = w.Count == 3 ? Path.GetFileName(w[2]) : "";
            if (w.Count != 3 || w[1] != "novy" || !KeyNamePattern().IsMatch(name))
            {
                throw new UsageException("Použitie: kluc novy [PRIEČINOK/]MENO (meno: písmená bez diakritiky, číslice, - a _)");
            }

            var folder = Path.GetDirectoryName(w[2]) is { Length: > 0 } d ? d : ".";
            var privateFile = Path.Combine(folder, name + ".kluc");
            var publicFile = Path.Combine(folder, name + ".verejny.json");
            if (File.Exists(privateFile) || File.Exists(publicFile))
            {
                throw new UsageException($"Súbor {privateFile} alebo {publicFile} už existuje – kľúč neprepíšem.");
            }

            var password = secret("Heslo ku kľúču (aspoň 12 znakov): ") ?? "";
            if (password.Length < MinPassword)
            {
                throw new UsageException($"Heslo musí mať aspoň {MinPassword} znakov.");
            }

            if (secret("Heslo ešte raz: ") != password)
            {
                throw new UsageException("Heslá sa nezhodujú.");
            }

            var (secretKey, publicKey) = RecipeSigning.NewConsoleKey(name, password);
            File.WriteAllText(privateFile, RecipeSigning.ToJson(secretKey));
            File.WriteAllText(publicFile, RecipeSigning.ToJson(publicKey));
            o.WriteLine($"Kľúč konzoly {name} (odtlačok {publicKey.Fingerprint}):");
            o.WriteLine($"  {privateFile} – SÚKROMNÝ, chránený heslom. Drž ho len u seba (napr. USB v trezore), nikdy nie na PC žiakov ani na GitHube.");
            o.WriteLine($"  {publicFile} – verejný. Na PC: polaris-agent doverovat {publicFile}");
            return 0;
        }

        if (w.Count != 3)
        {
            throw new UsageException("Použitie: podpisat RECEPT KLUC");
        }

        var recipe = RecipeText.Load(w[1]);
        var problems = RecipeCheck.Problems(recipe);
        if (problems.Count > 0)
        {
            throw new UsageException($"Recept má problémy ({problems.Count}), nepodpíšem ho. Prvý: {problems[0]}");
        }

        var keyFile = RecipeSigning.KeyFromJson(File.ReadAllText(w[2]));
        using var key = RecipeSigning.OpenPrivate(keyFile, secret($"Heslo ku kľúču {keyFile.Name}: ") ?? "");
        var signature = RecipeSigning.Sign(recipe, key, keyFile.Name, utcNow);
        var target = RecipeSigning.SignatureFileOf(w[1]);
        File.WriteAllText(target, RecipeSigning.ToJson(signature));
        o.WriteLine($"Recept {recipe.Name} v{recipe.Version} podpísaný konzolou {keyFile.Name}: {target}");
        o.WriteLine($"Odtlačok receptu: {signature.Fingerprint}");
        return 0;
    }

    [System.Text.RegularExpressions.GeneratedRegex("^[A-Za-z0-9_-]{1,40}$")]
    private static partial System.Text.RegularExpressions.Regex KeyNamePattern();

    // ---- helpers ----

    private static void Need(List<string> a, int count, string usage)
    {
        if (a.Count != count)
        {
            throw new UsageException("Použitie: " + usage);
        }
    }

    private static int Number(string text, string what) =>
        int.TryParse(text, NumberStyles.None, Inv, out var n) && n is >= 0 and <= 20
            ? n
            : throw new UsageException($"Neplatné {what}: „{text}“.");

    private static string ProfileName(string text)
    {
        var name = text.ToUpperInvariant();
        if (name.Length == 0 || name.Length > 40 || name == "-" || name.Any(char.IsWhiteSpace))
        {
            throw new UsageException($"Neplatné meno profilu „{text}“.");
        }

        return name;
    }

    private static string KnownProfile(Classroom data, string text)
    {
        var name = ProfileName(text);
        return data.FindProfile(name) is not null
            ? name
            : throw new UsageException($"Profil {name} neexistuje. Pridaj ho: profil {name}");
    }

    private static string KnownGroup(Classroom data, string text) =>
        data.Groups.Any(g => g.Name == text)
            ? text
            : throw new UsageException($"Skupina {text} neexistuje. Pridaj ju: skupina {text} PROFIL");

    private static int KnownPeriod(Classroom data, string text)
    {
        var n = Number(text, "číslo hodiny");
        return data.Timetable.Periods.Any(p => p.Number == n)
            ? n
            : throw new UsageException($"{n}. hodina nie je v zvonení. Pridaj ju: zvonenie {n} OD DO");
    }

    private static IEnumerable<string> Groups(Classroom data, List<string> a)
    {
        if (a.Count == 1)
        {
            return [KnownGroup(data, a[0])];
        }

        if (a.Count > 1)
        {
            throw new UsageException("Uveď najviac jednu skupinu.");
        }

        return data.Groups.Count > 0
            ? data.Groups.Select(g => g.Name).Order(StringComparer.Ordinal)
            : throw new UsageException("Nie je žiadna skupina PC. Pridaj ju: skupina ucebna PROFIL");
    }

    public static int Weekday(string text) => text.ToLowerInvariant() switch
    {
        "1" or "po" or "pondelok" => 1,
        "2" or "ut" or "utorok" => 2,
        "3" or "st" or "streda" => 3,
        "4" or "št" or "stv" or "štvrtok" or "stvrtok" => 4,
        "5" or "pi" or "piatok" => 5,
        "6" or "so" or "sobota" => 6,
        "7" or "ne" or "nedeľa" or "nedela" => 7,
        _ => throw new UsageException($"Neznámy deň „{text}“ (po ut st št pi so ne alebo 1–7)."),
    };

    public static TimeOnly ParseTime(string text) =>
        TimeOnly.TryParseExact(text, ["H:mm", "H.mm"], Inv, DateTimeStyles.None, out var t)
            ? t
            : throw new UsageException($"Neplatný čas „{text}“ (napr. 8:00).");

    public static DateOnly ParseDate(string text, DateOnly today) =>
        TryParseDate(text, today, out var d) ? d : throw new UsageException($"Neplatný dátum „{text}“ (napr. 16.11.2026).");

    public static bool TryParseDate(string text, DateOnly today, out DateOnly date)
    {
        switch (text)
        {
            case "dnes":
                date = today;
                return true;
            case "zajtra":
                date = today.AddDays(1);
                return true;
        }

        if (DateOnly.TryParseExact(text, ["d.M.yyyy", "yyyy-MM-dd"], Inv, DateTimeStyles.None, out date))
        {
            return true;
        }

        var parts = text.Split('.');
        if (parts.Length == 3 && parts[2].Length == 0
            && int.TryParse(parts[0], NumberStyles.None, Inv, out var day)
            && int.TryParse(parts[1], NumberStyles.None, Inv, out var month)
            && month is >= 1 and <= 12 && day >= 1 && day <= DateTime.DaysInMonth(today.Year, month))
        {
            date = new DateOnly(today.Year, month, day);
            return true;
        }

        date = default;
        return false;
    }

    private static string Time(TimeOnly t) => t.ToString("H:mm", Inv);

    private static string Date(DateOnly d) => d.ToString("d. M. yyyy", Inv);
}
