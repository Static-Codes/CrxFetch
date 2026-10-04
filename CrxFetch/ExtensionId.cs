using System.Text.RegularExpressions;

namespace CrxFetch;

/// <summary>Extracts 32-character Chrome extension ids from ids and store URLs.</summary>
public static partial class ExtensionId
{
    [GeneratedRegex(@"^[a-p]{32}$", RegexOptions.CultureInvariant)]
    private static partial Regex CrxIdPattern();

    [GeneratedRegex(@"(?<![a-z0-9])[a-p]{32}(?![a-z0-9])", RegexOptions.CultureInvariant)]
    private static partial Regex EmbeddedIdPattern();

    /// <summary>
    /// Accepts a bare 32-character id or any Chrome Web Store URL and pulls the id out of it.
    /// The id is the only interesting part of a store URL; the slug is cosmetic and changes.
    /// </summary>
    /// <param name="input">An extension id or a store URL containing one.</param>
    /// <param name="id">The extracted id, or an empty string on failure.</param>
    /// <returns><see langword="true"/> when an id was found.</returns>
    public static bool TryParse(string? input, out string id)
    {
        id = string.Empty;
        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        var candidate = input.Trim();

        if (CrxIdPattern().IsMatch(candidate))
        {
            id = candidate;
            return true;
        }

        if (!candidate.Contains("://", StringComparison.Ordinal))
        {
            return false;
        }

        // Store urls put the id last in the path: /detail/<slug>/<id>, optionally with /<version>.
        var segments = candidate.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var i = segments.Length - 1; i >= 0; i--)
        {
            if (CrxIdPattern().IsMatch(segments[i]))
            {
                id = segments[i];
                return true;
            }
        }

        // Fall back to scanning for a bare id anywhere in the string (query strings, etc).
        // CrxIdPattern is anchored, so it needs an unanchored pattern to find embedded ids.
        foreach (Match match in EmbeddedIdPattern().Matches(candidate))
        {
            id = match.Value;
            return true;
        }

        return false;
    }
}