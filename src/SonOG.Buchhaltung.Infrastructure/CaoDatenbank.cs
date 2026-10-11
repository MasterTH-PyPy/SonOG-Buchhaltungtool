using System.Globalization;
using MySqlConnector;
using SonOG.Buchhaltung.Core.Invoices;

namespace SonOG.Buchhaltung.Infrastructure;

/// <summary>Zugang zur CAO-Faktura-Datenbank (MySQL/MariaDB). Das Passwort wird mit Windows-DPAPI verschlüsselt gespeichert.</summary>
public sealed class CaoVerbindung
{
    public bool Aktiv { get; set; }
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 3306;
    public string Datenbank { get; set; } = "cao";
    public string Benutzer { get; set; } = "";
    public string PasswortGeschuetzt { get; set; } = "";

    public bool Eingerichtet => Aktiv && Host.Trim().Length > 0 && Datenbank.Trim().Length > 0;

    public void SetPasswort(string passwort)
    {
        var bytes = System.Security.Cryptography.ProtectedData.Protect(
            System.Text.Encoding.UTF8.GetBytes(passwort), null, System.Security.Cryptography.DataProtectionScope.CurrentUser);
        PasswortGeschuetzt = Convert.ToBase64String(bytes);
    }

    public string GetPasswort()
    {
        if (PasswortGeschuetzt.Length == 0) return "";
        var bytes = System.Security.Cryptography.ProtectedData.Unprotect(
            Convert.FromBase64String(PasswortGeschuetzt), null, System.Security.Cryptography.DataProtectionScope.CurrentUser);
        return System.Text.Encoding.UTF8.GetString(bytes);
    }

    public string ConnectionString => new MySqlConnectionStringBuilder
    {
        Server = Host.Trim(),
        Port = (uint)Math.Clamp(Port, 1, 65535),
        Database = Datenbank.Trim(),
        UserID = Benutzer.Trim(),
        Password = GetPasswort(),
        ConnectionTimeout = 5,
        DefaultCommandTimeout = 30,
        // CAO läuft oft auf einer alten MySQL-Version ohne TLS
        SslMode = MySqlSslMode.Preferred,
        AllowZeroDateTime = true,
        ConvertZeroDateTime = true,
        CharacterSet = "utf8",
    }.ConnectionString;
}

/// <summary>Liest Rechnungen aus der CAO-Tabelle JOURNAL: Rechnungsnummer → Kunde (KUN_NUM, KUN_NAME1), Brutto, Datum.</summary>
public static class CaoDatenbank
{
    /// <summary>Prüft die Verbindung und liefert die Anzahl der Rechnungen im Journal.</summary>
    public static long Testen(CaoVerbindung v)
    {
        using var con = new MySqlConnection(v.ConnectionString);
        con.Open();
        using var cmd = new MySqlCommand("SELECT COUNT(*) FROM JOURNAL WHERE VRENUM IS NOT NULL AND VRENUM <> ''", con);
        return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    /// <summary>Rechnungen zu den angegebenen Nummern. Bei mehreren Journalzeilen zählt die Verkaufsrechnung (QUELLE 3).</summary>
    public static Dictionary<string, CaoRechnung> Lesen(CaoVerbindung v, IEnumerable<string> nummern)
    {
        var result = new Dictionary<string, CaoRechnung>(StringComparer.Ordinal);
        var liste = nummern.Where(n => n.Length > 0).Distinct().ToList();
        if (liste.Count == 0) return result;

        using var con = new MySqlConnection(v.ConnectionString);
        con.Open();
        foreach (var block in liste.Chunk(500))
        {
            using var cmd = con.CreateCommand();
            var namen = new List<string>();
            for (int i = 0; i < block.Length; i++)
            {
                namen.Add("@n" + i);
                cmd.Parameters.AddWithValue("@n" + i, block[i]);
            }
            cmd.CommandText = "SELECT VRENUM, KUN_NUM, KUN_NAME1, BSUMME, RDATUM, QUELLE FROM JOURNAL " +
                              $"WHERE VRENUM IN ({string.Join(",", namen)}) ORDER BY (QUELLE = 3) DESC";
            using var rd = cmd.ExecuteReader();
            while (rd.Read())
            {
                var nr = Text(rd, 0);
                if (nr.Length == 0 || result.ContainsKey(nr)) continue; // erste Zeile (Verkaufsrechnung) gewinnt
                result[nr] = new CaoRechnung(nr, Text(rd, 1), Text(rd, 2), Betrag(rd, 3), Datum(rd, 4));
            }
        }
        return result;
    }

    private static string Text(MySqlDataReader rd, int i) =>
        rd.IsDBNull(i) ? "" : Convert.ToString(rd.GetValue(i), CultureInfo.InvariantCulture)?.Trim() ?? "";

    private static decimal? Betrag(MySqlDataReader rd, int i)
    {
        if (rd.IsDBNull(i)) return null;
        try { return Math.Round(Convert.ToDecimal(rd.GetValue(i), CultureInfo.InvariantCulture), 2, MidpointRounding.AwayFromZero); }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException) { return null; }
    }

    private static DateOnly? Datum(MySqlDataReader rd, int i)
    {
        if (rd.IsDBNull(i)) return null;
        return rd.GetValue(i) switch
        {
            DateTime dt when dt.Year > 1900 => DateOnly.FromDateTime(dt),
            DateOnly d when d.Year > 1900 => d,
            string s when DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var p) && p.Year > 1900 => DateOnly.FromDateTime(p),
            _ => null,
        };
    }
}
