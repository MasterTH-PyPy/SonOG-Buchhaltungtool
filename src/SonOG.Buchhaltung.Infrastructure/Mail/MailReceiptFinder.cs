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
        IProgress<string>? progress = null, CancellationToken ct = default, List<string>? log = null)
    {
        var problems = new List<string>();
        var todo = bookings.Where(b => !b.Beleglos && b.ReceiptFiles.Count == 0
                                       && (b.Category.IsAmazon() || b.Category == BookingCategory.AusgabeSonstige)).ToList();
        if (todo.Count == 0) return problems;

        foreach (var acc in accounts.Where(a => a.Enabled && a.Host.Length > 0))
        {
            try
            {
                using var client = Open(acc, ct, out var how);
                log?.Add($"Verbindung {acc.Name}: {how}");
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
                        try { FindFor(b, folder, dir, ct, log, $"{acc.Name}/{folderName}"); }
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

    /// <summary>Verbindet mit kurzem Timeout; schlägt der eingestellte Port fehl, wird der andere übliche IMAP-Port versucht (993 SSL / 143 STARTTLS).</summary>
    internal static ImapClient Open(MailAccount acc, CancellationToken ct, out string how)
    {
        var attempts = new List<(int Port, SecureSocketOptions Mode)>
        {
            acc.Port == 143 ? (143, SecureSocketOptions.StartTls) : (acc.Port, SecureSocketOptions.SslOnConnect),
            acc.Port == 143 ? (993, SecureSocketOptions.SslOnConnect) : (143, SecureSocketOptions.StartTls),
        };
        var errors = new List<string>();
        foreach (var (port, mode) in attempts)
        {
            var client = new ImapClient { Timeout = 20000 };
            try
            {
                client.Connect(acc.Host, port, mode, ct);
                client.Authenticate(acc.User, acc.GetPassword(), ct);
                how = $"Port {port} ({mode})";
                return client;
            }
            catch (MailKit.Security.AuthenticationException)
            {
                client.Dispose();
                throw new InvalidOperationException("Anmeldung abgelehnt - Benutzername/Passwort prüfen, bei web.de IMAP-Zugriff erlauben und ggf. ein anwendungsspezifisches Passwort verwenden.");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                client.Dispose();
                errors.Add($"Port {port}: {ex.Message}");
            }
        }
        throw new InvalidOperationException("Keine Verbindung zu " + acc.Host + " möglich (" + string.Join(" | ", errors) +
                                            "). Wahrscheinlich blockiert Firewall/Proxy/Virenscanner die IMAP-Ports 993 und 143.");
    }

    /// <summary>Für den Button "Verbindung testen". Gibt eine lesbare Meldung zurück.</summary>
    public static string TestConnection(MailAccount acc)
    {
        try
        {
            using var client = Open(acc, CancellationToken.None, out var how);
            var inbox = client.Inbox;
            inbox.Open(FolderAccess.ReadOnly);
            var n = inbox.Count;
            client.Disconnect(true);
            return $"Verbindung ok ({how}). Posteingang: {n} Mails.";
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    private static void FindFor(Booking b, IMailFolder folder, string dir, CancellationToken ct, List<string>? log, string where)
    {
        var keywords = MerchantKeywords.Extract(b.Text).ToList();
        if (b.Category.IsAmazon() && !keywords.Contains("amazon", StringComparer.OrdinalIgnoreCase)) keywords.Insert(0, "amazon");
        log?.Add($"{b.Number} [{where}] Suchwörter: {(keywords.Count > 0 ? string.Join(", ", keywords.Take(2)) : "-")}{(b.AmazonOrder.Length > 0 ? " + Bestellnr. " + b.AmazonOrder : "")} | Buchungstext: {b.Text.Replace('\n', ' ')}");
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
        log?.Add($"    Mails im Zeitfenster {date.AddDays(-DaysBefore):dd.MM.yyyy}-{date.AddDays(DaysAfter):dd.MM.yyyy}: {uids.Count}");
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
        foreach (var c in candidates) log?.Add($"    Mail {c.Date:dd.MM.yyyy} von {c.Sender}: {c.Pdfs.Count} PDF-Anhang/Anhänge");
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

        // Der Betrag muss im PDF stehen (der Absender passt schon über die Suche). Sonst wird nicht zugeordnet.
        var good = scored.Where(x => x.AmountHit).OrderByDescending(x => x.RefHit ? 1 : 0).ToList();
        if (good.Count == 0)
        {
            var any = scored[0];
            b.ReceiptNote = $"Mail von {any.Mail.Sender} ({any.Mail.Date:dd.MM.yyyy}) mit PDF gefunden, aber der Betrag {amount:N2} steht nicht im PDF" +
                            (any.RefHit ? " (Rechnungsnummer passt)" : "") + " - nicht zugeordnet: " + Path.GetFileName(any.Files[0]);
            return;
        }
        var best = good[0];
        b.ReceiptFiles.AddRange(best.Files);
        b.ReceiptNote = $"Beleg aus Mail von {best.Mail.Sender} ({best.Mail.Date:dd.MM.yyyy}): Betrag " +
                        (best.RefHit ? "und Rechnungsnummer" : "") + " im PDF gefunden - bitte prüfen"
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
