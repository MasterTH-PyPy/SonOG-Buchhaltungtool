using SonOG.Buchhaltung.Core.Invoices;
using SonOG.Buchhaltung.Core.Models;

namespace SonOG.Buchhaltung.Core.Accounting;

public enum PruefStufe
{
    Fehler,
    Hinweis,
}

public sealed record Pruefung(PruefStufe Stufe, string Nummer, string Text)
{
    public override string ToString() => $"{(Stufe == PruefStufe.Fehler ? "Fehler" : "Hinweis")} {Nummer}: {Text}";
}

/// <summary>Erzeugt aus den Kontierungen die DATEV-Buchungssätze und prüft sie vorher.</summary>
public static class Buchungssaetze
{
    // ---------------------------------------------------------------------------------------------
    // Prüfung
    // ---------------------------------------------------------------------------------------------

    public static List<Pruefung> Pruefen(Booking b, AccountingSettings s, PersonAccountDirectory dir)
    {
        var p = new List<Pruefung>();
        var nr = b.Number;
        var k = b.Kontierung;
        if (k is null || k.Zeilen.Count == 0)
        {
            p.Add(new(PruefStufe.Fehler, nr, "nicht kontiert"));
            return p;
        }

        if (k.Summe != Math.Abs(b.Amount))
            p.Add(new(PruefStufe.Fehler, nr, $"Summe der Zeilen {k.Summe:N2} ≠ Buchungsbetrag {Math.Abs(b.Amount):N2}"));

        if (k.Personenkonto is { } pk)
        {
            if (!s.IstPersonenkonto(pk))
                p.Add(new(PruefStufe.Fehler, nr, $"{pk} liegt in keinem Personenkonten-Nummernkreis (Debitoren {s.DebitorenVon}-{s.DebitorenBis}, Kreditoren {s.KreditorenVon}-{s.KreditorenBis})"));
            else if (dir.Get(pk) is null)
                p.Add(new(PruefStufe.Hinweis, nr, $"Personenkonto {pk} ist nicht im Verzeichnis - in Taxpool vorhanden?"));
        }

        int i = 0;
        foreach (var z in k.Zeilen)
        {
            i++;
            var zeile = k.Zeilen.Count > 1 ? $"Zeile {i}: " : "";
            if (z.Betrag == 0) p.Add(new(PruefStufe.Fehler, nr, zeile + "Betrag 0"));
            if (k.BrauchtSachkonto)
            {
                if (z.Sachkonto <= 0) p.Add(new(PruefStufe.Fehler, nr, zeile + "Sachkonto fehlt"));
                else if (!s.IstSachkonto(z.Sachkonto))
                    p.Add(new(PruefStufe.Fehler, nr, zeile + $"Sachkonto {z.Sachkonto} hat mehr als {s.SachkontenLaenge} Stellen"));
                else if (z.Sachkonto == s.Bankkonto)
                    p.Add(new(PruefStufe.Fehler, nr, zeile + "Sachkonto ist das Bankkonto selbst"));
                else if (s.Kontenplan.Count > 0 && !s.Kontenplan.ContainsKey(z.Sachkonto))
                    p.Add(new(PruefStufe.Hinweis, nr, zeile + $"Sachkonto {z.Sachkonto} steht nicht im eingelesenen Kontenplan"));
            }
            if (z.Belegdatum is { } d && d.Year != b.Date.Year && k.RechnungEinbuchen)
                p.Add(new(PruefStufe.Hinweis, nr, zeile + $"Rechnungsdatum {d:dd.MM.yyyy} liegt im Vorjahr - es wird mit dem Zahlungsdatum gebucht"));
        }

        if (k.Personenkonto is null && b.Category.IsCustomerPayment())
            p.Add(new(PruefStufe.Hinweis, nr, "Kundenzahlung ohne Debitor - offener Posten wird nicht ausgeglichen"));
        return p;
    }

    // ---------------------------------------------------------------------------------------------
    // Kontoauszug → Buchungssätze
    // ---------------------------------------------------------------------------------------------

