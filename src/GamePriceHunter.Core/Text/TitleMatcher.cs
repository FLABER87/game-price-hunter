using System.Text.RegularExpressions;

namespace GamePriceHunter.Core.Text;

public enum MatchMode
{
    /// <summary>Every query word must appear in the title (the last one may be a prefix).</summary>
    AllWords,

    /// <summary>The cleaned title must equal the query.</summary>
    Exact,
}

/// <summary>Scores how well a store title matches a user query.</summary>
public sealed partial class TitleMatcher
{
    private static readonly HashSet<string> StopWords = ["the", "a", "an", "of", "and"];

    private readonly string[] _queryTokens;
    private readonly string[] _significantTokens;
    private readonly string _compactQuery;
    private readonly MatchMode _mode;

    public TitleMatcher(string query, MatchMode mode = MatchMode.AllWords)
    {
        _mode = mode;
        _queryTokens = TextNormalizer.Tokenize(query);
        var significant = _queryTokens.Where(t => !StopWords.Contains(t)).ToArray();
        _significantTokens = significant.Length > 0 ? significant : _queryTokens;
        _compactQuery = string.Concat(_queryTokens);
    }

    public bool IsEmpty => _queryTokens.Length == 0;

    /// <summary>Returns 0 when the title does not match, otherwise a score in (0, 1].</summary>
    public double Score(string title)
    {
        if (IsEmpty)
            return 0;

        var titleTokens = TextNormalizer.Tokenize(title);
        if (titleTokens.Length == 0)
            return 0;

        if (_mode == MatchMode.Exact)
        {
            var clean = TextNormalizer.Tokenize(CleanTitle(title));
            return TokensEqual(clean, _queryTokens) || TokensEqual(titleTokens, _queryTokens) ? 1.0 : 0;
        }

        if (TokensEqual(titleTokens, _queryTokens))
            return 1.0;

        var matchedAll = true;
        for (var i = 0; i < _significantTokens.Length; i++)
        {
            // The last word may be unfinished ("witch" → "witcher"); earlier words may only
            // differ by a short ending ("assassin" → "assassins", but not "knight" → "knightfall").
            var maxExtra = i == _significantTokens.Length - 1 ? int.MaxValue : 2;
            if (!titleTokens.Any(t => TokenMatches(_significantTokens[i], t, maxExtra)))
            {
                matchedAll = false;
                break;
            }
        }

        if (!matchedAll)
            return CompactMatch(titleTokens) ? 0.4 : 0;

        // The game name plus store noise ("Witcher 3 (PC) Steam Key GLOBAL") is nearly exact.
        if (TokensEqual(TextNormalizer.Tokenize(CleanTitle(title)), _queryTokens))
            return 0.95;

        var score = 0.5;
        if (StartsWithQuery(titleTokens))
            score += 0.2;

        // Prefer titles that carry little beyond the query ("Witcher 3" over "Witcher 3 Soundtrack").
        score += 0.2 * Math.Min(1.0, (double)_significantTokens.Length / titleTokens.Length);
        return Math.Min(0.9, score);
    }

    /// <summary>
    /// Removes bracketed notes and store noise such as "Steam Key GLOBAL" so that
    /// marketplace titles can be compared with the plain game name.
    /// </summary>
    public static string CleanTitle(string title)
    {
        var text = BracketsRegex().Replace(title, " ");
        text = NoiseTailRegex().Replace(text, " ");
        text = NoiseWordsRegex().Replace(text, " ");
        return text.Trim(' ', '-', '–', '—', '|', '*', ',', '.', ':');
    }

    /// <summary>
    /// "hollowknight" / "half life" vs "Half-Life": compares without spaces, but only from a
    /// word boundary so "witcher" does not match "Switcher".
    /// </summary>
    private bool CompactMatch(string[] titleTokens)
    {
        if (_compactQuery.Length < 5)
            return false;
        for (var i = 0; i < titleTokens.Length; i++)
        {
            if (string.Concat(titleTokens.Skip(i)).StartsWith(_compactQuery, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    private bool StartsWithQuery(string[] titleTokens)
    {
        var tokens = titleTokens;
        if (tokens.Length > 0 && tokens[0] == "the" && _queryTokens.Length > 0 && _queryTokens[0] != "the")
            tokens = tokens[1..];
        if (tokens.Length < _queryTokens.Length)
            return false;
        for (var i = 0; i < _queryTokens.Length; i++)
        {
            if (!TokenMatches(_queryTokens[i], tokens[i], i == _queryTokens.Length - 1 ? int.MaxValue : 2))
                return false;
        }
        return true;
    }

    private static bool TokensEqual(string[] a, string[] b)
    {
        static IEnumerable<string> Significant(string[] tokens) => tokens.Where(t => !StopWords.Contains(t));
        var left = Significant(a).ToArray();
        var right = Significant(b).ToArray();
        if (left.Length == 0 || left.Length != right.Length)
            return false;
        for (var i = 0; i < left.Length; i++)
        {
            if (!TokenMatches(right[i], left[i], maxExtra: 0))
                return false;
        }
        return true;
    }

    /// <param name="maxExtra">How many characters the title word may have beyond the query word.</param>
    private static bool TokenMatches(string queryToken, string titleToken, int maxExtra)
    {
        if (queryToken == titleToken)
            return true;

        var queryNumber = ToNumber(queryToken);
        if (queryNumber is not null)
            return queryNumber == ToNumber(titleToken);

        return maxExtra > 0
            && queryToken.Length >= 2
            && titleToken.Length - queryToken.Length <= maxExtra
            && titleToken.StartsWith(queryToken, StringComparison.Ordinal);
    }

    /// <summary>Arabic digits or roman numerals II..XX ("iii" == "3").</summary>
    private static int? ToNumber(string token)
    {
        if (token.Length <= 4 && int.TryParse(token, out var value))
            return value;
        return token switch
        {
            "ii" => 2, "iii" => 3, "iv" => 4, "v" => 5, "vi" => 6, "vii" => 7, "viii" => 8, "ix" => 9, "x" => 10,
            "xi" => 11, "xii" => 12, "xiii" => 13, "xiv" => 14, "xv" => 15, "xvi" => 16,
            _ => null,
        };
    }

    [GeneratedRegex(@"\([^)]*\)|\[[^\]]*\]|\{[^}]*\}")]
    private static partial Regex BracketsRegex();

    // A separator (" - ", " | ", "✅", "·"...) immediately followed by a store word starts a noise
    // tail: "Elden Ring - Steam Gift", "Hollow Knight ✅STEAM GIFT AUTO". A real subtitle in
    // between ("- Gods & Nightmares ✅STEAM") stops the match at that separator.
    [GeneratedRegex(@"\s*[^\p{L}\p{N}\s()\[\]'’&:.,!?]+\s*(?:pc\s+)?(?:steam|gog|epic|origin|uplay|ubisoft|key|gift|account|global|worldwide|region|europe|eu|cis|row|ru)\b.*$", RegexOptions.IgnoreCase)]
    private static partial Regex NoiseTailRegex();

    [GeneratedRegex(@"\b(pc|steam|gog|epic games|cd[- ]?key|key|gift|global|region free|ww|worldwide|eu|row|digital (download|code)|download)\b", RegexOptions.IgnoreCase)]
    private static partial Regex NoiseWordsRegex();
}
