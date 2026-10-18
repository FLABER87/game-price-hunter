using System.Globalization;
using System.Text;

namespace GamePriceHunter.Core.Text;

public static class TextNormalizer
{
    /// <summary>
    /// Lower-cases, strips diacritics and trademark signs, drops apostrophes and turns every
    /// other non-alphanumeric character into a single space.
    /// "Assassin’s Creed® II: Deluxe" → "assassins creed ii deluxe".
    /// </summary>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        var decomposed = text.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        var pendingSpace = false;

        foreach (var ch in decomposed)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (category == UnicodeCategory.NonSpacingMark)
                continue;

            if (ch is '\'' or '’' or '‘' or '`' or '´')
                continue;

            if (ch == '&')
            {
                AppendWord(builder, "and", ref pendingSpace);
                continue;
            }

            if (char.IsLetterOrDigit(ch))
            {
                if (pendingSpace && builder.Length > 0)
                    builder.Append(' ');
                pendingSpace = false;
                builder.Append(char.ToLowerInvariant(ch));
            }
            else
            {
                pendingSpace = true;
            }
        }

        return builder.ToString();
    }

    public static string[] Tokenize(string? text) =>
        Normalize(text).Split(' ', StringSplitOptions.RemoveEmptyEntries);

    private static void AppendWord(StringBuilder builder, string word, ref bool pendingSpace)
    {
        if (builder.Length > 0)
            builder.Append(' ');
        builder.Append(word);
        pendingSpace = true;
    }
}
