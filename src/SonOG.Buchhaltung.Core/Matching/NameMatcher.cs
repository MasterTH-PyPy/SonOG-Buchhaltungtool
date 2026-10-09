using System.Globalization;
using System.Text;

namespace SonOG.Buchhaltung.Core.Matching;

/// <summary>Unscharfer Namensvergleich zwischen Zahler (Kontoauszug) und Rechnungsempfänger.</summary>
public static class NameMatcher
{
    private static readonly HashSet<string> Stop = new(StringComparer.Ordinal)
    {
        "gmbh", "mbh", "kg", "ag", "ug", "ohg", "gbr", "co", "und", "the", "herr", "frau", "dr", "prof",
        "ek", "inh", "inhaber", "eg", "se", "ltd", "der", "die", "das", "fuer", "von", "vom", "www", "de", "com",
    };

    /// <summary>Kleinschreibung, Umlaute ausgeschrieben (ä -> ae), übrige Akzente entfernt, nur Buchstaben und Ziffern.</summary>
    public static string Normalize(string s)
    {
        var t = s.ToLowerInvariant()
            .Replace("ä", "ae").Replace("ö", "oe").Replace("ü", "ue").Replace("ß", "ss");
        var sb = new StringBuilder(t.Length);
        foreach (var ch in t.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            sb.Append(char.IsLetterOrDigit(ch) ? ch : ' ');
        }
        return sb.ToString();
    }

    public static List<string> Tokens(string s) =>
        Normalize(s).Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(t => t.Length >= 3 && !Stop.Contains(t))
            .ToList();

    /// <summary>Anteil der Namensbestandteile von <paramref name="payer"/>, die im <paramref name="recipientText"/> vorkommen (0..1).</summary>
    public static double Score(string payer, string recipientText)
    {
        var a = Tokens(payer);
        if (a.Count == 0) return 0;
        var b = Tokens(recipientText);
        if (b.Count == 0) return 0;
        int hit = a.Count(ta => b.Any(tb => TokenEquals(ta, tb)));
        return (double)hit / a.Count;
    }

    /// <summary>True, wenn mindestens ein Namensbestandteil (ab 4 Zeichen) aus <paramref name="name"/> im Text vorkommt.</summary>
    public static bool ContainsAnyToken(string text, string name)
    {
        var haystack = Tokens(text);
        return Tokens(name).Any(t => t.Length >= 4 && haystack.Any(h => TokenEquals(t, h)));
    }

    private static bool TokenEquals(string a, string b)
    {
        if (a == b) return true;
        int min = Math.Min(a.Length, b.Length);
        if (min >= 4 && (a.StartsWith(b, StringComparison.Ordinal) || b.StartsWith(a, StringComparison.Ordinal))) return true;
        return min >= 5 && Math.Abs(a.Length - b.Length) <= 1 && Levenshtein(a, b) <= 1;
    }

    private static int Levenshtein(string a, string b)
    {
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) prev[j] = j;
        for (int i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (int j = 1; j <= b.Length; j++)
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }
}
