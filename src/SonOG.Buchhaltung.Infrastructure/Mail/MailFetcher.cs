using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MailKit.Security;
using MimeKit;

namespace SonOG.Buchhaltung.Infrastructure.Mail;

public sealed class MailFetchResult
{
    public int Downloaded { get; set; }
    public int AlreadyThere { get; set; }
    public int MessagesChecked { get; set; }
    public List<string> Errors { get; } = new();
}

/// <summary>
/// Holt PDF-Anhänge aus IMAP-Postfächern (web.de, Gmail, Roundcube-Server ...). Es wird nur gelesen (Ordner schreibgeschützt),
/// nichts gelöscht oder verschoben. Bereits geladene Anhänge werden nicht erneut geschrieben.
/// Gmail verlangt IMAP + ein App-Passwort (Konto mit 2-Faktor), web.de verlangt "IMAP-Zugriff erlauben" in den Einstellungen.
/// </summary>
public static class MailFetcher
{
    public static MailFetchResult Fetch(MailAccount account, DateOnly from, DateOnly to, string targetRoot,
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var result = new MailFetchResult();
        var dir = Path.Combine(targetRoot, SafeName(account.Name.Length > 0 ? account.Name : account.Host));
        Directory.CreateDirectory(dir);

        try
        {
            using var client = new ImapClient();
            var ssl = account.Port == 143 ? SecureSocketOptions.StartTls : SecureSocketOptions.SslOnConnect;
            client.Connect(account.Host, account.Port, ssl, ct);
            client.Authenticate(account.User, account.GetPassword(), ct);

            foreach (var folderName in account.Folders.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                IMailFolder folder;
                try { folder = folderName.Equals("INBOX", StringComparison.OrdinalIgnoreCase) ? client.Inbox : client.GetFolder(folderName); }
                catch (Exception ex) { result.Errors.Add($"{account.Name}: Ordner \"{folderName}\": {ex.Message}"); continue; }

                folder.Open(FolderAccess.ReadOnly, ct);
                var query = SearchQuery.DeliveredAfter(from.ToDateTime(TimeOnly.MinValue))
                    .And(SearchQuery.DeliveredBefore(to.AddDays(1).ToDateTime(TimeOnly.MinValue)))
                    .And(SearchQuery.NotDeleted);
                var uids = folder.Search(query, ct);
                int n = 0;
                foreach (var uid in uids)
                {
                    ct.ThrowIfCancellationRequested();
                    if (++n % 20 == 0) progress?.Report($"{account.Name} / {folderName}: {n} / {uids.Count} Mails");
                    result.MessagesChecked++;

                    MimeMessage msg;
                    try { msg = folder.GetMessage(uid, ct); }
                    catch (Exception ex) { result.Errors.Add($"{account.Name}: Mail {uid}: {ex.Message}"); continue; }

                    int k = 0;
                    foreach (var part in msg.BodyParts.OfType<MimePart>())
                    {
                        var fileName = part.FileName ?? part.ContentType.Name ?? "";
                        bool isPdf = part.ContentType.MimeType.Equals("application/pdf", StringComparison.OrdinalIgnoreCase)
                                     || fileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase);
                        if (!isPdf) continue;

                        k++;
                        var date = msg.Date.LocalDateTime.ToString("yyyyMMdd");
                        var sender = msg.From.Mailboxes.FirstOrDefault()?.Address ?? "";
                        var name = SafeName($"{date}_{SafeName(folderName)}{uid.Id}_{k}_{sender}_{(fileName.Length > 0 ? fileName : "Anhang.pdf")}");
                        if (!name.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) name += ".pdf";
                        if (name.Length > 150) name = name[..120] + ".pdf";
                        var path = Path.Combine(dir, name);
                        if (File.Exists(path)) { result.AlreadyThere++; continue; }

                        using (var fs = File.Create(path)) part.Content.DecodeTo(fs, ct);
                        File.SetLastWriteTime(path, msg.Date.LocalDateTime);
                        result.Downloaded++;
                    }
                }
                folder.Close(false, ct);
            }
            client.Disconnect(true, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            result.Errors.Add($"{account.Name}: {ex.Message}");
        }
        return result;
    }

    public static string MailFolder(string dataDir) => Path.Combine(dataDir, "mail");

    private static string SafeName(string s)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        return s.Replace(' ', '_');
    }
}
