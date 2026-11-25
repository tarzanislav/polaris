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

        // without a code: the PC applies and a console from centrum.txt approves it (decided 25. 11. 2026)
        var consoles = File.Exists(file) ? CentreFile.Consoles(File.ReadAllText(file)).Select(c => c.Fingerprint).ToList() : [];
        if (consoles.Count > 0 && Approved(o, host, centre.Value.Address, centre.Value.Pin, consoles))
        {
            return Wait(o, 0);
        }

        if (PcLink.Waiting() is not null)
        {
            return Wait(o, 0); // still waiting: the service finishes it after the approval
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

    /// <summary>Applies and waits (up to half an hour) for a console to approve; true when registered.</summary>
    private static bool Approved(TextWriter o, AgentHost host, Uri address, string pin, List<string> consoles)
    {
        var (registered, waiting, message, check) = PcLink.Apply(address, pin, consoles, Environment.MachineName, host.Clock());
        if (!registered && !waiting)
        {
            o.WriteLine("Prihláška sa nepodarila: " + message);
            o.WriteLine("Použi kód z konzoly (+ Pridať PC):");
            return false;
        }

        if (!registered)
        {
            o.WriteLine();
            o.WriteLine($"   Kontrolný kód tohto PC:  {check}");
            o.WriteLine();
            o.WriteLine("V konzole: Čakajú na schválenie → skontroluj kód → Prijať.");
            o.WriteLine("Okno čaká na schválenie. Môžeš ho aj zavrieť – PC sa zaregistruje samo, keď ho v konzole prijmeš.");
            o.Write("Čakám");
            var until = DateTime.UtcNow.AddMinutes(30);
            while (!registered && DateTime.UtcNow < until)
            {
                Thread.Sleep(TimeSpan.FromSeconds(5));
                o.Write(".");
                (registered, waiting, message, _) = PcLink.Apply(address, pin, consoles, Environment.MachineName, host.Clock());
                if (!registered && !waiting)
                {
                    o.WriteLine();
                    o.WriteLine("Prihláška sa nepodarila: " + message);
                    return false;
                }
            }

            o.WriteLine();
            if (!registered)
            {
                o.WriteLine("Schválenie zatiaľ neprišlo – PC sa zaregistruje samo, keď ho v konzole prijmeš.");
                return false;
            }
        }

        o.WriteLine(message);
        var (sent, said) = PcLink.Report(host);
        o.WriteLine(sent ? "Prvé hlásenie centrum prijalo." : "Prvé hlásenie sa nepodarilo: " + said);
        o.WriteLine();
        o.WriteLine("HOTOVO. PC je v konzole; nastavenia z centra si stiahne do 5 minút.");
        return true;
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
