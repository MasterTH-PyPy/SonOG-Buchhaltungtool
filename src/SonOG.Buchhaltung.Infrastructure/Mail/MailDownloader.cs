using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;

namespace SonOG.Buchhaltung.Infrastructure.Mail;

/// <summary>
/// Lädt alle PDF-Anhänge der Mails in einem Zeitraum aus den Postfächern (nur lesend, nur die Anhänge, nicht die ganzen Mails).
/// Die geladenen PDFs werden danach wie der Eingangsordner nach Inhalt, Betrag und Rechnungsnummer abgeglichen.
/// Dateiname: Datum_Absender_Anhangsname.pdf, bereits vorhandene Dateien werden nicht erneut geladen.
/// </summary>
public static class MailDownloader
{
    public static (int Downloaded, int AlreadyThere, int MailsChecked) Run(IEnumerable<MailAccount> accounts, DateOnly from, DateOnly to,
        string targetRoot, List<string> problems, List<string>? log, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        int downloaded = 0, there = 0, checkedMails = 0;
        foreach (var acc in accounts.Where(a => a.Enabled && a.Host.Length > 0))
        {
            try
            {
                using var client = MailReceiptFinder.Open(acc, ct, out var how);
                log?.Add($"Mail-Download {acc.Name}: {how}, Zeitraum {from:dd.MM.yyyy}-{to:dd.MM.yyyy}");
                var dir = Path.Combine(targetRoot, Safe(acc.Name.Length > 0 ? acc.Name : acc.Host));
                Directory.CreateDirectory(dir);

                foreach (var folderName in acc.Folders.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    IMailFolder folder;
                    try { folder = folderName.Equals("INBOX", StringComparison.OrdinalIgnoreCase) ? client.Inbox : client.GetFolder(folderName); }
                    catch (Exception ex) { problems.Add($"{acc.Name}: Ordner \"{folderName}\": {ex.Message}"); continue; }
                    folder.Open(FolderAccess.ReadOnly, ct);

                    var query = SearchQuery.DeliveredAfter(from.ToDateTime(TimeOnly.MinValue))
                        .And(SearchQuery.DeliveredBefore(to.AddDays(1).ToDateTime(TimeOnly.MinValue)));
                    var uids = folder.Search(query, ct);
                    log?.Add($"  {folderName}: {uids.Count} Mails im Zeitraum");
                    if (uids.Count == 0) { folder.Close(false, ct); continue; }

                    // Nur Kopfdaten und Aufbau der Mails holen; heruntergeladen werden ausschließlich PDF-Anhänge.
                    var summaries = folder.Fetch(uids, MessageSummaryItems.UniqueId | MessageSummaryItems.Envelope
                                                       | MessageSummaryItems.BodyStructure | MessageSummaryItems.InternalDate, ct);
                    int n = 0;
                    foreach (var sum in summaries)
                    {
                        ct.ThrowIfCancellationRequested();
                        checkedMails++;
                        if (++n % 25 == 0) progress?.Report($"Mail-Anhänge laden ({acc.Name}): {n} / {summaries.Count}");

                        var pdfs = (sum.BodyParts ?? Enumerable.Empty<BodyPartBasic>())
                            .Where(p => p.ContentType.MimeType.Equals("application/pdf", StringComparison.OrdinalIgnoreCase)
                                        || (p.FileName ?? p.ContentType.Name ?? "").EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
                            .ToList();
                        if (pdfs.Count == 0) continue;

                        var date = (sum.InternalDate ?? sum.Date).LocalDateTime;
                        var sender = sum.Envelope?.From?.Mailboxes.FirstOrDefault()?.Address ?? "unbekannt";
                        int k = 0;
                        foreach (var part in pdfs)
                        {
                            k++;
                            var name = Safe($"{date:yyyyMMdd}_{sender}_{(pdfs.Count > 1 ? k + "_" : "")}{part.FileName ?? part.ContentType.Name ?? "Anhang.pdf"}");
                            if (!name.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) name += ".pdf";
                            if (name.Length > 150) name = name[..120] + ".pdf";
                            var path = Path.Combine(dir, name);
                            if (File.Exists(path)) { there++; continue; }
                            try
                            {
                                if (folder.GetBodyPart(sum.UniqueId, part, ct) is MimeKit.MimePart entity)
                                {
                                    using (var fs = File.Create(path)) entity.Content.DecodeTo(fs, ct);
                                    File.SetLastWriteTime(path, date);
                                    downloaded++;
                                }
                            }
                            catch (Exception ex) when (ex is not OperationCanceledException)
                            {
                                problems.Add($"{acc.Name}: Anhang {name}: {ex.Message}");
                                try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { }
                            }
                        }
                    }
                    folder.Close(false, ct);
                }
                client.Disconnect(true, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { problems.Add($"{acc.Name}: {ex.Message}"); }
        }
        return (downloaded, there, checkedMails);
    }

    private static string Safe(string s)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        return s.Replace(' ', '_');
    }
}