    /// <summary>Buchungssätze einer Kontoauszugsbuchung (ohne Prüfung).</summary>
    public static List<DatevBuchung> AusKontoauszug(Booking b, AccountingSettings s)
    {
        var k = b.Kontierung ?? throw new InvalidOperationException($"Buchung {b.Number} ist nicht kontiert.");
        var list = new List<DatevBuchung>();
        int vorzeichen = b.Amount >= 0 ? 1 : -1; // +1 = Geldeingang
        var bank = s.Bankkonto;

        if (k.Personenkonto is not int person)
        {
            // Direkt gegen die Bank
            foreach (var z in k.Zeilen)
            {
                var eff = vorzeichen * z.Betrag;
                list.Add(new DatevBuchung
                {
                    Umsatz = Math.Abs(z.Betrag),
                    SollHaben = eff > 0 ? 'S' : 'H',
                    Konto = bank,
                    Gegenkonto = z.Sachkonto,
                    BuSchluessel = z.BuSchluessel,
                    Belegdatum = b.Date,
                    Belegfeld1 = b.Number,
                    Belegfeld2 = z.Rechnungsnummer,
                    Buchungstext = z.Text,
                });
            }
            return list;
        }

        if (k.RechnungEinbuchen)
        {
            // 1. Beleg auf dem Personenkonto einbuchen: Ausgabe → Personenkonto Haben (Verbindlichkeit), Eingang → Soll.
            foreach (var z in k.Zeilen)
            {
                var eff = vorzeichen * z.Betrag;
                var datum = z.Belegdatum is { } d && d.Year == b.Date.Year && d <= b.Date ? d : b.Date;
                list.Add(new DatevBuchung
                {
                    Umsatz = Math.Abs(z.Betrag),
                    SollHaben = eff > 0 ? 'S' : 'H',
                    Konto = person,
                    Gegenkonto = z.Sachkonto,
                    BuSchluessel = z.BuSchluessel,
                    Belegdatum = datum,
                    Belegfeld1 = b.Number,
                    Belegfeld2 = z.Rechnungsnummer,
                    Buchungstext = z.Text,
                });
            }
            // 2. Zahlung vom Personenkonto ausbuchen
            var text = k.Zeilen.Count == 1 ? k.Zeilen[0].Text : (k.Zeilen.FirstOrDefault(z => z.Text.Length > 0)?.Text ?? "");
            list.Add(new DatevBuchung
            {
                Umsatz = Math.Abs(b.Amount),
                SollHaben = vorzeichen > 0 ? 'S' : 'H',
                Konto = bank,
                Gegenkonto = person,
                Belegdatum = b.Date,
                Belegfeld1 = b.Number,
                Belegfeld2 = k.Zeilen.Count == 1 ? k.Zeilen[0].Rechnungsnummer : "",
                Buchungstext = KontierungsVorschlag.Cut("Zahlung " + text),
            });
            return list;
        }

        // Ausgleich offener Posten auf dem Personenkonto (je Rechnung eine Zeile)
        foreach (var z in k.Zeilen)
        {
            var eff = vorzeichen * z.Betrag;
            bool reInBf1 = s.RechnungsnummerInBelegfeld1BeiAusgleich && z.Rechnungsnummer.Length > 0;
            list.Add(new DatevBuchung
            {
                Umsatz = Math.Abs(z.Betrag),
                SollHaben = eff > 0 ? 'S' : 'H',
                Konto = bank,
                Gegenkonto = person,
                Belegdatum = b.Date,
                Belegfeld1 = reInBf1 ? z.Rechnungsnummer : b.Number,
                Belegfeld2 = reInBf1 ? b.Number : z.Rechnungsnummer,
                Buchungstext = z.Text,
            });
        }
        return list;
    }

    // ---------------------------------------------------------------------------------------------
    // Sollstellung der Ausgangsrechnungen
    // ---------------------------------------------------------------------------------------------

    /// <summary>Forderung auf dem Debitor: Debitor Soll an Erlöskonto (Gutschrift/Storno umgekehrt).</summary>
    public static List<DatevBuchung> AusSollstellung(SollstellungsPosten p)
    {
        var k = p.Kontierung;
        var person = k.Personenkonto ?? throw new InvalidOperationException($"Rechnung {p.Rechnung.Number}: Debitor fehlt.");
        var datum = p.Rechnung.Date ?? throw new InvalidOperationException($"Rechnung {p.Rechnung.Number}: Rechnungsdatum fehlt.");
        int vorzeichen = (p.Rechnung.GrossAmount ?? 0) >= 0 ? 1 : -1;
        return k.Zeilen.Select(z =>
        {
            var eff = vorzeichen * z.Betrag;
            return new DatevBuchung
            {
                Umsatz = Math.Abs(z.Betrag),
                SollHaben = eff > 0 ? 'S' : 'H',
                Konto = person,
                Gegenkonto = z.Sachkonto,
                BuSchluessel = z.BuSchluessel,
                Belegdatum = datum,
                Belegfeld1 = p.Rechnung.Number,
                Buchungstext = z.Text,
            };
        }).ToList();
    }

