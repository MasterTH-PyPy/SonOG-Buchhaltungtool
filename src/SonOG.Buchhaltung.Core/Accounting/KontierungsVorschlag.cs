using SonOG.Buchhaltung.Core.Matching;
using System.Text.RegularExpressions;
using SonOG.Buchhaltung.Core.Models;

namespace SonOG.Buchhaltung.Core.Accounting;

/// <summary>
/// Schlägt für jede Kontoauszugsbuchung eine Kontierung vor. Reihenfolge:
/// 1. Kundenzahlung mit erkannten Rechnungen → Ausgleich auf dem Debitor (je Rechnung eine Zeile).
/// 2. Erste passende Kontierungsregel.
/// 3. Personenkonto über Suchbegriff gefunden und mit Standard-Sachkonto → Beleg über das Personenkonto.
/// 4. Sonst offen (eine Zeile mit dem vollen Betrag, Konto fehlt).
/// Der Vorschlag ist immer änderbar; bestätigt wird er vom Anwender.
/// </summary>
public sealed class KontierungsVorschlag
{
    private readonly AccountingSettings _s;
    private readonly PersonAccountDirectory _dir;

    public KontierungsVorschlag(AccountingSettings settings, PersonAccountDirectory directory)
    {
        _s = settings;
        _dir = directory;
    }

    public Kontierung Vorschlagen(Booking b)
    {
        var betrag = Math.Abs(b.Amount);
        var text = Kurztext(b);

        // 1. Kundenzahlung → Debitor
        if (b.Category.IsCustomerPayment() && b.Match is { Invoices.Count: > 0 } m)
        {
            var inv = m.Invoices;
            var names = inv.Select(i => i.RecipientFirstLine).Append(b.PayerName).ToArray();
            var debitor = _dir.FindDebitor(names);
            var k = new Kontierung
            {
                Personenkonto = debitor?.Konto,
                RechnungEinbuchen = false,
                Quelle = debitor is null ? KontierungsQuelle.Offen : KontierungsQuelle.Abgleich,
                Hinweis = debitor is null
                    ? $"Debitor zu \"{inv[0].RecipientFirstLine}\" fehlt - Personenkonto wählen oder anlegen"
                    : "Kundenzahlung" + (m.CountsAsPaid ? "" : " (Abgleich noch prüfen)"),
            };
            var sum = inv.Sum(i => i.GrossAmount ?? 0);
            if (inv.Count > 1 && inv.All(i => i.GrossAmount is not null) && sum == betrag)
            {
                foreach (var i in inv)
                    k.Zeilen.Add(new KontierungsZeile { Betrag = i.GrossAmount!.Value, Rechnungsnummer = i.Number, Text = Cut($"Zahlung Re {i.Number} {i.RecipientFirstLine}") });
            }
            else
            {
                var nr = string.Join(",", inv.Select(i => i.Number));
                k.Zeilen.Add(new KontierungsZeile { Betrag = betrag, Rechnungsnummer = inv.Count == 1 ? inv[0].Number : "", Text = Cut($"Zahlung Re {nr} {inv[0].RecipientFirstLine}") });
                if (m.Difference is { } d && d != 0)
                    k.Hinweis += $"; Differenz {d:N2} zur Rechnung - Rest (Skonto o. Ä.) in Taxpool ausbuchen oder Zeile aufteilen";
            }
            return k;
        }

        // 2. Regel
        var regel = _s.Regeln.FirstOrDefault(r => r.Passt(b.Text, b.Category.ToString(), b.Amount));
        if (regel is not null) return AusRegel(b, regel);

        // 3. Personenkonto aus dem Buchungstext (Suchbegriff, Name oder markanter Namensteil; Richtung beachtet)
        var personTreffer = _dir.Erkennen(b.Text, b.Amount);
        var person = personTreffer?.Konto;

        // 3a. Zahlungseingang eines Kunden: Forderung steht aus der Sollstellung schon auf dem Debitor → nur ausgleichen
        if (person is { Art: PersonenArt.Debitor } && b.Amount > 0)
        {
            return new Kontierung
            {
                Personenkonto = person.Konto,
                RechnungEinbuchen = false,
                Quelle = KontierungsQuelle.Regel,
                Hinweis = $"Zahlungseingang {person} (erkannt über {personTreffer!.Grund}) - gleicht den offenen Posten aus",
                Zeilen = { new KontierungsZeile { Betrag = betrag, Text = text } },
            };
        }

        // 3b. Vorlage für dieses Personenkonto (z. B. aus Taxpool "ADN": 4964 an 70001) - geht vor dem Standardkonto
        if (person is not null && VorlageFuer(person, b) is { } vorlage)
        {
            var kv = AusRegel(b, vorlage);
            kv.Personenkonto = person.Konto;
            kv.Hinweis = $"Vorlage \"{vorlage.Name}\" für {person} (erkannt über {personTreffer!.Grund})" +
                         (kv.Quelle == KontierungsQuelle.Offen ? " - Sachkonto fehlt" : "");
            return kv;
        }

        // 3c. Lieferant (oder Kunde bei Abbuchung) mit Standard-Sachkonto: Beleg einbuchen, Zahlung ausbuchen
        if (person is { StandardSachkonto: > 0 })
        {
            return new Kontierung
            {
                Personenkonto = person.Konto,
                RechnungEinbuchen = true,
                Quelle = KontierungsQuelle.Regel,
                Hinweis = "Standardkonto von " + person + " (erkannt über " + personTreffer!.Grund + ")",
                Zeilen = { new KontierungsZeile { Betrag = betrag, Sachkonto = person.StandardSachkonto, BuSchluessel = person.StandardBuSchluessel, Rechnungsnummer = Rechnungsnummer.Aus(b), Text = text } },
            };
        }

        // 4. Offen
        return new Kontierung
        {
            Personenkonto = person?.Konto,
            RechnungEinbuchen = person is not null,
            Quelle = KontierungsQuelle.Offen,
            Hinweis = "Kein Vorschlag - Konto wählen" + (person is null ? "" : " (Personenkonto " + person + " erkannt über " + personTreffer!.Grund + ")"),
            Zeilen = { new KontierungsZeile { Betrag = betrag, Text = text } },
        };
    }

