using Polaris.Core;
using Polaris.Windows;

namespace Polaris.Lab;

/// <summary>The menu of the owner. It recognizes where it runs – the original Windows without POLARIS, the original
/// Windows with POLARIS installed, or a layer – and offers only what makes sense there.</summary>
internal static class Menu
{
    public static int Run()
    {
        Ui.Line($"{Product.Name} – laboratórium {Product.Stage}, program {Product.Version}", ConsoleColor.White);
        Ui.Line("Otvorený softvér na zmrazenie systému. Ovládaš ho číslami, nič sa nepíše.", ConsoleColor.Gray);
        while (true)
        {
            var store = Store.Find();
            Slot? running = null;
            LabState? state = null;
            if (store is not null)
            {
                try
                {
                    state = store.Load();
                    running = Rotation.RunningSlot(state, SystemInfo.BootIdentifier());
                }
                catch (Exception e) when (e is IOException or FileNotFoundException or SystemCallException)
                {
                    Ui.Error("Stav POLARIS sa nedá prečítať: " + e.Message);
                }
            }

            var keep = running is { } slot && state is not null ? InLayer(store!, state, slot)
                : state is not null ? InOriginalInstalled(store!, state)
                : InOriginalClean();
            if (!keep)
            {
                return 0;
            }
        }
    }

    private static bool InOriginalClean()
    {
        Ui.Title("Pôvodný Windows – POLARIS nie je nainštalovaný");
        Ui.Option('1', "Kontrola počítača (nič nemení)");
        Ui.Option('2', "Prevod do vrstiev a skúšobný štart");
        Ui.Option('0', "Koniec");
        switch (Ui.Choose("120"))
        {
            case '1':
                ShowChecks(Conversion.Inspect());
                Ui.Pause();
                return true;
            case '2':
                var facts = Conversion.Inspect();
                ShowChecks(facts);
                if (!facts.CanStart)
                {
                    Ui.Error("Prevod sa nemôže začať – oprav položky označené X a skús znova.");
                    Ui.Pause();
                    return true;
                }

                Ui.Line("");
                Ui.Line("Čo sa stane: vytvorí sa snímka bežiaceho Windows, z nej základná vrstva a tri čisté vrstvy A, B, C");
                Ui.Line("v priečinku C:\\POLARIS. Pôvodný Windows ostane nedotknutý. Potom sa PC reštartuje do vrstvy A –");
                Ui.Line("ak by čokoľvek zlyhalo, ďalší reštart sa sám vráti do pôvodného Windows.");
                Ui.Line($"Prevod potrvá podľa disku asi 5 – 30 minút (použité dáta {Format.Gb(facts.UsedBytes)}). Ulož si otvorené súbory.");
                if (!Ui.Confirm("Začať prevod?"))
                {
                    return true;
                }

                if (Conversion.Run(facts))
                {
                    try
                    {
                        Ui.Countdown("Reštart do vrstvy A", 15);
                        SystemInfo.Restart("POLARIS: prvý štart z vrstvy A", force: false);
                        return false;
                    }
                    catch (OperationCanceledException)
                    {
                        Ui.Warn("Reštart zrušený. Vrstva A naštartuje pri najbližšom reštarte.");
                    }
                }

                Ui.Pause();
                return true;
            default:
                return false;
        }
    }

    private static bool InOriginalInstalled(Store store, LabState state)
    {
        Ui.Title("Pôvodný Windows – POLARIS je nainštalovaný (tento Windows nie je zmrazený)");
        Ui.Line("Stav: " + Report.PhaseText(state.Phase));
        var boots = store.Journal().Count(e => e.Kind == JournalKind.Boot);
        if (state.Phase == LabPhase.FirstBootPending && boots == 0)
        {
            Ui.Warn("Prvý štart z vrstvy A sa zatiaľ nepodaril alebo ešte neprebehol. Ak si už reštartoval, PC sa vrátilo");
            Ui.Warn("do pôvodného Windows – pošli správu (bod 1) do chatu.");
        }
        else if (state.Phase == LabPhase.Running)
        {
            Ui.Warn("PC naštartovalo pôvodný Windows (bezpečný režim po výpadku alebo chybe, alebo voľba v ponuke).");
        }

        Ui.Option('1', "Stav a správa (uloží sa na plochu)");
        Ui.Option('2', "Pokračovať vo vrstvách (všetky vrstvy čisté, reštart do vrstvy A)");
        Ui.Option('3', "Odinštalovať POLARIS (zmaže C:\\POLARIS, ponuka štartu ako predtým)");
        Ui.Option('0', "Koniec");
        switch (Ui.Choose("1230"))
        {
            case '1':
                SaveReport(store, toDesktop: true);
                Ui.Pause();
                return true;
            case '2':
                if (state.Phase == LabPhase.Preparing)
                {
                    Ui.Error("Prevod nebol dokončený – odinštaluj (3) a sprav prevod znova.");
                    Ui.Pause();
                    return true;
                }

                Maintenance.Resume(store);
                return RestartNow("POLARIS: štart do vrstvy A");
            case '3':
                if (Ui.Confirm("Naozaj odinštalovať POLARIS a zmazať vrstvy?"))
                {
                    SaveReport(store, toDesktop: true);
                    Ui.Line(Maintenance.Uninstall(store, null) ? "POLARIS je odstránený." : "POLARIS je odstránený, niečo ostalo (pozri vyššie).");
                    Ui.Pause();
                }

                return true;
            default:
                return false;
        }
    }

