using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Polaris.Recipes;

namespace Polaris.Agent;

/// <summary>Executor of the folder element: makes the folder when missing and adds the rights (inherited by everything
/// inside) through the access control of Windows; without the element it takes away only the rights it gave. The folder
/// and its content are never deleted.</summary>
[SupportedOSPlatform("windows")]
public sealed class FolderHandler : IElementHandler
{
    private const InheritanceFlags Inside = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;

    public bool CanHandle(Element element) => element is FolderElement;

    public Observation Observe(Element element)
    {
        var f = (FolderElement)element;
        var path = f.Path.Trim().TrimEnd('\\');
        if (!Directory.Exists(path))
        {
            return new Observation(Observed.Absent, "priečinok nie je");
        }

        var rules = new DirectoryInfo(path).GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier)).OfType<FileSystemAccessRule>().ToList();
        var given = f.Access.Where(a => Has(rules, Sid(a.Who), Rights(a.Rights), explicitOnly: !f.Present)).ToList();
        if (!f.Present)
        {
            return given.Count > 0 ? new Observation(Observed.Present, "recept dal práva " + Describe(given)) : new Observation(Observed.Absent);
        }

        var missing = f.Access.Except(given).ToList();
        return missing.Count == 0
            ? new Observation(Observed.Present, Describe(f.Access))
            : new Observation(Observed.Different, "chýba " + Describe(missing));
    }

    public void Apply(Element element)
    {
        var f = (FolderElement)element;
        var path = f.Path.Trim().TrimEnd('\\');
        if (f.Present)
        {
            Directory.CreateDirectory(path);
        }
        else if (!Directory.Exists(path))
        {
            return;
        }

        var folder = new DirectoryInfo(path);
        var security = folder.GetAccessControl();
        foreach (var a in f.Access)
        {
            var rule = new FileSystemAccessRule(Sid(a.Who), Rights(a.Rights), Inside, PropagationFlags.None, AccessControlType.Allow);
            if (f.Present)
            {
                security.AddAccessRule(rule);
            }
            else
            {
                security.RemoveAccessRuleSpecific(rule);
            }
        }

        folder.SetAccessControl(security);
    }

    public static SecurityIdentifier Sid(string who) => who.Trim() switch
    {
        "Users" => new SecurityIdentifier(FolderRules.UsersSid),
        "AuthenticatedUsers" => new SecurityIdentifier(FolderRules.AuthenticatedUsersSid),
        var name => new SecurityIdentifier(Profiles.SidOf(name) ?? throw new InvalidOperationException($"Konto {name} neexistuje.")),
    };

    public static FileSystemRights Rights(FolderRights rights) => rights switch
    {
        FolderRights.Read => FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize,
        _ => FileSystemRights.Modify | FileSystemRights.Synchronize,
    };

    /// <summary>An allowing rule for the SID that covers the rights and reaches inside the folder.</summary>
    private static bool Has(List<FileSystemAccessRule> rules, SecurityIdentifier sid, FileSystemRights rights, bool explicitOnly) =>
        rules.Any(r => r.AccessControlType == AccessControlType.Allow
            && r.IdentityReference.Equals(sid)
            && (r.FileSystemRights & rights) == rights
            && (r.InheritanceFlags & Inside) == Inside
            && (!explicitOnly || !r.IsInherited));

    private static string Describe(IEnumerable<FolderAccess> access) =>
        string.Join(", ", access.Select(a => $"{a.Who}: {(a.Rights == FolderRights.Read ? "čítanie" : "zápis")}"));
}
