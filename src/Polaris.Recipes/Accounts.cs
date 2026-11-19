using System.Text.RegularExpressions;

namespace Polaris.Recipes;

/// <summary>A local account the PC must have: always a standard account (never an administrator), its password never
/// expires and its user can never change it – a pupil cannot lock the shared account for the others. Without a password
/// (<see cref="NoPassword"/>) it signs in with one click; Windows then lets such an account in only at the PC itself,
/// never over the network (POLARIS keeps that rule on). <see cref="AutoLogon"/>: the PC signs in to it by itself after
/// it starts. A recipe never deletes an account.</summary>
public sealed class AccountElement : Element
{
    public string Name { get; set; } = "";

    public bool NoPassword { get; set; }

    public bool AutoLogon { get; set; }

    public override string Key => "account:" + Name.Trim().ToLowerInvariant();

    public override string Title => $"konto {Name}" + (NoPassword ? " bez hesla" : "") + (AutoLogon ? ", automatické prihlásenie" : "");
}

/// <summary>The local password policy lets an account have no password: no complexity rule and no minimum length (part
/// of an account without a password – decided by the admin, 17. 11. 2026). Passwords already set stay as they are; only
/// passwords set from now on may be simpler. Never set back by POLARIS by itself.</summary>
public sealed class PasswordPolicyElement : Element
{
    public override string Key => "password-policy";

    public override string Title => "politika hesiel dovolí konto bez hesla";
}

/// <summary>The PC signs in to this account by itself after it starts (part of an account element). Absent = the PC
/// does not sign in to it by itself.</summary>
public sealed class AutoLogonElement : Element
{
    public string Account { get; set; } = "";

    public override string Key => "autologon:" + Account.Trim().ToLowerInvariant();

    public override string Title => $"automatické prihlásenie do konta {Account}";
}

public static partial class AccountRules
{
    public const string LsaKey = @"HKLM\SYSTEM\CurrentControlSet\Control\Lsa";

    /// <summary>An account element is the account itself, the rule of Windows that an account without a password works
    /// only at the PC itself, and the automatic sign-in (present or not).</summary>
    public static IEnumerable<Element> Expand(AccountElement a)
    {
        if (a.NoPassword)
        {
            // the policy first: the empty password is set only after it allows one
            yield return new PasswordPolicyElement { Id = a.Id + ".politika-hesiel", Note = a.Title };
        }

        yield return a;
        if (a.NoPassword)
        {
            // a value of Windows itself (on by default): written when an account has no password, never removed
            yield return new RegistryValueElement
            {
                Id = a.Id + ".len-pri-pc",
                Note = "kontá bez hesla len priamo pri PC, nikdy cez sieť",
                Path = LsaKey,
                Name = "LimitBlankPasswordUse",
                Kind = RegistryKind.DWord,
                Data = "1",
            };
        }

        yield return new AutoLogonElement { Id = a.Id + ".prihlasenie", Account = a.Name.Trim(), Present = a.Present && a.AutoLogon };
    }

    public static IEnumerable<string> Problems(AccountElement a)
    {
        var name = a.Name.Trim();
        if (!NamePattern().IsMatch(name))
        {
            yield return "chýba alebo je neplatné meno konta (lokálne konto, najviac 20 znakov, bez znakov \" / \\ [ ] : ; | = , + * ? < > @).";
        }
        else if (SessionPolicy.NeverClean.Contains(name))
        {
            yield return $"konto „{name}“ je vstavané konto Windows – recept ho nespravuje.";
        }

        if (!a.Present)
        {
            yield return "kontá recept nemaže – vynechaj prvok (konto ostane, POLARIS ho prestane spravovať).";
        }

        if (a.AutoLogon && !a.NoPassword)
        {
            yield return "automatické prihlásenie len do konta bez hesla – inak by heslo muselo ležať v registri.";
        }
    }

    [GeneratedRegex("^[^\"/\\\\\\[\\]:;|=,+*?<>@]{1,20}$")]
    public static partial Regex NamePattern();
}