    private static bool InLayer(Store store, LabState state, Slot running)
    {
        Ui.Title($"Vrstva {running} – ZMRAZENÉ: zmeny po reštarte zmiznú");
        Ui.Line("Režim: " + Report.ModeText(state.Mode));
        var gate = Report.Gate(store.Journal());
        Ui.Line(state.AutoTest.Active
            ? $"Skúška reštartov prebieha: štart {state.AutoTest.CurrentIndex} z {state.AutoTest.Total}."
            : $"Čisté štarty skúšky: {gate.CleanAutoTestBoots} z {gate.Required}.");
        Ui.Option('1', "Stav a správa (uloží sa do C:\\POLARIS\\Sklad\\Spravy pôvodného disku)");
        Ui.Option('2', "Skúška reštartov (zadáš počet, PC sa bude reštartovať samo)");
        Ui.Option('3', "Reštartovať teraz (do čistej vrstvy)");
        Ui.Option('4', state.Mode == LabMode.Safe ? "Prepnúť na Ostrý režim" : "Prepnúť na Bezpečný režim");
        Ui.Option('5', "Zastaviť prebiehajúcu skúšku");
        Ui.Option('6', "Odinštalovať (návrat do pôvodného Windows)");
        Ui.Option('0', "Koniec");
        switch (Ui.Choose("1234560"))
        {
            case '1':
                SaveReport(store, toDesktop: false);
                Ui.Pause();
                return true;
            case '2':
                var count = Ui.Number("Počet štartov", 1, 1000, Report.GateBoots);
                var wait = Ui.Number("Koľko sekúnd čakať po každom štarte", 30, 3600, 120);
                Ui.Line($"Skúška potrvá približne {Format.Duration(TimeSpan.FromSeconds(count * (wait + 60)))}. Prerušíš ju bodom 5.");
                if (Ui.Confirm("Spustiť skúšku a reštartovať?"))
                {
                    Maintenance.StartAutoTest(store, count, wait);
                    return RestartNow("POLARIS: skúška reštartov");
                }

                return true;
            case '3':
                Maintenance.RestartRequested(store);
                return RestartNow("POLARIS: reštart do čistej vrstvy");
            case '4':
                if (state.Mode == LabMode.Safe)
                {
                    Ui.Line("V Ostrom režime po výpadku prúdu alebo chybe naštartuje ďalšia čistá vrstva, nie pôvodný Windows.");
                    Ui.Line("Odporúčam ho až po aspoň 10 čistých štartoch v Bezpečnom režime.");
                    if (!Ui.Confirm("Prepnúť?"))
                    {
                        return true;
                    }
                }

                Maintenance.SwitchMode(store, running);
                Ui.Ok("Režim zmenený.");
                return true;
            case '5':
                Maintenance.StopAutoTest(store);
                Ui.Ok("Skúška zastavená.");
                return true;
            case '6':
                if (Ui.Confirm("Odinštalovať? PC naštartuje pôvodný Windows; tam spusti program znova a zvoľ Odinštalovať (zmaže súbory)."))
                {
                    Maintenance.Uninstall(store, running);
                    return RestartNow("POLARIS: návrat do pôvodného Windows");
                }

                return true;
            default:
                return false;
        }
    }

    private static void ShowChecks(PcFacts facts)
    {
        Ui.Title("Kontrola počítača");
        foreach (var r in facts.Results)
        {
            Ui.Check(r);
        }
    }

    private static void SaveReport(Store store, bool toDesktop)
    {
        var state = store.Load();
        var path = store.WriteReport(state);
        Ui.Line(File.ReadAllText(path));
        if (toDesktop)
        {
            var copy = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), Path.GetFileName(path));
            File.Copy(path, copy, overwrite: true);
            Ui.Ok("Správa uložená na plochu: " + copy);
        }
        else
        {
            Ui.Ok("Správa uložená: " + path);
        }
    }

    private static bool RestartNow(string reason)
    {
        try
        {
            Ui.Countdown("Reštart", 10);
            SystemInfo.Restart(reason, force: false);
            return false;
        }
        catch (OperationCanceledException)
        {
            Ui.Warn("Reštart zrušený – urob ho, keď budeš chcieť.");
            return true;
        }
    }
}
