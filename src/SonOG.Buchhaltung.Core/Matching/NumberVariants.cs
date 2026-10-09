namespace SonOG.Buchhaltung.Core.Matching;

/// <summary>Hilfen für Tippfehler und Zahlendreher in Rechnungsnummern und Beträgen.</summary>
public static class NumberVariants
{
    /// <summary>
    /// Mögliche richtige Nummern zu einer (falschen) Nummer, nach Wahrscheinlichkeit geordnet:
    /// 1. zwei benachbarte Ziffern vertauscht, 2. beliebige zwei Ziffern vertauscht, 3. eine Ziffer falsch.
    /// </summary>
    public static IEnumerable<string> Of(string number)
    {
        var seen = new HashSet<string> { number };
        int n = number.Length;

        for (int i = 0; i < n - 1; i++)
            if (number[i] != number[i + 1] && TrySwap(number, i, i + 1, seen, out var s)) yield return s;

        for (int i = 0; i < n; i++)
            for (int j = i + 2; j < n; j++)
                if (number[i] != number[j] && TrySwap(number, i, j, seen, out var s)) yield return s;

        for (int i = 0; i < n; i++)
        {
            for (char d = '0'; d <= '9'; d++)
            {
                if (d == number[i]) continue;
                var c = number.ToCharArray();
                c[i] = d;
                var s = new string(c);
                if (seen.Add(s)) yield return s;
            }
        }
    }

    private static bool TrySwap(string number, int i, int j, HashSet<string> seen, out string result)
    {
        var c = number.ToCharArray();
        (c[i], c[j]) = (c[j], c[i]);
        result = new string(c);
        return seen.Add(result);
    }

    /// <summary>True, wenn sich die Beträge nur durch zwei vertauschte Nachbarziffern unterscheiden (z. B. 45,90 und 54,90).</summary>
    public static bool AmountsDifferByTransposition(decimal a, decimal b)
    {
        if (a == b) return false;
        var x = ((long)Math.Round(Math.Abs(a) * 100)).ToString();
        var y = ((long)Math.Round(Math.Abs(b) * 100)).ToString();
        if (x.Length != y.Length || x.Length < 2) return false;

        var diff = new List<int>();
        for (int i = 0; i < x.Length; i++)
            if (x[i] != y[i]) diff.Add(i);

        return diff.Count == 2 && diff[1] == diff[0] + 1 && x[diff[0]] == y[diff[1]] && x[diff[1]] == y[diff[0]];
    }
}
