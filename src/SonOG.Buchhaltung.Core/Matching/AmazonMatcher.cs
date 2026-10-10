using System.Text.RegularExpressions;
using SonOG.Buchhaltung.Core.Models;

namespace SonOG.Buchhaltung.Core.Matching;

public enum ReceiptKind
{
    Unbekannt,
    /// <summary>"Übersicht zur Bestellung #..." - keine Rechnung im Sinne des UStG.</summary>
    Bestelluebersicht,
    /// <summary>Rechnung (Amazon oder Marktplatz-Verkäufer).</summary>
    Rechnung,
    /// <summary>Rechnungskorrektur / Gutschrift (negativer Betrag).</summary>
    Gutschrift,
}

/// <summary>
/// Ein Amazon-Beleg (PDF) mit Bestellnummern, Art und Betrag.
/// Amount ist vorzeichenbehaftet wie im Beleg: Rechnung positiv, Gutschrift negativ.
/// </summary>
public sealed record ReceiptDocument(
    string FilePath,
    IReadOnlyList<string> OrderNumbers,
    ReceiptKind Kind = ReceiptKind.Unbekannt,
    decimal? Amount = null,
    IReadOnlyList<decimal>? AllAmounts = null,
    string SearchText = "",
    bool IsScan = false,
    bool FromOcr = false)
{
    /// <summary>Betrag, der auf dem Konto erscheint: Rechnung 62,70 → -62,70; Gutschrift -20,05 → +20,05.</summary>
    public decimal? BookingAmount => Amount is null ? null : -Amount.Value;

    /// <summary>Dateiname ohne Endung (bei Amazon die Rechnungsnummer, z. B. DE66OD6DABEI).</summary>
    public string DocumentId => Path.GetFileNameWithoutExtension(FilePath);
}

public sealed class AmazonAssignResult
{
    /// <summary>Rechnungen/Gutschriften, die keiner Buchung dieses Auszugs zugeordnet wurden.</summary>
    public List<ReceiptDocument> UnusedDocuments { get; } = new();
}

public static class AmazonMatcher
{
    private static readonly Regex OrderRx = new(@"(?<!\d)(\d{3}-\d{7}-\d{7})(?!\d)", RegexOptions.Compiled);

    private static readonly Regex[] AmountRx =
    {
        Rx(@"Zahl\s*betrag\s*:?\s*(-?\s*[\d.]+,\d{2})"),
        Rx(@"Rechnungs\s*summe\s*:?\s*(-?\s*[\d.]+,\d{2})"),
        Rx(@"Gesamt\s*Brutto\s*:?\s*(-?\s*[\d.]+,\d{2})"),
    };
    private static readonly Regex OverviewAmountRx = Rx(@"Gesamtbestellwert:?\s*(-?\s*[\d.]+,\d{2})");

