using System.Diagnostics;
using System.Runtime.Versioning;
using Polaris.Net;

namespace Polaris.Agent;

/// <summary>The installer of a PC on a double click: asks Windows for the administrator once, installs or updates the
/// service and registers the PC in the centre. The address and the pin of the centre come from centrum.txt lying beside
/// the program (the file the centre wrote) – the administrator types only the code from the console.</summary>
[SupportedOSPlatform("windows")]
public static class Setup
{
    public static int Run(TextWriter o, AgentHost host)
    {
        if (!host.IsAdmin())
        {
            // once more as administrator (Windows asks); this window ends
            try
            {
                Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "instalovat") { UseShellExecute = true, Verb = "runas", WorkingDirectory = AppContext.BaseDirectory })?.Dispose();
                return 0;
            }
            catch (System.ComponentModel.Win32Exception)
            {
                o.WriteLine("Inštalácia potrebuje práva správcu – Windows ich nepovolil. Spusti znova a potvrď „Áno“.");
                return Wait(o, 1);
            }
        }

        o.WriteLine("POLARIS – inštalácia PC");
        o.WriteLine("=======================");
        o.WriteLine();
        o.WriteLine("1/2 Služba POLARIS agent");
        Commands.Run(["instalovat-sluzbu"], o, host);
        o.WriteLine();
        o.WriteLine("2/2 Registrácia v centre");
        if (PcLink.Id() is { } id && Centre.Settings() is { } known)
        {
            o.WriteLine($"PC už je zaregistrované (identifikátor {id[..8]}, centrum {known.Address}).");
            var (sent, message) = PcLink.Report(host);
            o.WriteLine(sent ? "Hlásenie centrum prijalo – PC vidno v konzole." : "Hlásenie sa nepodarilo: " + message);
            return Wait(o, sent ? 0 : 1);
        }

        var file = Path.Combine(AppContext.BaseDirectory, CentreFile.Name);
        var centre = File.Exists(file) ? CentreFile.Parse(File.ReadAllText(file)) : null;
        if (centre is null)
        {
            o.WriteLine($"Vedľa programu nie je {CentreFile.Name} (zo servera: /etc/polaris/centrum.txt). Zadaj údaje centra:");
            o.Write("Adresa centra (napr. 172.16.0.10): ");
            var address = Console.ReadLine()?.Trim() ?? "";
            o.Write("Odtlačok centra (64 znakov): ");
            centre = CentreFile.Parse(address + " " + Console.ReadLine());
            if (centre is null)
            {
                o.WriteLine("Adresa alebo odtlačok nie sú v správnom tvare.");
                return Wait(o, 1);
            }

            TrySave(file, CentreFile.Line(centre.Value.Address, centre.Value.Pin));
        }
        else
        {
            o.WriteLine($"Centrum: {centre.Value.Address} (z {CentreFile.Name})");
        }

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            o.Write("Kód z konzoly (tlačidlo + Pridať PC): ");
            var code = Console.ReadLine()?.Trim() ?? "";
            if (code.Length == 0)
            {
                continue;
            }

            var result = Commands.Run(["registrovat", centre.Value.Address.ToString(), centre.Value.Pin, code], o, host);
            if (result == 0)
            {
                o.WriteLine();
                o.WriteLine("HOTOVO. PC je v konzole; nastavenia z centra si stiahne do 5 minút.");
                return Wait(o, 0);
            }

            o.WriteLine("Skús kód znova (alebo vydaj v konzole nový).");
        }

        return Wait(o, 1);
    }

    private static void TrySave(string file, string text)
    {
        try
        {
            File.WriteAllText(file, text);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // only a convenience for the next PC
        }
    }

    /// <summary>The window of a double click closes at the end – the administrator reads first.</summary>
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
