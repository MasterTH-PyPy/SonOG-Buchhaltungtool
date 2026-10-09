using System.Text.RegularExpressions;
using SonOG.Buchhaltung.Core.Models;
using SonOG.Buchhaltung.Core.Rules;

namespace SonOG.Buchhaltung.Core.Classification;

/// <summary>Ordnet Buchungen einer Kategorie zu und liest Bestell-/Rechnungsnummern und Zahlernamen aus dem Text.</summary>
public sealed class BookingClassifier
{
    private static readonly Regex AmazonOrderRx = new(@"\b(\d{3}-\d{7}-\d{7})\b", RegexOptions.Compiled);

    // Lastschrift-Einzug: "SVWZ+Re-Nr. 3 4138, 34140 DATUM ..." - der Zeilenumbruch kann mitten in einer Nummer liegen.
    private static readonly Regex EinzugNumbersRx = new(@"Re-?\s?Nr[.:]?\s*(?<seg>.*?)\s+DATUM", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex PayerMarkerRx = new(
        @"\b(?:re\.?\s*-?\s*nr|r\.?\s*-?\s*nr|rechnung(?:s-?\s*nr|snummer)?|rg\.?\s*-?\s*nr|rg|rech\.?|inv(?:oice)?|k-?\s*nr|kd-?\s*nr)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex TrailingMandateRx = new(@"\s+(?:[0-9a-f]{8}|\d{8})$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly Regex _ownInvoiceRx;
    private readonly int _statementYear;

    public BookingClassifier(AppRules rules, int statementYear)
    {
        _ownInvoiceRx = new Regex(rules.EigeneRechnungsnummerRegex, RegexOptions.Compiled);
        _statementYear = statementYear;
    }

    public void Classify(Booking b)
    {
        b.OwnInvoiceNumbers = new List<string>();
        b.NumbersDerivedFromShortForm = false;
        b.AmazonOrder = "";

        var upper = b.Text.ToUpperInvariant();
        if (upper.Contains("AMAZON"))
        {
            var m = AmazonOrderRx.Match(b.Text);
            b.AmazonOrder = m.Success ? m.Groups[1].Value : "";
            b.Category = b.Amount > 0 ? BookingCategory.AmazonErstattung : BookingCategory.AmazonAusgabe;
            return;
        }

        var numbers = ReadOwnInvoiceNumbers(b);
        b.PayerName = ReadPayerName(b, numbers);

        if (numbers.Count > 0)
        {
            b.OwnInvoiceNumbers = numbers;
            b.Category = numbers.Count > 1 ? BookingCategory.KundenSammelzahlung : BookingCategory.Kundenzahlung;
            return;
        }

        if (b.Amount > 0) b.Category = BookingCategory.EingangSonstige;
        else if (b.Type.StartsWith("Entgeltabrechnung", StringComparison.OrdinalIgnoreCase)) b.Category = BookingCategory.Bankgebuehren;
        else b.Category = BookingCategory.AusgabeSonstige;
    }

    private List<string> ReadOwnInvoiceNumbers(Booking b)
    {
        var found = new List<string>();
        if (b.Amount <= 0) return found;

        if (b.Type.Contains("Einzug", StringComparison.OrdinalIgnoreCase))
        {
            var m = EinzugNumbersRx.Match(b.Text);
            if (m.Success)
            {
                var seg = Regex.Replace(m.Groups["seg"].Value, @"\s+", "");
                foreach (var part in seg.Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (Regex.IsMatch(part, @"^\d{5}$"))
                    {
                        found.Add(_statementYear.ToString() + part);
                        b.NumbersDerivedFromShortForm = true;
                    }
                    else if (Regex.IsMatch(part, @"^\d{9}$"))
                    {
                        found.Add(part);
                    }
                }
            }
            return found.Distinct().ToList();
        }

        foreach (Match m in _ownInvoiceRx.Matches(b.Text))
            found.Add(m.Groups[1].Value);
        return found.Distinct().ToList();
    }

    private static string ReadPayerName(Booking b, List<string> numbers)
    {
        var text = b.Text;

        var svwz = text.IndexOf("SVWZ", StringComparison.Ordinal);
        if (svwz > 0)
        {
            var name = text[..svwz].Trim();
            return TrailingMandateRx.Replace(name, "").Trim();
        }

        int idx = -1;
        var marker = PayerMarkerRx.Match(text);
        if (marker.Success) idx = marker.Index;
        foreach (var n in numbers)
        {
            var p = text.IndexOf(n, StringComparison.Ordinal);
            if (p >= 0 && (idx < 0 || p < idx)) idx = p;
        }
        return idx > 0 ? text[..idx].Trim() : "";
    }
}
