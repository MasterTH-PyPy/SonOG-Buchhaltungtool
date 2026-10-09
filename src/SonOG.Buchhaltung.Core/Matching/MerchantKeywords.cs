using System.Text.RegularExpressions;

namespace SonOG.Buchhaltung.Core.Matching;

/// <summary>Leitet aus dem Buchungstext Suchwörter für den Shop/Verkäufer ab (für die Suche in Mail-Postfächern).</summary>
public static class MerchantKeywords
{
    private static readonly HashSet<string> Stop = new(StringComparer.OrdinalIgnoreCase)
    {
        "GMBH", "MBH", "GBR", "KG", "OHG", "SE", "AG", "UG", "EURO", "EUROPE", "SARL", "LASTSCHRIFT", "SEPA", "IBAN", "BIC",
        "SVWZ", "EREF", "MREF", "CRED", "KREF", "RECHNUNG", "RECHNUNGSNR", "RATE", "BEITRAG", "ZAHLUNG", "DATUM", "UHR",
        "ONLINE", "PAYMENTS", "PAYMENT", "SERVICE", "SERVICES", "DEUTSCHLAND", "GERMANY", "KUNDENNUMMER", "VERTRAG", "ENTGELT",
        "ABRECHNUNG", "ÜBERWEISUNG", "GUTSCHRIFT", "EINZUG", "BUCHUNG", "MITGLIED", "NUMMER", "AUFTRAG", "BESTELLUNG",
    };

    private static readonly Regex WordRx = new(@"[\p{L}][\p{L}\-]{3,}", RegexOptions.Compiled);

    /// <summary>Bis zu zwei auffällige Wörter vom Anfang des Buchungstexts (meist der Name des Zahlungsempfängers).</summary>
    public static IReadOnlyList<string> Extract(string bookingText, int max = 2)
    {
        var result = new List<string>();
        foreach (Match m in WordRx.Matches(bookingText))
        {
            var w = m.Value.Trim('-');
            if (w.Length < 4 || Stop.Contains(w) || w.Split('-').Any(part => Stop.Contains(part))) continue;
            if (result.Contains(w, StringComparer.OrdinalIgnoreCase)) continue;
            result.Add(w);
            if (result.Count >= max) break;
        }
        return result;
    }

    private static readonly Regex RefRx = new(
        @"(?:rechnung\w*|re\.?\s?-?\s?nr\.?|rg\.?\s?-?\s?nr\.?|beleg\w*|auftrag\w*|bestell\w*|invoice)\s*[:#]?\s*([A-Z0-9][A-Z0-9\-/]{4,})",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Rechnungs-/Beleg-/Auftragsnummern, die im Buchungstext genannt werden (zum Abgleich mit dem PDF-Text).</summary>
    public static IReadOnlyList<string> ReferenceNumbers(string bookingText)
    {
        var list = new List<string>();
        foreach (Match m in RefRx.Matches(bookingText))
        {
            var v = m.Groups[1].Value.Trim('-', '/');
            if (v.Length >= 5 && v.Any(char.IsDigit) && !list.Contains(v, StringComparer.OrdinalIgnoreCase)) list.Add(v);
        }
        return list;
    }
}
