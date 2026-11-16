using System.Globalization;
using System.Security;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Polaris.Recipes;

/// <summary>A folder of the PC shown inside the Sandbox.</summary>
public sealed class MappedFolder
{
    /// <summary>Folder on the PC (full path).</summary>
    public string HostFolder { get; set; } = "";

    /// <summary>Where it appears inside the Sandbox (empty = on its desktop).</summary>
    public string? SandboxFolder { get; set; }

    /// <summary>Inside the Sandbox only for reading (default).</summary>
    public bool ReadOnly { get; set; } = true;
}

/// <summary>A template of the Sandbox (Pieskovisko): a file on the common desktop that starts Windows Sandbox with these
/// settings. Defaults are the strict ones – no network, no clipboard, no microphone or camera, protected client.</summary>
public sealed class SandboxElement : Element
{
    /// <summary>Name shown to pupils (the file is "Pieskovisko – NAME.wsb").</summary>
    public string Name { get; set; } = "";

    public bool Networking { get; set; }

    public bool Gpu { get; set; }

    public int MemoryMb { get; set; } = 4096;

    public bool Clipboard { get; set; }

    public bool ProtectedClient { get; set; } = true;

    public bool Microphone { get; set; }

    public bool Camera { get; set; }

    public bool Printer { get; set; }

    public List<MappedFolder> Folders { get; set; } = [];

    /// <summary>A command run inside after the start (e.g. installing the tools of a lesson).</summary>
    public string? LogonCommand { get; set; }

    public override string Key => "sandbox:" + Name.Trim().ToLowerInvariant();

    public override string Title => $"šablóna Pieskoviska {Name}";

    public string FileName => $"Pieskovisko – {Name.Trim()}.wsb";
}

/// <summary>The configuration file of Windows Sandbox for a template, always written the same way.</summary>
public static class SandboxConfig
{
    public const int MinMemory = 2048;
    public const int MaxMemory = 65536;

    public static string Xml(SandboxElement s)
    {
        static string OnOff(bool on) => on ? "Enable" : "Disable";
        var x = new StringBuilder();
        x.Append("<Configuration>\r\n");
        x.Append($"  <vGPU>{OnOff(s.Gpu)}</vGPU>\r\n");
        x.Append($"  <Networking>{OnOff(s.Networking)}</Networking>\r\n");
        if (s.Folders.Count > 0)
        {
            x.Append("  <MappedFolders>\r\n");
            foreach (var f in s.Folders)
            {
                x.Append("    <MappedFolder>\r\n");
                x.Append($"      <HostFolder>{SecurityElement.Escape(f.HostFolder)}</HostFolder>\r\n");
                if (f.SandboxFolder is { Length: > 0 } inside)
                {
                    x.Append($"      <SandboxFolder>{SecurityElement.Escape(inside)}</SandboxFolder>\r\n");
                }

                x.Append($"      <ReadOnly>{(f.ReadOnly ? "true" : "false")}</ReadOnly>\r\n");
                x.Append("    </MappedFolder>\r\n");
            }

            x.Append("  </MappedFolders>\r\n");
        }

        if (s.LogonCommand is { Length: > 0 } command)
        {
            x.Append($"  <LogonCommand>\r\n    <Command>{SecurityElement.Escape(command)}</Command>\r\n  </LogonCommand>\r\n");
        }

        x.Append($"  <AudioInput>{OnOff(s.Microphone)}</AudioInput>\r\n");
        x.Append($"  <VideoInput>{OnOff(s.Camera)}</VideoInput>\r\n");
        x.Append($"  <ProtectedClient>{OnOff(s.ProtectedClient)}</ProtectedClient>\r\n");
        x.Append($"  <PrinterRedirection>{OnOff(s.Printer)}</PrinterRedirection>\r\n");
        x.Append($"  <ClipboardRedirection>{OnOff(s.Clipboard)}</ClipboardRedirection>\r\n");
        x.Append($"  <MemoryInMB>{s.MemoryMb.ToString(CultureInfo.InvariantCulture)}</MemoryInMB>\r\n");
        x.Append("</Configuration>\r\n");
        return x.ToString();
    }
}

/// <summary>A program in the School store: what the pupil sees and how it is installed.</summary>
public sealed class StoreItem
{
    public string Id { get; set; } = "";

    public string Title { get; set; } = "";

    public string Description { get; set; } = "";

    public ProgramElement Program { get; set; } = new();
}

