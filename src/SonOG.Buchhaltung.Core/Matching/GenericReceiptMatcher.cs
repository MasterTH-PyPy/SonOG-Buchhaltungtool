using System.Globalization;
using SonOG.Buchhaltung.Core.Models;

namespace SonOG.Buchhaltung.Core.Matching;

/// <summary>
/// Ordnet sonstigen Ausgaben (nicht Amazon, nicht beleglos) Belege aus Mail-Anhängen/Ordnern über den Betrag zu.
/// Es wird nur zugeordnet, wenn genau ein noch freier Beleg den Betrag enthält; sonst bleibt ein Hinweis.
/// Jede Zuordnung ist ein Vorschlag und wird mit "bitte prüfen" markiert.
/// </summary>
public static class GenericReceiptMatcher
{
    public static void Assign(IEnumerable<Booking> bookings, IReadOnlyList<ReceiptDocument> receipts)
    {
        var all = bookings.ToList();
        var taken = new HashSet<string>(all.SelectMany(b => b.ReceiptFiles), StringComparer.OrdinalIgnoreCase);
        var free = receipts
            .Where(r => r.Kind != ReceiptKind.Bestelluebersicht && r.AllAmounts is { Count: > 0 } && !taken.Contains(r.FilePath))
            .GroupBy(r => r.DocumentId + "|" + string.Join(',', r.AllAmounts!.OrderBy(a => a)))
            .Select(g => g.First())
            .ToList();
        var de = CultureInfo.GetCultureInfo("de-DE");

        foreach (var b in all.Where(b => !b.Beleglos && b.Category == BookingCategory.AusgabeSonstige && b.ReceiptFiles.Count == 0))
        {
            var amount = Math.Abs(b.Amount);
            var hits = free.Where(r => !taken.Contains(r.FilePath) && r.AllAmounts!.Contains(amount)).ToList();
            if (hits.Count == 1)
            {
                taken.Add(hits[0].FilePath);
                b.ReceiptFiles.Add(hits[0].FilePath);
                b.ReceiptNote = "Zuordnung über den Betrag (Mail/Ordner) - bitte prüfen";
            }
            else if (hits.Count > 1)
            {
                b.ReceiptNote = $"{hits.Count} mögliche Belege über {amount.ToString("N2", de)}: " +
                                string.Join(", ", hits.Take(4).Select(h => h.DocumentId)) + " - bitte von Hand zuordnen";
            }
            else
            {
                b.ReceiptNote = "Kein Beleg über " + amount.ToString("N2", de) + " gefunden";
            }
        }
    }
}
