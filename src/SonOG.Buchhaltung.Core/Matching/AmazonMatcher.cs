using System.Text.RegularExpressions;
using SonOG.Buchhaltung.Core.Models;

namespace SonOG.Buchhaltung.Core.Matching;

/// <summary>Ein Amazon-Beleg (Rechnung/Gutschrift als PDF) mit den darin gefundenen Bestellnummern.</summary>
public sealed record ReceiptDocument(string FilePath, IReadOnlyList<string> OrderNumbers);

public static class AmazonMatcher
{
    private static readonly Regex OrderRx = new(@"\b(\d{3}-\d{7}-\d{7})\b", RegexOptions.Compiled);

    public static IReadOnlyList<string> FindOrderNumbers(string text) =>
        OrderRx.Matches(text).Select(m => m.Groups[1].Value).Distinct().ToList();

    /// <summary>Ordnet Amazon-Buchungen über die Bestellnummer den Beleg-PDFs zu.</summary>
    public static void Assign(IEnumerable<Booking> bookings, IReadOnlyList<ReceiptDocument> receipts)
    {
        var byOrder = new Dictionary<string, List<string>>();
        foreach (var r in receipts)
            foreach (var o in r.OrderNumbers)
            {
                if (!byOrder.TryGetValue(o, out var list)) byOrder[o] = list = new List<string>();
                if (!list.Contains(r.FilePath)) list.Add(r.FilePath);
            }

        foreach (var b in bookings.Where(b => b.Category.IsAmazon()))
        {
            b.ReceiptFiles.Clear();
            b.ReceiptNote = "";
            if (b.Beleglos) continue;

            if (b.AmazonOrder.Length == 0)
            {
                b.ReceiptNote = "Keine Amazon-Bestellnummer im Verwendungszweck";
            }
            else if (byOrder.TryGetValue(b.AmazonOrder, out var files))
            {
                b.ReceiptFiles.AddRange(files);
            }
            else
            {
                b.ReceiptNote = $"Beleg zu Bestellung {b.AmazonOrder} fehlt";
            }
        }
    }
}