    private static Regex Rx(string p) => new(p, RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex MoneyRx = new(@"(?<![\d.,])(\d{1,3}(?:\.\d{3})*|\d+),(\d{2})(?!\d)", RegexOptions.Compiled);

    /// <summary>Alle Geldbeträge (Format 1.234,56) im Text, ohne Doppelte.</summary>
    public static IReadOnlyList<decimal> FindAmounts(string text)
    {
        var set = new List<decimal>();
        foreach (Match m in MoneyRx.Matches(text))
        {
            var s = m.Groups[1].Value.Replace(".", "") + "." + m.Groups[2].Value;
            if (decimal.TryParse(s, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var v) && !set.Contains(v))
                set.Add(v);
        }
        return set;
    }

    public static IReadOnlyList<string> FindOrderNumbers(string text) =>
        OrderRx.Matches(text).Select(m => m.Groups[1].Value).Distinct().ToList();

    /// <summary>Bestellnummern aus Text und Dateiname (Exporte heißen z. B. "20260909_Tax Invoice_303-4130002-8523510.pdf").</summary>
    public static IReadOnlyList<string> FindOrderNumbers(string text, string fileName) =>
        FindOrderNumbers(text).Concat(FindOrderNumbers(Path.GetFileNameWithoutExtension(fileName))).Distinct().ToList();

    /// <summary>Erkennt Art und Betrag eines Amazon-Belegs aus dem PDF-Text (und dem Dateinamen).</summary>
    public static (ReceiptKind Kind, decimal? Amount) Analyze(string text, string fileName)
    {
        var head = text.Length > 300 ? text[..300] : text;
        var stem = Path.GetFileNameWithoutExtension(fileName);
        // PdfPig liefert manchmal Text ohne Zeilenwechsel - dann den Anfang großzügiger prüfen.
        if (!head.Contains("Rechnung", StringComparison.OrdinalIgnoreCase) && !head.Contains("Bestellung", StringComparison.OrdinalIgnoreCase)
            && !head.Contains("Gutschrift", StringComparison.OrdinalIgnoreCase))
            head = text.Length > 1500 ? text[..1500] : text;

        ReceiptKind kind;
        if (head.Contains("Übersicht zur Bestellung", StringComparison.OrdinalIgnoreCase) || OrderRx.IsMatch(stem) && OrderRx.Match(stem).Value == stem)
            kind = ReceiptKind.Bestelluebersicht;
        else if (head.Contains("Rechnungskorrektur", StringComparison.OrdinalIgnoreCase) || head.Contains("Gutschrift", StringComparison.OrdinalIgnoreCase))
            kind = ReceiptKind.Gutschrift;
        else if (head.Contains("Rechnung", StringComparison.OrdinalIgnoreCase))
            kind = ReceiptKind.Rechnung;
        else
            kind = ReceiptKind.Unbekannt;

        decimal? amount = null;
        if (kind == ReceiptKind.Bestelluebersicht)
        {
            amount = ParseAmount(OverviewAmountRx.Match(text));
        }
        else
        {
            foreach (var rx in AmountRx)
            {
                amount = ParseAmount(rx.Match(text));
                if (amount is not null) break;
            }
            // Eine Gutschrift ist immer negativ, auch wenn das Vorzeichen im PDF anders gesetzt ist.
            if (kind == ReceiptKind.Gutschrift && amount > 0) amount = -amount;
        }
        return (kind, amount);
    }

    private static decimal? ParseAmount(Match m)
    {
        if (!m.Success) return null;
        var s = m.Groups[1].Value.Replace(" ", "").Replace(".", "").Replace(',', '.');
        return decimal.TryParse(s, System.Globalization.NumberStyles.Number | System.Globalization.NumberStyles.AllowLeadingSign,
            System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    /// <summary>
    /// Ordnet Amazon-Buchungen den Belegen zu: gleiche Bestellnummer UND gleicher Betrag auf den Cent.
    /// Eine Bestellung kann mehrere Buchungen und mehrere Rechnungen/Gutschriften haben; jeder Beleg wird
    /// höchstens einer Buchung zugeordnet. Die Bestellübersicht ist nur ein Betragsanker, kein Beleg.
    /// </summary>
    public static AmazonAssignResult Assign(IEnumerable<Booking> bookings, IReadOnlyList<ReceiptDocument> receipts)
    {
        // Dieselbe Datei in mehreren Wochenexporten nur einmal berücksichtigen.
        var docs = new List<ReceiptDocument>();
        var seen = new HashSet<string>();
        foreach (var r in receipts)
            if (seen.Add($"{r.Kind}|{r.DocumentId}|{r.Amount}|{string.Join(',', r.OrderNumbers)}")) docs.Add(r);

        var byOrder = new Dictionary<string, List<ReceiptDocument>>();
        foreach (var r in docs)
            foreach (var o in r.OrderNumbers)
            {
                if (!byOrder.TryGetValue(o, out var list)) byOrder[o] = list = new List<ReceiptDocument>();
                list.Add(r);
            }

        var amazon = bookings.Where(b => b.Category.IsAmazon()).ToList();
        var used = new HashSet<ReceiptDocument>();

        foreach (var b in amazon)
        {
            b.ReceiptFiles.Clear();
            b.ReceiptNote = "";
        }
        var open = amazon.Where(b => !b.Beleglos).ToList();

        foreach (var b in open.Where(b => b.AmazonOrder.Length == 0))
            b.ReceiptNote = "Keine Amazon-Bestellnummer im Verwendungszweck";
        open = open.Where(b => b.AmazonOrder.Length > 0).ToList();

        bool IsReal(ReceiptDocument d) => d.Kind is ReceiptKind.Rechnung or ReceiptKind.Gutschrift or ReceiptKind.Unbekannt;
        List<ReceiptDocument> Real(string order) =>
            byOrder.TryGetValue(order, out var l) ? l.Where(IsReal).ToList() : new List<ReceiptDocument>();

        var done = new HashSet<Booking>();

        // 1. Beleg mit genau passendem Betrag.
        foreach (var b in open)
        {
            var hit = Real(b.AmazonOrder).FirstOrDefault(d => !used.Contains(d) && d.BookingAmount == b.Amount);
            if (hit is null) continue;
            used.Add(hit);
            b.ReceiptFiles.Add(hit.FilePath);
            done.Add(b);
        }

        // 2. Mehrere Rechnungen dieser Bestellung wurden mit einer Buchung abgebucht (Summe stimmt).
        foreach (var b in open.Where(b => !done.Contains(b)))
        {
            var free = Real(b.AmazonOrder).Where(d => !used.Contains(d) && d.Amount is not null).ToList();
            if (free.Count < 2) continue;
            if (free.Sum(d => d.BookingAmount!.Value) != b.Amount) continue;
            foreach (var d in free) { used.Add(d); b.ReceiptFiles.Add(d.FilePath); }
            b.ReceiptNote = $"Sammelbuchung über {free.Count} Belege";
            done.Add(b);
        }

        // 3. Beleg ohne lesbaren Betrag (z. B. Marktplatz-Verkäufer): nur wenn eindeutig und die Bestellübersicht denselben Betrag zeigt.
        foreach (var b in open.Where(b => !done.Contains(b)))
        {
            var free = Real(b.AmazonOrder).Where(d => !used.Contains(d)).ToList();
            var unreadable = free.Where(d => d.Amount is null).ToList();
            if (unreadable.Count != 1 || free.Count != 1) continue;
            var overview = byOrder[b.AmazonOrder].FirstOrDefault(d => d.Kind == ReceiptKind.Bestelluebersicht);
            if (overview?.BookingAmount != b.Amount) continue;
            used.Add(unreadable[0]);
            b.ReceiptFiles.Add(unreadable[0].FilePath);
            b.ReceiptNote = "Betrag der Rechnung nicht lesbar - passt zur Bestellübersicht, bitte prüfen";
            done.Add(b);
        }

        // 3b. Betrag nicht als "Zahlbetrag" erkannt, steht aber im Beleg: Bestellnummer + Betrag im Text genügen (Hinweis zur Prüfung).
        foreach (var b in open.Where(b => !done.Contains(b)))
        {
            var cand = Real(b.AmazonOrder).Where(d => !used.Contains(d) && d.Amount is null
                && d.AllAmounts is not null && d.AllAmounts.Contains(Math.Abs(b.Amount))).ToList();
            if (cand.Count != 1) continue;
            used.Add(cand[0]);
            b.ReceiptFiles.Add(cand[0].FilePath);
            b.ReceiptNote = "Betrag nur im Belegtext gefunden (nicht als Zahlbetrag erkannt) - bitte prüfen";
            done.Add(b);
        }

        // 4. Nicht zugeordnete Buchungen: sagen, woran es liegt.
        foreach (var b in open.Where(b => !done.Contains(b)))
        {
            var all = byOrder.TryGetValue(b.AmazonOrder, out var l) ? l : new List<ReceiptDocument>();
            var real = all.Where(IsReal).ToList();
            if (all.Count == 0)
            {
                b.ReceiptNote = b.Category == BookingCategory.AmazonErstattung
                    ? $"Gutschrift zu Bestellung {b.AmazonOrder} fehlt"
                    : $"Beleg zu Bestellung {b.AmazonOrder} fehlt";
            }
            else if (real.Count == 0)
            {
                b.ReceiptNote = $"Zu Bestellung {b.AmazonOrder} liegt nur die Bestellübersicht vor (keine Rechnung)";
            }
            else
            {
                var amounts = string.Join(", ", real.Select(d => d.Amount is { } a ? a.ToString("N2", System.Globalization.CultureInfo.GetCultureInfo("de-DE")) : "?"));
                b.ReceiptNote = $"Kein Beleg zu Bestellung {b.AmazonOrder} über {Math.Abs(b.Amount).ToString("N2", System.Globalization.CultureInfo.GetCultureInfo("de-DE"))} (vorhanden: {amounts})";
            }
        }

        var result = new AmazonAssignResult();
        result.UnusedDocuments.AddRange(docs.Where(d => IsReal(d) && !used.Contains(d)));
        return result;
    }
}
