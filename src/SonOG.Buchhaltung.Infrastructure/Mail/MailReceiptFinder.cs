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
/// Gemeinsame IMAP-Verbindung (Fallback 993/143, kurzer Timeout) und Verbindungstest. Die eigentliche Arbeit macht MailDownloader.
/// </summary>
public static class MailReceiptFinder
{
    /// <summary>Zeitfenster der Mail-Suche in Tagen vor/nach der Buchung (in der Oberfläche einstellbar).</summary>
    public static int DaysBefore { get; set; } = 60;
    public static int DaysAfter { get; set; } = 30;

    public static string MailFolder(string dataDir) => Path.Combine(dataDir, "mail");

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
}
