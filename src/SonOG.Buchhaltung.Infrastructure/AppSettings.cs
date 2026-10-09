using System.Text.Json;

namespace SonOG.Buchhaltung.Infrastructure;

/// <summary>Zuletzt verwendete Pfade. Es sind keine Zugangsdaten enthalten, deshalb als normale JSON-Datei.</summary>
public sealed class AppSettings
{
    public string StatementPath { get; set; } = "";
    public string InvoiceFolder { get; set; } = "";
    /// <summary>Eingangsrechnungen (Amazon-Wochenexporte, Rechnungen von Lieferanten, Scans).</summary>
    public string EingangFolder { get; set; } = "";

    /// <summary>Alter Name der Einstellung, wird beim Laden in EingangFolder übernommen.</summary>
    public string AmazonFolder { get; set; } = "";
    public string OutputFolder { get; set; } = "";

    /// <summary>Offene Rechnungen erst ab diesem Rechnungsdatum anzeigen (leer = alle).</summary>
    public string OpenFrom { get; set; } = "";

    /// <summary>Mail-Postfächer (IMAP), aus denen PDF-Anhänge als Belege geholt werden.</summary>
    public List<MailAccount> MailAccounts { get; set; } = new();

    public bool FetchMail { get; set; }

    /// <summary>Mail-Zeitraum: Tage vor der ersten und nach der letzten Buchung.</summary>
    public int MailDaysBefore { get; set; } = 60;
    public int MailDaysAfter { get; set; } = 30;

    public static string DataDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SonOG-Buchhaltung");

    public static string FilePath => Path.Combine(DataDir, "einstellungen.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                {
                var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
                if (loaded.EingangFolder.Length == 0 && loaded.AmazonFolder.Length > 0) loaded.EingangFolder = loaded.AmazonFolder;
                loaded.AmazonFolder = "";
                return loaded;
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException) { }
        return new AppSettings();
    }

    public void Save()
    {
        Directory.CreateDirectory(DataDir);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}

/// <summary>IMAP-Zugang. Das Passwort wird mit Windows-DPAPI (nur dieser Benutzer, dieser Rechner) verschlüsselt gespeichert.</summary>
public sealed class MailAccount
{
    public string Name { get; set; } = "";
    public string Host { get; set; } = "";
    public int Port { get; set; } = 993;
    public string User { get; set; } = "";
    public string PasswordProtected { get; set; } = "";
    /// <summary>Zu durchsuchende IMAP-Ordner, getrennt durch Semikolon.</summary>
    public string Folders { get; set; } = "INBOX";
    public bool Enabled { get; set; } = true;

    public void SetPassword(string password)
    {
        var bytes = System.Security.Cryptography.ProtectedData.Protect(
            System.Text.Encoding.UTF8.GetBytes(password), null, System.Security.Cryptography.DataProtectionScope.CurrentUser);
        PasswordProtected = Convert.ToBase64String(bytes);
    }

    public string GetPassword()
    {
        if (PasswordProtected.Length == 0) return "";
        var bytes = System.Security.Cryptography.ProtectedData.Unprotect(
            Convert.FromBase64String(PasswordProtected), null, System.Security.Cryptography.DataProtectionScope.CurrentUser);
        return System.Text.Encoding.UTF8.GetString(bytes);
    }
}
