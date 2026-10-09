using System.Text.Json;

namespace SonOG.Buchhaltung.Infrastructure;

/// <summary>Zuletzt verwendete Pfade. Es sind keine Zugangsdaten enthalten, deshalb als normale JSON-Datei.</summary>
public sealed class AppSettings
{
    public string StatementPath { get; set; } = "";
    public string InvoiceFolder { get; set; } = "";
    public string AmazonFolder { get; set; } = "";
    public string OutputFolder { get; set; } = "";

    /// <summary>Offene Rechnungen erst ab diesem Rechnungsdatum anzeigen (leer = alle).</summary>
    public string OpenFrom { get; set; } = "";

    public static string DataDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SonOG-Buchhaltung");

    public static string FilePath => Path.Combine(DataDir, "einstellungen.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
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
