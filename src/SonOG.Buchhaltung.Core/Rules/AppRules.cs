using System.Text.Encodings.Web;
using System.Text.Json;
using SonOG.Buchhaltung.Core.Models;

namespace SonOG.Buchhaltung.Core.Rules;

public sealed class KeywordRule
{
    public string Stichwort { get; set; } = "";
    public string Grund { get; set; } = "";
}

public sealed class TypeRule
{
    public string Typ { get; set; } = "";
    public string Grund { get; set; } = "";
}

/// <summary>Bereich auf der ersten Rechnungsseite (Punkt, Ursprung links oben), in dem der Empfänger steht.</summary>
public sealed class RegionRule
{
    public double X0 { get; set; } = 40;
    public double Y0 { get; set; } = 100;
    public double X1 { get; set; } = 330;
    public double Y1 { get; set; } = 280;
}

/// <summary>Einstellungen, wie Betrag, Empfänger und Datum in den Rechnungs-PDFs gefunden werden.</summary>
public sealed class InvoiceReadRules
{
    public List<string> BetragStichwoerter { get; set; } = new()
    {
        "Gesamtbetrag", "Rechnungsbetrag", "Endbetrag", "Zahlbetrag", "Gesamtsumme", "Endsumme", "Brutto", "zu zahlen",
    };
    public RegionRule Empfaenger { get; set; } = new();
    public string DatumRegex { get; set; } = @"(\d{2}\.\d{2}\.\d{4})";
}

/// <summary>Regeln, die der Anwender anpassen kann (regeln.json).</summary>
public sealed class AppRules
{
    /// <summary>Format der laufenden Nummer. Platzhalter: {year} und {n:0000}.</summary>
    public string NummernFormat { get; set; } = "{year}-{n:0000}";

    /// <summary>Muster der eigenen Rechnungsnummern (CAO: Jahr + 5 Stellen, z. B. 202634552). Gruppe 1 = Nummer.</summary>
    public string EigeneRechnungsnummerRegex { get; set; } = @"(?<!\d)(20\d{7})(?!\d)";

    /// <summary>Buchungen mit diesen Stichwörtern im Text werden beleglos eingebucht.</summary>
    public List<KeywordRule> BelegloseStichwoerter { get; set; } = new()
    {
        new KeywordRule { Stichwort = "FINANZAMT", Grund = "Steuer" },
    };

    /// <summary>Buchungen dieser Buchungsarten (Anfang der Erläuterung) werden beleglos eingebucht.</summary>
    public List<TypeRule> BelegloseTypen { get; set; } = new()
    {
        new TypeRule { Typ = "Entgeltabrechnung", Grund = "Bankentgelt (Anlage im Auszug)" },
    };

    public InvoiceReadRules Rechnung { get; set; } = new();

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static AppRules Load(string path)
    {
        if (!File.Exists(path))
        {
            var def = new AppRules();
            def.Save(path);
            return def;
        }
        return JsonSerializer.Deserialize<AppRules>(File.ReadAllText(path), Json) ?? new AppRules();
    }

    public void Save(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, JsonSerializer.Serialize(this, Json));
    }

    /// <summary>Setzt Beleglos/BelegloGrund an der Buchung nach den Regeln.</summary>
    public void ApplyBeleglos(Booking b)
    {
        b.Beleglos = false;
        b.BelegloGrund = "";
        foreach (var r in BelegloseStichwoerter)
        {
            if (r.Stichwort.Length > 0 && b.Text.Contains(r.Stichwort, StringComparison.OrdinalIgnoreCase))
            {
                b.Beleglos = true;
                b.BelegloGrund = r.Grund;
                return;
            }
        }
        foreach (var r in BelegloseTypen)
        {
            if (r.Typ.Length > 0 && b.Type.StartsWith(r.Typ, StringComparison.OrdinalIgnoreCase))
            {
                b.Beleglos = true;
                b.BelegloGrund = r.Grund;
                return;
            }
        }
    }
}
