using System.Runtime.Versioning;
using Polaris.Recipes;

namespace Polaris.Agent;

/// <summary>The local group policy of the PC (what gpedit.msc edits): Registry.pol of the machine or of users and gpt.ini
/// in %windir%\System32\GroupPolicy. After a change Windows itself sets the registry from it (gpupdate for the machine;
/// at the next sign-in for users) – the supported way for policies some protections of Windows (UCPD) let only Windows
/// write. Other policies in the files stay as they are.</summary>
[SupportedOSPlatform("windows")]
public sealed class LocalPolicy(string? folder = null, bool refresh = true)
{
    public static readonly TimeSpan RefreshLimit = TimeSpan.FromMinutes(3);

    public string Folder { get; } = folder ?? Path.Combine(Environment.SystemDirectory, "GroupPolicy");

    public string File(bool user) => Path.Combine(Folder, user ? "User" : "Machine", "Registry.pol");

    public List<PolicyEntry> Entries(bool user)
    {
        var file = File(user);
        return System.IO.File.Exists(file) ? PolicyFile.Read(System.IO.File.ReadAllBytes(file)) : [];
    }

    /// <summary>Sets (present) or removes one value, then lets Windows read the policy again.</summary>
    public void Change(bool user, string key, string name, bool present, RegistryKind kind, string data)
    {
        var entries = Entries(user);
        var changed = present
            ? PolicyFile.Set(entries, PolicyFile.Entry(key, name, kind, data))
            : PolicyFile.Without(entries, key, name);
        var file = File(user);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        System.IO.File.WriteAllBytes(file + ".novy", PolicyFile.Write(changed));
        System.IO.File.Move(file + ".novy", file, overwrite: true);

        var ini = Path.Combine(Folder, "gpt.ini");
        var text = System.IO.File.Exists(ini) ? System.IO.File.ReadAllText(ini) : "";
        System.IO.File.WriteAllText(ini, PolicyIni.Bump(text, user));
        if (refresh && !user)
        {
            Refresh();
        }
    }

    /// <summary>Whether the file has the value as wanted (for policies of users, which Windows sets only at sign-in).</summary>
    public bool Has(bool user, string key, string name, RegistryKind kind, string data)
    {
        var found = PolicyFile.Find(Entries(user), key, name);
        var wanted = PolicyFile.Entry(key, name, kind, data);
        return found is not null && found.Type == wanted.Type && found.Data.AsSpan().SequenceEqual(wanted.Data);
    }

    /// <summary>gpupdate for the machine part – Windows sets the registry now.</summary>
    public static void Refresh()
    {
        var gpupdate = Path.Combine(Environment.SystemDirectory, "gpupdate.exe");
        var result = Processes.Run(gpupdate, ["/target:computer", "/force"], RefreshLimit);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"gpupdate skončil s kódom {result.Code}: {result.OutputTail}");
        }
    }
}

/// <summary>Executor of the policies of users: their entries in the user part of the local group policy.</summary>
[SupportedOSPlatform("windows")]
public sealed class UserPolicyHandler(LocalPolicy? policy = null) : IElementHandler
{
    private readonly LocalPolicy _policy = policy ?? new LocalPolicy();

    public bool CanHandle(Element element) => element is UserPolicyElement;

    public Observation Observe(Element element)
    {
        var p = (UserPolicyElement)element;
        var found = PolicyFile.Find(_policy.Entries(user: true), p.Path, p.Name);
        if (!p.Present)
        {
            return found is null ? new Observation(Observed.Absent) : new Observation(Observed.Present, "v miestnej politike používateľov");
        }

        return found is null
            ? new Observation(Observed.Absent, "v miestnej politike používateľov nie je")
            : _policy.Has(true, p.Path, p.Name, p.Kind, p.Data)
                ? new Observation(Observed.Present, "Windows ju nastaví pri každom prihlásení")
                : new Observation(Observed.Different, "v miestnej politike používateľov je iná hodnota");
    }

    public void Apply(Element element)
    {
        var p = (UserPolicyElement)element;
        _policy.Change(user: true, p.Path, p.Name, p.Present, p.Kind, p.Data);
    }
}
