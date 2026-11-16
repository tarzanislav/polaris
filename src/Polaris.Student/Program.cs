using System.Text;
using Polaris.Recipes;
using Polaris.Student;

// polaris-ziak                   the icon (started by the agent's service in every session; by hand only for a test)
// polaris-ziak --skuska SÚBOR    draws the icon, asks the store, writes what the menu would show – for CI, no window
if (args is ["--skuska", var report])
{
    var text = new StringBuilder();
    if (OperatingSystem.IsWindows())
    {
        foreach (var size in new[] { 16, 20, 24, 32, 48, 64 })
        {
            Polaris.Student.Probe.Icon(size);
        }

        text.AppendLine("ikona: 16, 20, 24, 32, 48, 64 px");
    }

    var store = StoreClient.Ask(new StoreRequest { Command = "list" }, StoreClient.ListLimit, connectMilliseconds: 5000);
    void Write(IEnumerable<MenuEntry> entries, string indent)
    {
        foreach (var e in entries)
        {
            text.AppendLine(indent + (e.IsSeparator ? "———" : e.Text) + (e.Checked ? " ✓" : "") + (e.Enabled || e.IsSeparator ? "" : " (sivé)"));
            if (e.Children is { } children)
            {
                Write(children, indent + "  ");
            }
        }
    }

    Write(TrayMenu.Build(store, [], [], SessionCleaning.AtSignOut), "");
    File.WriteAllText(report, text.ToString(), new UTF8Encoding(false));
    return 0;
}

if (!OperatingSystem.IsWindows())
{
    Console.Error.WriteLine("Ikona POLARIS beží len vo Windows.");
    return 2;
}

// started by hand a second time: one icon per session is enough (the service's own start skips this)
using var single = new Mutex(true, @"Local\POLARIS-ikona", out var first);
if (!first && !args.Contains("--zo-sluzby"))
{
    return 0;
}

return Tray.Run();
