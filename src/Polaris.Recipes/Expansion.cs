using Polaris.Json;

namespace Polaris.Recipes;

/// <summary>Elements of a higher kind made of simpler ones: bookmarks are policy values in the registry. The agent then
/// needs fewer executors, and what is written is checked in tests on any system.</summary>
public static class Expansion
{
    public const string EdgePolicies = @"HKLM\SOFTWARE\Policies\Microsoft\Edge";
    public const string ChromePolicies = @"HKLM\SOFTWARE\Policies\Google\Chrome";

    /// <summary>The simple elements an element consists of (a simple element is itself).</summary>
    public static IEnumerable<Element> Expand(Element element) => element switch
    {
        BookmarksElement b => Bookmarks(b),
        KeyboardsElement k => KeyboardLayouts.Expand(k),
        _ => [element],
    };

    public static IEnumerable<Element> ExpandAll(IEnumerable<Element> elements) => elements.SelectMany(Expand);

    private static IEnumerable<Element> Bookmarks(BookmarksElement b)
    {
        var json = ManagedBookmarksJson(b);
        foreach (var browser in b.Browsers.Distinct().Order())
        {
            var (path, bar) = browser switch
            {
                Browser.Edge => (EdgePolicies, "FavoritesBarEnabled"),
                Browser.Chrome => (ChromePolicies, "BookmarkBarEnabled"),
                _ => throw new InvalidDataException($"Neznámy prehliadač {browser}."),
            };
            var name = browser.ToString().ToLowerInvariant();
            yield return new RegistryValueElement
            {
                Id = $"{b.Id}.{name}",
                Present = b.Present,
                Note = b.Title,
                Path = path,
                Name = "ManagedBookmarks",
                Kind = RegistryKind.String,
                Data = json,
            };
            if (b.ShowBar)
            {
                yield return new RegistryValueElement
                {
                    Id = $"{b.Id}.{name}.lista",
                    Present = b.Present,
                    Note = b.Title,
                    Path = path,
                    Name = bar,
                    Kind = RegistryKind.DWord,
                    Data = "1",
                };
            }
        }
    }

    /// <summary>The policy ManagedBookmarks of Edge and Chrome: a list starting with the name of the top folder, then the
    /// bookmarks (name + url) and folders (name + children).</summary>
    public static string ManagedBookmarksJson(BookmarksElement b)
    {
        var list = new JsonArray();
        if (b.Folder.Length > 0)
        {
            list.Add(new JsonObject().Add("toplevel_name", b.Folder));
        }

        foreach (var item in b.Items)
        {
            list.Add(Bookmark(item));
        }

        return JsonText.Canonical(list);
    }

    private static JsonObject Bookmark(Bookmark item) => item.Children is { } children
        ? new JsonObject().Add("name", item.Name).Add("children", JsonArray.Of(children, Bookmark))
        : new JsonObject().Add("name", item.Name).Add("url", item.Url ?? "");
}
