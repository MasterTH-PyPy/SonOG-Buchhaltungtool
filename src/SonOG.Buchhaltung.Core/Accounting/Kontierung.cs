using System.Text.Json.Serialization;

namespace SonOG.Buchhaltung.Core.Accounting;

/// <summary>Eine Zeile der Kontierung (bei Splitbuchungen mehrere je Kontoauszugsbuchung).</summary>
public sealed class KontierungsZeile
{
    /// <summary>
    /// Bruttobetrag der Zeile, positiv in Richtung der Buchung. Die Summe aller Zeilen ergibt den Betrag im Kontoauszug.
    /// Eine negative Zeile kehrt die Richtung um (z. B. eine Erstattung, die mit einer Abbuchung verrechnet wurde).
    /// </summary>
    public decimal Betrag { get; set; }

    /// <summary>Sachkonto (Aufwand, Erlös, Privat ...). Beim reinen Ausgleich eines Personenkontos nicht nötig.</summary>
    public int Sachkonto { get; set; }

    public string BuSchluessel { get; set; } = "";

    /// <summary>Rechnungs-/Belegnummer des Geschäftspartners (offener Posten).</summary>
    public string Rechnungsnummer { get; set; } = "";

    /// <summary>Datum der Rechnung (für die Einbuchung auf dem Personenkonto). Leer = Datum der Zahlung.</summary>
    public DateOnly? Belegdatum { get; set; }

    public string Text { get; set; } = "";

    public KontierungsZeile Clone() => (KontierungsZeile)MemberwiseClone();
}

public enum KontierungsQuelle
{
    /// <summary>Kein Vorschlag möglich - bitte kontieren.</summary>
    Offen,
    /// <summary>Aus einer Kontierungsregel.</summary>
    Regel,
    /// <summary>Automatisch aus dem Abgleich (z. B. Kundenzahlung → Debitor).</summary>
    Abgleich,
    /// <summary>Von Hand bearbeitet.</summary>
    Manuell,
}

/// <summary>
/// Kontierung einer Kontoauszugsbuchung. Drei Fälle:
/// <list type="bullet">
/// <item>Kein Personenkonto: jede Zeile wird direkt gegen die Bank gebucht (Bankentgelt, Finanzamt, Privat ...).</item>
/// <item>Personenkonto mit <see cref="RechnungEinbuchen"/>: jede Zeile wird als Beleg auf dem Personenkonto eingebucht
/// (Sachkonto an Kreditor bzw. Debitor an Sachkonto) und die Zahlung danach vom Personenkonto ausgebucht.</item>
/// <item>Personenkonto ohne <see cref="RechnungEinbuchen"/>: die Zahlung gleicht offene Posten aus, die schon auf dem
/// Personenkonto stehen (z. B. Ausgangsrechnungen aus der Sollstellung). Je Rechnung eine Zeile.</item>
/// </list>
/// </summary>
public sealed class Kontierung
{
    public int? Personenkonto { get; set; }
    public bool RechnungEinbuchen { get; set; }
    public List<KontierungsZeile> Zeilen { get; set; } = new();

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public KontierungsQuelle Quelle { get; set; }

    /// <summary>Woher der Vorschlag kommt (Regelname, "Kundenzahlung" ...) oder was fehlt.</summary>
    public string Hinweis { get; set; } = "";

    /// <summary>Vom Anwender geprüft und bestätigt.</summary>
    public bool Bestaetigt { get; set; }

    [JsonIgnore]
    public decimal Summe => Zeilen.Sum(z => z.Betrag);

    [JsonIgnore]
    public bool BrauchtSachkonto => Personenkonto is null || RechnungEinbuchen;

    public Kontierung Clone()
    {
        var c = (Kontierung)MemberwiseClone();
        c.Zeilen = Zeilen.Select(z => z.Clone()).ToList();
        return c;
    }
}
