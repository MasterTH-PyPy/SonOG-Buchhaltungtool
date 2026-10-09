using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MailKit.Security;
using MimeKit;
using SonOG.Buchhaltung.Core.Matching;
using SonOG.Buchhaltung.Core.Models;
using SonOG.Buchhaltung.Infrastructure.Pdf;

namespace SonOG.Buchhaltung.Infrastructure.Mail;

/// <summary>
/// Sucht zu Buchungen ohne Beleg in den Postfächern nach einer Mail des Shops/Verkäufers (Absender oder Betreff enthält den Namen,
/// bei Amazon auch die Bestellnummer im Text) in einem kleinen Zeitfenster vor der Buchung, lädt deren PDF-Anhänge und
/// gleicht sie mit Rechnungsnummer (aus dem Buchungstext) und/oder Betrag ab. Es wird nur gelesen.
/// Jede Zuordnung bekommt den Hinweis "bitte prüfen"; im Druckdialog kann sie abgewählt werden.
/// </summary>
public static class MailReceiptFinder
{
    public const int DaysBefore = 60;
    public const int DaysAfter = 3;

    private sealed record Candidate(DateTime Date, string Sender, List<MimePart> Pdfs, bool HasOtherAttachment);

    public static string MailFolder(string dataDir) => Path.Combine(dataDir, "mail");

    public static List<string> Run(IEnumerable<MailAccount> accounts, IReadOnlyList<Booking> bookings, string targetRoot,
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var problems = new List<string>();
        var todo = bookings.Where(b => !b.Beleglos && b.ReceiptFiles.Count == 0
                                       && (b.Category.IsAmazon() || b.Category == BookingCategory.AusgabeSonstige)).ToList();
        if (todo.Count == 0) return problems;

        foreach (var acc in accounts.Where(a => a.Enabled && a.Host.Length > 0))
        {
            try
            {
                using var client = new ImapClient();
                client.Connect(acc.Host, acc.Port, acc.Port == 143 ? SecureSocketOptions.StartTls : SecureSocketOptions.SslOnConnect, ct);
                client.Authenticate(acc.User, acc.GetPassword(), ct);
                var dir = Path.Combine(targetRoot, Safe(acc.Name.Length > 0 ? acc.Name : acc.Host));
                Directory.CreateDirectory(dir);

                foreach (var folderName in acc.Folders.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    IMailFolder folder;
                    try { folder = folderName.Equals("INBOX", StringComparison.OrdinalIgnoreCase) ? client.Inbox : client.GetFolder(folderName); }
                    catch (Exception ex) { problems.Add($"{acc.Name}: Ordner \"{folderName}\": {ex.Message}"); continue; }
                    folder.Open(FolderAccess.ReadOnly, ct);

                    int i = 0;
                    foreach (var b in todo.Where(b => b.ReceiptFiles.Count == 0))
                    {
                        ct.ThrowIfCancellationRequested();
                        progress?.Report($"Mails durchsuchen ({acc.Name}): {b.Number} {++i}");
                        try { FindFor(b, folder, dir, ct); }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex) { problems.Add($"{acc.Name}: {b.Number}: {ex.Message}"); }
                    }
                    folder.Close(false, ct);
                }
                client.Disconnect(true, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                problems.Add($"{acc.Name}: {ex.Message}");
            }
        }
        return problems;
    }