    public static List<Pruefung> Pruefen(SollstellungsPosten p, AccountingSettings s, PersonAccountDirectory dir)
    {
        var list = new List<Pruefung>();
        var nr = "Re " + p.Rechnung.Number;
        var k = p.Kontierung;
        if (p.Rechnung.Date is null) list.Add(new(PruefStufe.Fehler, nr, "Rechnungsdatum nicht lesbar"));
        if (p.Rechnung.GrossAmount is not { } brutto) list.Add(new(PruefStufe.Fehler, nr, "Rechnungsbetrag nicht lesbar"));
        else if (k.Summe != Math.Abs(brutto)) list.Add(new(PruefStufe.Fehler, nr, $"Summe {k.Summe:N2} ≠ Rechnungsbetrag {Math.Abs(brutto):N2}"));
        if (k.Personenkonto is not { } pk) list.Add(new(PruefStufe.Fehler, nr, "Debitor fehlt"));
        else if (pk < s.DebitorenVon || pk > s.DebitorenBis) list.Add(new(PruefStufe.Fehler, nr, $"{pk} ist kein Debitorenkonto"));
        else if (dir.Get(pk) is null) list.Add(new(PruefStufe.Hinweis, nr, $"Debitor {pk} ist nicht im Verzeichnis - in Taxpool vorhanden?"));
        foreach (var z in k.Zeilen)
        {
            if (z.Sachkonto <= 0) list.Add(new(PruefStufe.Fehler, nr, "Erlöskonto fehlt"));
            else if (!s.IstSachkonto(z.Sachkonto)) list.Add(new(PruefStufe.Fehler, nr, $"Erlöskonto {z.Sachkonto} hat mehr als {s.SachkontenLaenge} Stellen"));
            if (z.Betrag == 0) list.Add(new(PruefStufe.Fehler, nr, "Betrag 0"));
        }
        return list;
    }

    // ---------------------------------------------------------------------------------------------
    // Kopf
    // ---------------------------------------------------------------------------------------------

    /// <summary>DATEV-Kopf für die Buchungen. Alle Buchungen müssen im selben Wirtschaftsjahr liegen.</summary>
    public static DatevKopf Kopf(IReadOnlyList<DatevBuchung> buchungen, AccountingSettings s, string bezeichnung)
    {
        if (buchungen.Count == 0) throw new InvalidOperationException("Keine Buchungen zum Exportieren.");
        var von = buchungen.Min(b => b.Belegdatum);
        var bis = buchungen.Max(b => b.Belegdatum);
        var wj = s.WirtschaftsjahrBeginn(von);
        if (s.WirtschaftsjahrBeginn(bis) != wj)
            throw new InvalidOperationException($"Die Buchungen liegen in zwei Wirtschaftsjahren ({von:dd.MM.yyyy} bis {bis:dd.MM.yyyy}). Bitte getrennt exportieren.");
        return new DatevKopf
        {
            Berater = s.Beraternummer,
            Mandant = s.Mandantennummer,
            WirtschaftsjahrBeginn = wj,
            SachkontenLaenge = s.SachkontenLaenge,
            DatumVon = von,
            DatumBis = bis,
            Bezeichnung = bezeichnung,
            Diktatkuerzel = s.Diktatkuerzel,
            Herkunft = s.Herkunft,
            Festschreibung = s.Festschreiben,
            Kontenrahmen = s.Kontenrahmen,
        };
    }
}

/// <summary>Eine Ausgangsrechnung für die Sollstellung (Forderung auf dem Debitor einbuchen).</summary>
public sealed class SollstellungsPosten
{
    public SollstellungsPosten(InvoiceRecord rechnung, Kontierung kontierung)
    {
        Rechnung = rechnung;
        Kontierung = kontierung;
    }

    public InvoiceRecord Rechnung { get; }
    public Kontierung Kontierung { get; set; }

    /// <summary>Soll mit exportiert werden.</summary>
    public bool Auswahl { get; set; } = true;

    /// <summary>Monat, in dem die Rechnung schon sollgestellt wurde (z. B. "2026-09"), sonst leer.</summary>
    public string BereitsGestellt { get; set; } = "";
}