/// <summary>The School store: programs a pupil of a shared account may install for the whole PC without the password of an
/// administrator. The agent installs them and removes them when the session of the account is cleaned.</summary>
public sealed class StoreElement : Element
{
    /// <summary>Shared accounts allowed to use the store (e.g. ziak).</summary>
    public List<string> Accounts { get; set; } = [];

    /// <summary>At most so many programs in one session.</summary>
    public int MaxPerSession { get; set; } = 3;

    public List<StoreItem> Items { get; set; } = [];

    public override string Key => "store";

    public override string Title => $"Školský obchod ({Items.Count} programov)";
}

/// <summary>A program installed from the store for a session – kept by the agent, so it can be removed even after the
/// catalogue changed.</summary>
public sealed class StoreInstall
{
    public string Item { get; set; } = "";

    public string Account { get; set; } = "";

    public DateTimeOffset InstalledUtc { get; set; }

    public ProgramElement Program { get; set; } = new();
}

/// <summary>A request of the pupil's program to the agent and its answer (one line of JSON each way).</summary>
public sealed class StoreRequest
{
    /// <summary>"list" or "install".</summary>
    public string Command { get; set; } = "";

    public string? Item { get; set; }
}

public sealed class StoreOffer
{
    public string Id { get; set; } = "";

    public string Title { get; set; } = "";

    public string Description { get; set; } = "";

    public bool Installed { get; set; }
}

public sealed class StoreAnswer
{
    public bool Ok { get; set; }

    public string Message { get; set; } = "";

    public List<StoreOffer> Items { get; set; } = [];
}

[JsonSourceGenerationOptions(WriteIndented = false, UseStringEnumConverter = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(StoreElement))]
[JsonSerializable(typeof(StoreInstall))]
[JsonSerializable(typeof(StoreRequest))]
[JsonSerializable(typeof(StoreAnswer))]
internal sealed partial class StoreJson : JsonSerializerContext;

/// <summary>What the store allows – the same rules for the agent and the tests.</summary>
public static class StoreRules
{
    public const int MaxRequest = 4096;

    public static string ToJson(StoreElement s) => JsonSerializer.Serialize(s, StoreJson.Default.StoreElement);

    public static StoreElement? CatalogueFromJson(string json) => JsonSerializer.Deserialize(json, StoreJson.Default.StoreElement);

    public static string ToJson(StoreInstall s) => JsonSerializer.Serialize(s, StoreJson.Default.StoreInstall);

    public static StoreInstall? InstallFromJson(string json) => JsonSerializer.Deserialize(json, StoreJson.Default.StoreInstall);

    public static string ToJson(StoreRequest s) => JsonSerializer.Serialize(s, StoreJson.Default.StoreRequest);

    public static string ToJson(StoreAnswer s) => JsonSerializer.Serialize(s, StoreJson.Default.StoreAnswer);

    public static StoreAnswer? AnswerFromJson(string json) => JsonSerializer.Deserialize(json, StoreJson.Default.StoreAnswer);

    /// <summary>A request of at most <see cref="MaxRequest"/> characters; anything else is null (refused).</summary>
    public static StoreRequest? RequestFromJson(string? line)
    {
        if (line is null || line.Length > MaxRequest)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize(line, StoreJson.Default.StoreRequest);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Whether an account may install an item now; null = yes, otherwise the reason in Slovak.</summary>
    public static string? Refusal(StoreElement? catalogue, string account, bool administrator, IReadOnlyCollection<StoreInstall> installed, string? itemId, bool alreadyThere)
    {
        if (catalogue is null || catalogue.Items.Count == 0)
        {
            return "Školský obchod je na tomto PC zatvorený.";
        }

        if (administrator)
        {
            return "Správca inštaluje programy receptom, nie cez Školský obchod.";
        }

        if (!catalogue.Accounts.Contains(account, StringComparer.OrdinalIgnoreCase))
        {
            return $"Konto {account} nemôže používať Školský obchod.";
        }

        if (catalogue.Items.FirstOrDefault(i => i.Id == itemId) is not { } item)
        {
            return "Taký program v Školskom obchode nie je.";
        }

        if (installed.Any(i => i.Item == item.Id))
        {
            return $"{item.Title} je už nainštalovaný.";
        }

        if (alreadyThere)
        {
            return $"{item.Title} je na tomto PC nainštalovaný natrvalo.";
        }

        return installed.Count(i => string.Equals(i.Account, account, StringComparison.OrdinalIgnoreCase)) >= catalogue.MaxPerSession
            ? $"V jednej relácii sa dá nainštalovať najviac {catalogue.MaxPerSession} programov."
            : null;
    }
}