    /// <summary>
    /// Vorlage, die genau dieses Personenkonto bebucht und zur Richtung passt. Bei mehreren gewinnt die, deren Name
    /// am besten zum Buchungstext passt; bei Gleichstand die kürzeste Bezeichnung (meist die allgemeine).
    /// </summary>
    private KontierungsRegel? VorlageFuer(PersonAccount person, Booking b)
    {
        var richtung = b.Amount < 0 ? Richtung.Ausgang : Richtung.Eingang;
        return _s.Regeln
            .Where(r => r.Personenkonto == person.Konto && (r.Richtung == Richtung.Alle || r.Richtung == richtung))
            .Where(r => r.Sachkonto > 0 || r.Aufteilung is { Count: > 0 } || !r.RechnungEinbuchen)
            .OrderByDescending(r => NameMatcher.Score(Ohne(r.Name), b.Text))
            .ThenBy(r => r.Name.Length)
            .FirstOrDefault();
    }

    private static string Ohne(string name) =>
        name.StartsWith(TaxpoolVorlagenImport.Praefix, StringComparison.Ordinal) ? name[TaxpoolVorlagenImport.Praefix.Length..] : name;

    /// <summary>Wendet eine Regel oder Vorlage auf die Buchung an (auch von Hand aus der Oberfläche).</summary>
    public Kontierung AusRegel(Booking b, KontierungsRegel regel)
    {
        PersonAccount? person = null;
        PersonTreffer? treffer = null;
        if (regel.Personenkonto > 0) person = _dir.Get(regel.Personenkonto);
        else if (regel.PersonenkontoSuchen) person = (treffer = _dir.Erkennen(b.Text, b.Amount))?.Konto;

        int? personKonto = regel.Personenkonto > 0 ? regel.Personenkonto : person?.Konto;
        int sachkonto = regel.Sachkonto > 0 ? regel.Sachkonto : person?.StandardSachkonto ?? 0;
        var bu = regel.Sachkonto > 0 ? regel.BuSchluessel : (person?.StandardBuSchluessel ?? regel.BuSchluessel);

        var hinweis = "Regel: " + regel + (treffer is null ? "" : ", " + person + " erkannt über " + treffer.Grund);
        var quelle = KontierungsQuelle.Regel;
        if (regel.PersonenkontoSuchen && personKonto is null)
        {
            hinweis += " - Personenkonto nicht gefunden, bitte wählen oder anlegen";
            quelle = KontierungsQuelle.Offen;
        }
        if (sachkonto == 0 && regel.Aufteilung is not { Count: > 0 } && (personKonto is null || regel.RechnungEinbuchen))
        {
            hinweis += " - Sachkonto fehlt";
            quelle = KontierungsQuelle.Offen;
        }

        // Rechnungsnummer: endet der Vorlagentext auf "Re-Nr.:", wird sie angehängt; außerdem Belegfeld 2
        var reNr = Rechnungsnummer.Aus(b);
        var text = regel.Buchungstext.Trim().Length > 0 ? Rechnungsnummer.Einsetzen(regel.Buchungstext.Trim(), reNr) : Kurztext(b);
        var k = new Kontierung
        {
            Personenkonto = personKonto,
            RechnungEinbuchen = personKonto is not null && regel.RechnungEinbuchen,
            Quelle = quelle,
            Hinweis = hinweis,
        };

        if (regel.Aufteilung is { Count: > 0 } teile)
        {
            // Splitvorlage: Betrag nach Anteilen verteilen, Rundungsrest auf den letzten Teil
            var gesamt = Math.Abs(b.Amount);
            var summeAnteile = teile.Sum(t => Math.Abs(t.Anteil));
            decimal verteilt = 0;
            for (int i = 0; i < teile.Count; i++)
            {
                var t = teile[i];
                var betrag = i == teile.Count - 1
                    ? gesamt - verteilt
                    : Math.Round(gesamt * (summeAnteile == 0 ? 1m / teile.Count : Math.Abs(t.Anteil) / summeAnteile), 2, MidpointRounding.AwayFromZero);
                verteilt += betrag;
                k.Zeilen.Add(new KontierungsZeile
                {
                    Betrag = betrag,
                    Sachkonto = t.Sachkonto,
                    BuSchluessel = t.BuSchluessel,
                    Rechnungsnummer = reNr,
                    Text = t.Text.Trim().Length > 0 ? Rechnungsnummer.Einsetzen(t.Text.Trim(), reNr) : text,
                });
            }
            if (k.Zeilen.Any(z => z.Sachkonto == 0) && k.Quelle != KontierungsQuelle.Offen)
            {
                k.Quelle = KontierungsQuelle.Offen;
                k.Hinweis += " - Sachkonto fehlt";
            }
            return k;
        }

        k.Zeilen.Add(new KontierungsZeile { Betrag = Math.Abs(b.Amount), Sachkonto = sachkonto, BuSchluessel = bu, Rechnungsnummer = reNr, Text = text });
        return k;
    }

    /// <summary>Kurzer Buchungstext aus dem Kontoauszug (max. 60 Zeichen).</summary>
    public static string Kurztext(Booking b)
    {
        if (b.AmazonOrder.Length > 0) return Cut("Amazon " + b.AmazonOrder);
        var t = Regex.Replace(b.Text, @"\s+", " ").Trim();
        if (b.PayerName.Length > 0)
        {
            var rest = t.StartsWith(b.PayerName, StringComparison.Ordinal) ? t[b.PayerName.Length..].Trim() : "";
            return Cut((b.PayerName + " " + rest).Trim());
        }
        return Cut(t.Length > 0 ? t : b.Type);
    }

    internal static string Cut(string s, int max = 60)
    {
        s = Regex.Replace(s, @"\s+", " ").Trim();
        return s.Length <= max ? s : s[..max].TrimEnd();
    }
}
