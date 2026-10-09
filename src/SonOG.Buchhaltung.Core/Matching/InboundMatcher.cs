using System.Globalization;
using SonOG.Buchhaltung.Core.Models;

namespace SonOG.Buchhaltung.Core.Matching;

/// <summary>
/// Ordnet sonstigen Ausgaben (nicht Amazon, nicht beleglos) Eingangsrechnungen aus dem Eingangsordner zu.
/// Zuordnung nur bei Rechnungsnummer aus dem Buchungstext im PDF, oder bei Betrag UND Shop-/Verkäufername im PDF.
/// Nur Betrag ohne Namen wird als Hinweis angezeigt, nicht zugeordnet. Alles mit "bitte prüfen".
/// </summary>
public static class InboundMatcher
{
    public static void Assign(IEnumerable<Booking> bookings, IReadOnlyList<ReceiptDocument> receipts)
    {
        var all = bookings.ToList();
        var taken = new HashSet<string>(all.SelectMany(b => b.ReceiptFiles), StringComparer.OrdinalIgnoreCase);
        var docs = receipts.Where(r => r.Kind != ReceiptKind.Bestelluebersicht && !r.IsScan && r.SearchText.Length > 0).ToList();
        var de = CultureInfo.GetCultureInfo("de-DE");

        foreach (var b in all.Where(b => !b.Beleglos && b.Category == BookingCategory.AusgabeSonstige && b.ReceiptFiles.Count == 0))
        {
            var amount = Math.Abs(b.Amount);
            var keywords = MerchantKeywords.Extract(b.Text).Select(k => k.ToLowerInvariant()).ToList();
            var refs = MerchantKeywords.ReferenceNumbers(b.Text).Select(r => r.Replace(" ", "").ToLowerInvariant()).ToList();

            var scored = docs.Where(d => !taken.Contains(d.FilePath)).Select(d =>
            {
                var flat = d.SearchText.Replace(" ", "");
                bool refHit = refs.Any(r => flat.Contains(r, StringComparison.Ordinal));
                bool amountHit = d.AllAmounts?.Contains(amount) == true;
                bool nameHit = keywords.Any(k => d.SearchText.Contains(k, StringComparison.Ordinal) || d.FilePath.Contains(k, StringComparison.OrdinalIgnoreCase));
                return (Doc: d, RefHit: refHit, AmountHit: amountHit, NameHit: nameHit);
            }).ToList();

            var good = scored.Where(s => s.RefHit || (s.AmountHit && s.NameHit)).ToList();
            if (good.Count > 0)
            {
                var best = good.OrderByDescending(s => (s.RefHit ? 4 : 0) + (s.AmountHit ? 2 : 0) + (s.NameHit ? 1 : 0)).First();
                taken.Add(best.Doc.FilePath);
                b.ReceiptFiles.Add(best.Doc.FilePath);
                b.ReceiptNote = "Eingangsrechnung " + Path.GetFileName(best.Doc.FilePath) + ": " +
                    (best.RefHit && best.AmountHit ? "Rechnungsnummer und Betrag gefunden"
                     : best.RefHit ? "Rechnungsnummer gefunden, Betrag nicht"
                     : "Betrag und Name gefunden") + " - bitte prüfen" +
                    (good.Count > 1 ? $" ({good.Count} mögliche Belege)" : "");
                continue;
            }

            var amountOnly = scored.Where(s => s.AmountHit).ToList();
            if (amountOnly.Count > 0)
                b.ReceiptNote = $"Nur der Betrag {amount.ToString("N2", de)} passt zu " +
                                string.Join(", ", amountOnly.Take(3).Select(s => Path.GetFileName(s.Doc.FilePath))) + " (Name fehlt) - von Hand zuordnen?";
        }
    }
}
