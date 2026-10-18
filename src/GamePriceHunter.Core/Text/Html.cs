using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace GamePriceHunter.Core.Text;

/// <summary>
/// Small, dependency-free helpers for pulling data out of store HTML. Store pages are
/// parsed by splitting them into result blocks and matching patterns inside each block.
/// </summary>
public static partial class Html
{
    private static readonly ConcurrentDictionary<string, Regex> Cache = new();

    /// <summary>Decodes entities and collapses whitespace.</summary>
    public static string Decode(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;
        return WhitespaceRegex().Replace(WebUtility.HtmlDecode(text), " ").Trim();
    }

    /// <summary>Removes tags, decodes entities and collapses whitespace.</summary>
    public static string ToText(string? html)
    {
        if (string.IsNullOrEmpty(html))
            return string.Empty;
        return Decode(TagRegex().Replace(html, " "));
    }

    /// <summary>
    /// Splits <paramref name="html"/> into blocks, each starting at an occurrence of
    /// <paramref name="marker"/> and ending right before the next one.
    /// </summary>
    public static IEnumerable<string> Blocks(string html, string marker)
    {
        var start = html.IndexOf(marker, StringComparison.Ordinal);
        while (start >= 0)
        {
            var next = html.IndexOf(marker, start + marker.Length, StringComparison.Ordinal);
            yield return next < 0 ? html[start..] : html[start..next];
            start = next;
        }
    }

    /// <summary>First capture group of <paramref name="pattern"/> (case-insensitive, singleline), or null.</summary>
    public static string? Match(string input, string pattern)
    {
        var match = GetRegex(pattern).Match(input);
        if (!match.Success)
            return null;
        return match.Groups.Count > 1 ? match.Groups[1].Value : match.Value;
    }

    public static IEnumerable<System.Text.RegularExpressions.Match> Matches(string input, string pattern) =>
        GetRegex(pattern).Matches(input);

    /// <summary>Value of an attribute inside a tag fragment: Attr("&lt;a href='x'&gt;", "href") → "x".</summary>
    public static string? Attr(string tag, string name)
    {
        var match = GetRegex($@"\b{Regex.Escape(name)}\s*=\s*(?:""([^""]*)""|'([^']*)'|([^\s>]+))").Match(tag);
        if (!match.Success)
            return null;
        for (var i = 1; i < match.Groups.Count; i++)
        {
            if (match.Groups[i].Success)
                return WebUtility.HtmlDecode(match.Groups[i].Value);
        }
        return null;
    }

    /// <summary>Contents of a &lt;script&gt; element with the given id (e.g. "__NEXT_DATA__").</summary>
    public static string? ScriptById(string html, string id) =>
        Match(html, $@"<script[^>]*\bid\s*=\s*[""']{Regex.Escape(id)}[""'][^>]*>(.*?)</script>");

    /// <summary>
    /// Finds <paramref name="marker"/> and returns the balanced JSON object or array that
    /// follows it, e.g. ExtractJson(html, "window.__STATE__ =").
    /// </summary>
    public static string? ExtractJson(string text, string marker)
    {
        var index = text.IndexOf(marker, StringComparison.Ordinal);
        if (index < 0)
            return null;

        var start = index + marker.Length;
        while (start < text.Length && text[start] != '{' && text[start] != '[')
        {
            if (!char.IsWhiteSpace(text[start]) && text[start] != '=' && text[start] != ':' && text[start] != '(')
                return null;
            start++;
        }
        if (start >= text.Length)
            return null;

        var depth = 0;
        var inString = false;
        var quote = '"';
        for (var i = start; i < text.Length; i++)
        {
            var ch = text[i];
            if (inString)
            {
                if (ch == '\\')
                    i++;
                else if (ch == quote)
                    inString = false;
                continue;
            }

            switch (ch)
            {
                case '"' or '\'':
                    inString = true;
                    quote = ch;
                    break;
                case '{' or '[':
                    depth++;
                    break;
                case '}' or ']':
                    depth--;
                    if (depth == 0)
                        return text[start..(i + 1)];
                    break;
            }
        }
        return null;
    }

    /// <summary>Unescapes a JavaScript string literal body ("A\/" → "A/").</summary>
    public static string UnescapeJs(string value)
    {
        if (!value.Contains('\\'))
            return value;
        var builder = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            var ch = value[i];
            if (ch != '\\' || i == value.Length - 1)
            {
                builder.Append(ch);
                continue;
            }

            var next = value[++i];
            switch (next)
            {
                case 'n': builder.Append('\n'); break;
                case 't': builder.Append('\t'); break;
                case 'r': builder.Append('\r'); break;
                case 'u' when i + 4 < value.Length && int.TryParse(value.AsSpan(i + 1, 4), System.Globalization.NumberStyles.HexNumber, null, out var code):
                    builder.Append((char)code);
                    i += 4;
                    break;
                default: builder.Append(next); break;
            }
        }
        return builder.ToString();
    }

    private static Regex GetRegex(string pattern) =>
        Cache.GetOrAdd(pattern, p => new Regex(p, RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2)));

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex TagRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
