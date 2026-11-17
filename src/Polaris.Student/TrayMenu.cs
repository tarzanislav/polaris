using Polaris.Recipes;

namespace Polaris.Student;

/// <summary>One line of the menu: a command (0 = only text), a submenu, or a separator.</summary>
public sealed record MenuEntry(int Command, string Text, bool Checked = false, bool Enabled = true, IReadOnlyList<MenuEntry>? Children = null)
{
    public static readonly MenuEntry Separator = new(0, "", Enabled: false);

    public bool IsSeparator => ReferenceEquals(this, Separator);
}

/// <summary>What the icon shows – no Windows here, so it is checked on any system.</summary>
public static class TrayMenu
{
    public const int Refresh = 10;
    public const int StoreFirst = 1000;
    public const int SandboxFirst = 2000;

    /// <param name="store">The last answer of the store (null = not known yet).</param>
    /// <param name="pending">Items being installed now.</param>
    /// <param name="sandboxes">Names of the Sandbox templates on the desktop.</param>
    /// <param name="cleaning">The rule of this account's session, null when it has none.</param>
    public static List<MenuEntry> Build(StoreAnswer? store, IReadOnlyCollection<string> pending, IReadOnlyList<string> sandboxes, SessionCleaning? cleaning)
    {
        var menu = new List<MenuEntry> { new(0, "Školský obchod", Children: StoreEntries(store, pending)) };
        if (sandboxes.Count > 0)
        {
            menu.Add(new MenuEntry(0, "Pieskovisko", Children: sandboxes.Select((name, i) => new MenuEntry(SandboxFirst + i, name)).ToList()));
        }

        if (Short(cleaning) is { } note)
        {
            menu.Add(MenuEntry.Separator);
            menu.Add(new MenuEntry(0, note, Enabled: false));
        }

        return menu;
    }

    private static List<MenuEntry> StoreEntries(StoreAnswer? store, IReadOnlyCollection<string> pending)
    {
        var list = new List<MenuEntry>();
        if (store is null)
        {
            list.Add(new MenuEntry(0, "Načítavam ponuku…", Enabled: false));
        }
        else if (!store.Ok || store.Items.Count == 0)
        {
            list.Add(new MenuEntry(0, store.Message.Length > 0 ? store.Message : "Ponuka je prázdna.", Enabled: false));
        }
        else
        {
            for (var i = 0; i < store.Items.Count; i++)
            {
                var o = store.Items[i];
                var busy = pending.Contains(o.Id);
                var text = o.Title + (o.Description.Length > 0 ? " – " + o.Description : "") + (busy ? "  (inštaluje sa…)" : o.Installed ? "  (spustiť)" : "");
                list.Add(new MenuEntry(StoreFirst + i, text, Checked: o.Installed, Enabled: !busy));
            }
        }

        list.Add(MenuEntry.Separator);
        list.Add(new MenuEntry(Refresh, "Obnoviť ponuku"));
        return list;
    }

    /// <summary>For the menu and the tooltip (one line).</summary>
    public static string? Short(SessionCleaning? cleaning) => cleaning switch
    {
        SessionCleaning.AtSignOut => "Po odhlásení sa tento profil vymaže",
        SessionCleaning.EndOfDay => "Po skončení dňa sa tento profil vymaže",
        SessionCleaning.EndOfWeek => "Po skončení týždňa sa tento profil vymaže",
        SessionCleaning.Restore => "Po odhlásení sa tento profil vráti do pôvodného stavu",
        _ => null,
    };

    /// <summary>The notice after signing in: what happens to the work.</summary>
    public static string? Notice(SessionCleaning? cleaning) => Short(cleaning) is { } s
        ? s + ". Prácu si ukladaj do Úschovne alebo na USB – na tomto PC neostane."
        : null;

    public static string Tooltip(SessionCleaning? cleaning) => "POLARIS – Školský obchod" + (Short(cleaning) is { } s ? "\n" + s : "");

    /// <summary>The templates on the desktop: "Pieskovisko – NAME.wsb" → NAME, in order of names.</summary>
    public static List<(string Name, string File)> Sandboxes(IEnumerable<string> files)
    {
        const string prefix = "Pieskovisko – ";
        const string extension = ".wsb";
        return files
            .Select(f => (Name: f[(f.LastIndexOfAny(['\\', '/']) + 1)..], File: f)) // a Windows path also when checked elsewhere
            .Where(x => x.Name.EndsWith(extension, StringComparison.OrdinalIgnoreCase) && x.Name.StartsWith(prefix, StringComparison.Ordinal))
            .Select(x => (x.Name[prefix.Length..^extension.Length], x.File))
            .OrderBy(x => x.Item1, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }
}
