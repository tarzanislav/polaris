using Polaris.Net;

namespace Polaris.Centrum;

/// <summary>The console on a double click: the key of the console is made the first time (only its password is asked),
/// the address and pin of the centre come from centrum.txt beside the program (the file the centre wrote; asked once
/// when it is missing), then the console opens in the browser.</summary>
public static class ConsoleSetup
{
    public static int Run(TextWriter o, CancellationToken stop)
    {
        o.WriteLine("POLARIS konzola");
        o.WriteLine("===============");
        var folder = Home(o);
        Environment.CurrentDirectory = folder;
        var keys = Directory.GetFiles(folder, "*.kluc").Order(StringComparer.OrdinalIgnoreCase).ToList();
        if (keys.Count == 0)
        {
            o.WriteLine("Prvé spustenie: vytvorím kľúč tejto konzoly. Heslo si vymysli (aspoň 12 znakov, pri písaní sa nezobrazuje) a zapíš si ho.");
            o.Write("Meno konzoly (Enter = ucitel; napr. notebook1): ");
            var name = Console.ReadLine()?.Trim() is { Length: > 0 } typed ? typed : "ucitel";
            for (var attempt = 0; attempt < 3 && keys.Count == 0; attempt++)
            {
                Commands.Run(["kluc", "novy", name], o, DateTime.Now, DateTimeOffset.UtcNow);
                keys = Directory.GetFiles(folder, "*.kluc").ToList();
            }

            if (keys.Count == 0)
            {
                return Wait(o, 1);
            }

            o.WriteLine();
            o.WriteLine($"DÔLEŽITÉ: súbor {name}.verejny.json z tohto priečinka nakopíruj na server a tam spusti:");
            o.WriteLine($"  polaris-centrum-nastavit {name}.verejny.json");
            o.WriteLine();
        }

        var key = keys[0];
        if (keys.Count > 1)
        {
            for (var i = 0; i < keys.Count; i++)
            {
                o.WriteLine($"  {i + 1}. {Path.GetFileNameWithoutExtension(keys[i])}");
            }

            o.Write("Ktorý kľúč (číslo, Enter = 1): ");
            key = int.TryParse(Console.ReadLine(), out var n) && n >= 1 && n <= keys.Count ? keys[n - 1] : keys[0];
        }

        var file = Path.Combine(folder, CentreFile.Name);
        var centre = File.Exists(file) ? CentreFile.Parse(File.ReadAllText(file)) : null;
        if (centre is null)
        {
            o.WriteLine($"Chýba {CentreFile.Name} (zo servera /etc/polaris/centrum.txt – stačí ho skopírovať sem). Alebo zadaj údaje:");
            o.Write("Adresa centra (napr. 172.16.0.10): ");
            var address = Console.ReadLine()?.Trim() ?? "";
            o.Write("Odtlačok centra (64 znakov, vložíš pravým tlačidlom): ");
            centre = CentreFile.Parse(address + " " + Console.ReadLine());
            if (centre is null)
            {
                o.WriteLine("Adresa alebo odtlačok nie sú v správnom tvare.");
                return Wait(o, 1);
            }

            File.WriteAllText(file, CentreFile.Line(centre.Value.Address, centre.Value.Pin));
        }

        o.WriteLine("Konzola sa otvorí v prehliadači. Toto okno nechaj otvorené – jeho zatvorením konzola skončí.");
        var result = Commands.Run(["konzola", key, "--server", centre.Value.Address.ToString(), centre.Value.Pin], o, DateTime.Now, DateTimeOffset.UtcNow, stop: stop);
        if (result != 0)
        {
            o.WriteLine($"Ak je zlá adresa alebo odtlačok, zmaž {CentreFile.Name} a spusti znova.");
        }

        return Wait(o, result);
    }

    /// <summary>Where the console keeps its key and centrum.txt: beside the program when a key already lies there (as
    /// before), otherwise C:\POLARIS\konzola – the program copies itself there (a newer one replaces it), takes
    /// centrum.txt along and puts a shortcut on the desktop. The downloads folder never holds the key.</summary>
    private static string Home(TextWriter o)
    {
        var here = Path.GetFullPath(AppContext.BaseDirectory);
        if (Directory.GetFiles(here, "*.kluc").Length > 0 || !OperatingSystem.IsWindows())
        {
            return here;
        }

        var home = Path.Combine(Path.GetPathRoot(Environment.SystemDirectory)!, "POLARIS", "konzola");
        if (string.Equals(here.TrimEnd('\\'), home, StringComparison.OrdinalIgnoreCase))
        {
            return home;
        }

        Directory.CreateDirectory(home);
        var program = Path.Combine(home, ProgramName);
        try
        {
            File.Copy(Environment.ProcessPath!, program, overwrite: true);
        }
        catch (IOException)
        {
            // the console from that folder is running right now – it stays as it is
        }

        var centre = Path.Combine(here, CentreFile.Name);
        if (File.Exists(centre) && CentreFile.Parse(File.ReadAllText(centre)) is not null)
        {
            File.Copy(centre, Path.Combine(home, CentreFile.Name), overwrite: true);
        }

        try
        {
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            Polaris.Windows.Shortcuts.Create(Path.Combine(desktop, "POLARIS konzola.lnk"), program, home);
            o.WriteLine($"Konzola je v {home}; na ploche je odkaz „POLARIS konzola“ (nabudúce stačí ten).");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or Polaris.Windows.SystemCallException)
        {
            o.WriteLine($"Konzola je v {home} (odkaz na plochu sa nepodarilo vytvoriť: {e.Message}).");
        }

        return home;
    }

    public const string ProgramName = "POLARIS-konzola.exe";

    private static int Wait(TextWriter o, int code)
    {
        if (!Console.IsInputRedirected)
        {
            o.WriteLine();
            o.Write("Stlač Enter na zatvorenie okna…");
            Console.ReadLine();
        }

        return code;
    }
}