    private static void FindFor(Booking b, IMailFolder folder, string dir, CancellationToken ct)
    {
        var keywords = MerchantKeywords.Extract(b.Text).ToList();
        if (b.Category.IsAmazon() && !keywords.Contains("amazon", StringComparer.OrdinalIgnoreCase)) keywords.Insert(0, "amazon");
        if (keywords.Count == 0 && b.AmazonOrder.Length == 0) return;

        var date = b.Date.ToDateTime(TimeOnly.MinValue);
        var window = SearchQuery.DeliveredAfter(date.AddDays(-DaysBefore)).And(SearchQuery.DeliveredBefore(date.AddDays(DaysAfter + 1)));

        SearchQuery? who = null;
        foreach (var k in keywords.Take(2))
        {
            var q = SearchQuery.FromContains(k).Or(SearchQuery.SubjectContains(k));
            who = who is null ? q : who.Or(q);
        }
        if (b.AmazonOrder.Length > 0)
        {
            var o = SearchQuery.BodyContains(b.AmazonOrder).Or(SearchQuery.SubjectContains(b.AmazonOrder));
            who = who is null ? o : who.Or(o);
        }
        if (who is null) return;

        var uids = folder.Search(window.And(who), ct);
        if (uids.Count == 0) return;

        var candidates = new List<Candidate>();
        foreach (var uid in uids.Reverse().Take(12))
        {
            var msg = folder.GetMessage(uid, ct);
            var pdfs = msg.BodyParts.OfType<MimePart>().Where(IsPdf).ToList();
            var others = msg.Attachments.OfType<MimePart>().Any(p => !IsPdf(p));
            candidates.Add(new Candidate(msg.Date.LocalDateTime, msg.From.Mailboxes.FirstOrDefault()?.Address ?? "", pdfs, others));
        }

        var withPdf = candidates.Where(c => c.Pdfs.Count > 0).ToList();
        if (withPdf.Count == 0)
        {
            var c = candidates.OrderBy(x => Math.Abs((x.Date - date).TotalDays)).First();
            b.ReceiptNote = $"Mail von {c.Sender} am {c.Date:dd.MM.yyyy} gefunden, aber " +
                            (c.HasOtherAttachment ? "ohne PDF-Anhang (anderer Anhang)" : "ohne Anhang (Rechnung evtl. als Link)");
            return;
        }

        // PDFs der in Frage kommenden Mails speichern und mit Rechnungsnummer / Betrag abgleichen.
        var amount = Math.Abs(b.Amount);
        var refs = MerchantKeywords.ReferenceNumbers(b.Text).ToList();
        if (b.AmazonOrder.Length > 0) refs.Add(b.AmazonOrder);

        var scored = new List<(Candidate Mail, List<string> Files, bool RefHit, bool AmountHit)>();
        foreach (var c in withPdf.OrderBy(x => Math.Abs((x.Date - date).TotalDays)).Take(4))
        {
            var files = new List<string>();
            int k = 0;
            foreach (var part in c.Pdfs)
            {
                var name = Safe($"{c.Date:yyyyMMdd}_{Safe(c.Sender)}_{++k}_{part.FileName ?? "Anhang.pdf"}");
                if (!name.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) name += ".pdf";
                if (name.Length > 150) name = name[..120] + ".pdf";
                var path = Path.Combine(dir, name);
                if (!File.Exists(path))
                {
                    using var fs = File.Create(path);
                    part.Content.DecodeTo(fs, ct);
                }
                files.Add(path);
            }
            bool refHit = false, amountHit = false;
            foreach (var f in files)
            {
                try
                {
                    var text = PdfWordReader.ReadAllText(f);
                    var flat = text.Replace(" ", "");
                    refHit |= refs.Any(r => flat.Contains(r.Replace(" ", ""), StringComparison.OrdinalIgnoreCase));
                    amountHit |= AmazonMatcher.FindAmounts(text).Contains(amount);
                }
                catch (Exception) { /* nicht lesbar: dann zählt nur das Datum */ }
            }
            scored.Add((c, files, refHit, amountHit));
        }

        var best = scored.OrderByDescending(s => (s.RefHit ? 2 : 0) + (s.AmountHit ? 1 : 0)).First();
        b.ReceiptFiles.AddRange(best.Files);
        var what = best.RefHit && best.AmountHit ? "Rechnungsnummer und Betrag im PDF gefunden"
                 : best.RefHit ? "Rechnungsnummer im PDF gefunden, Betrag nicht"
                 : best.AmountHit ? "Betrag im PDF gefunden"
                 : "weder Rechnungsnummer noch Betrag im PDF gefunden";
        b.ReceiptNote = $"Beleg aus Mail von {best.Mail.Sender} ({best.Mail.Date:dd.MM.yyyy}): {what} - bitte prüfen"
                        + (withPdf.Count > 1 ? $" ({withPdf.Count} Mails mit Anhang)" : "");
    }

    private static bool IsPdf(MimePart p) =>
        p.ContentType.MimeType.Equals("application/pdf", StringComparison.OrdinalIgnoreCase)
        || (p.FileName ?? p.ContentType.Name ?? "").EndsWith(".pdf", StringComparison.OrdinalIgnoreCase);

    private static string Safe(string s)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        return s.Replace(' ', '_');
    }
}
