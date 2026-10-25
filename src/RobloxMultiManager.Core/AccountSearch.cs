using System.Text.RegularExpressions;

namespace RobloxMultiManager.Core;

public static class AccountSearch
{
    private static readonly Regex Terms = new("(?:[^\\s\"]+|\"[^\"]*\")+", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(100));
    public static bool Matches(AccountProfile profile, AccountMetadata? metadata, IEnumerable<AccountGroup> groups, string query)
    {
        if (query.Length > 1000) return false;
        static bool Has(string? value, string text) => value?.Contains(text, StringComparison.OrdinalIgnoreCase) == true;
        foreach (Match token in Terms.Matches(query))
        {
            var term = token.Value; var separator = term.IndexOf(':');
            var kind = separator > 0 ? term[..separator].ToLowerInvariant() : "";
            var text = (separator > 0 ? term[(separator + 1)..] : term).Trim('"');
            bool Fields() => metadata?.Fields?.Any(f => Has(f.Key, text) || Has(f.Value, text)) == true;
            bool Tags() => metadata?.Tags.Any(t => Has(t, text)) == true;
            bool Groups() => groups.Any(g => g.Accounts.Contains(profile.Id) && Has(g.Name, text));
            var match = kind switch
            {
                "tag" => Tags(), "group" => Groups(), "note" => Has(metadata?.Notes, text), "field" => Fields(),
                "user" => Has(profile.RobloxUsername, text), "id" => Has(profile.RobloxUserId?.ToString(), text),
                "name" => Has(profile.Name, text),
                _ => Has(profile.Name, term.Trim('"')) || Has(profile.AccountDisplay, term.Trim('"')) || Has(profile.RobloxUserId?.ToString(), term)
                    || Has(metadata?.Notes, term.Trim('"')) || Tags() || Fields() || Groups()
            };
            if (!match) return false;
        }
        return true;
    }
}
