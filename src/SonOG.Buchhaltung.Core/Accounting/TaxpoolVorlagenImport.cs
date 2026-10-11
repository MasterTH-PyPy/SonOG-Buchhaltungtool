namespace SonOG.Buchhaltung.Core.Accounting;

/// <summary>
/// Übernimmt Buchungsvorlagen, Kontenplan und Personenkonten aus der Taxpool-Datenbank.
/// <para>Vorlagen werden zu Kontierungsregeln ohne Stichwort (also Vorlagen, die man je Buchung anwendet).
/// Ein Stichwort, das der Anwender ergänzt, macht sie zur automatischen Regel und bleibt beim erneuten Einlesen erhalten.</para>
/// <para>Steuer: Automatikkonten bekommen keinen BU-Schlüssel. Sonst gilt der Steuersatz der Vorlage, ersatzweise der
/// des Kontos; bei Ausgaben der Vorsteuer-, bei Einnahmen der Umsatzsteuer-Schlüssel aus der Taxpool-Steuertabelle.</para>
/// </summary>
public static class TaxpoolVorlagenImport
{
    public const string Praefix = "Taxpool: ";

    public static TaxpoolImportErgebnis Uebernehmen(TaxpoolDaten d, AccountingSettings s, PersonAccountDirectory dir, int? jahr = null)
    {
        var e = new TaxpoolImportErgebnis();
        int aktJahr = jahr ?? DateTime.Today.Year;

        // ------------------------------------------------------------- Konten
        foreach (var k in d.Konten.Values)
        {
            if (s.IstPersonenkonto(k.Nummer)) continue;
            if (k.Name.Length > 0) s.Kontenplan[k.Nummer] = k.Name;
            e.Sachkonten++;
        }
        s.Automatikkonten = d.Konten.Values.Where(k => k.Automatik && !s.IstPersonenkonto(k.Nummer)).Select(k => k.Nummer).OrderBy(n => n).ToList();
        s.KontoSteuerschluessel = new Dictionary<int, string>();
        foreach (var k in d.Konten.Values.Where(k => !k.Automatik && k.SteuerCode > 0 && !s.IstPersonenkonto(k.Nummer)))
            if (d.SteuerFuerCode(k.SteuerCode) is { } st && (st.SchluesselVSt.Length > 0 || st.SchluesselUSt.Length > 0))
                s.KontoSteuerschluessel[k.Nummer] = st.SchluesselVSt + "|" + st.SchluesselUSt;

        // ------------------------------------------------------------- Bankkonto
        // Steht in buchhaltung.json ein Bankkonto, das in Taxpool nie bebucht wird, das tatsächlich genutzte übernehmen
        var banken = d.Konten.Values
            .Where(k => !s.IstPersonenkonto(k.Nummer) && k.Name.TrimStart().StartsWith("Bank", StringComparison.OrdinalIgnoreCase)
                        && !k.Name.Contains("geb", StringComparison.OrdinalIgnoreCase) && !k.Name.Contains("kosten", StringComparison.OrdinalIgnoreCase))
            .Select(k => (k.Nummer, k.Name, Nutzung: d.KontoNutzung.GetValueOrDefault(k.Nummer)))
            .Where(k => k.Nutzung > 0)
            .OrderByDescending(k => k.Nutzung).ToList();
        if (banken.Count > 0 && d.KontoNutzung.GetValueOrDefault(s.Bankkonto) == 0)
        {
            e.Hinweise.Add($"Bankkonto auf {banken[0].Nummer} \"{banken[0].Name}\" umgestellt (bisher {s.Bankkonto}, in Taxpool nicht bebucht)." +
                           (banken.Count > 1 ? " Weitere genutzte Bankkonten: " + string.Join(", ", banken.Skip(1).Select(b => $"{b.Nummer} {b.Name}")) + "." : ""));
            s.Bankkonto = banken[0].Nummer;
        }
        var bankKonten = new HashSet<int>(banken.Select(b => b.Nummer)) { s.Bankkonto };

        // ------------------------------------------------------------- Personenkonten
        foreach (var (konto, name) in d.Personen)
        {
            // Im Debitorenbereich angelegte Aufwands-/Erlöskonten (z. B. 20000 "Serverkosten") sind keine Kunden
            if (konto >= s.DebitorenVon && konto <= s.DebitorenBis && d.Konten.TryGetValue(konto, out var kt) && kt.Typ == 1)
            {
                e.Hinweise.Add($"{konto} {name}: in Taxpool als Aufwands-/Erlöskonto geführt - nicht als Kunde übernommen.");
                if (dir.Get(konto) is { Suchbegriffe.Count: 0 } alt) dir.Konten.Remove(alt); // früher irrtümlich übernommen
                continue;
            }
            if (!s.IstPersonenkonto(konto))
            {
                e.Hinweise.Add($"Personenkonto {konto} {name}: liegt außerhalb der Nummernkreise in buchhaltung.json - übergangen.");
                continue;
            }
            var art = konto >= s.DebitorenVon && konto <= s.DebitorenBis ? PersonenArt.Debitor : PersonenArt.Kreditor;
            dir.Upsert(konto, name, art).Nutzung = d.KontoNutzung.GetValueOrDefault(konto);
            e.Personenkonten++;
        }

        // Übliches Gegenkonto je Personenkonto aus den Taxpool-Buchungen (nur wo keins von Hand gesetzt ist)
        int standard = 0;
        foreach (var p in dir.Konten.Where(p => p.StandardSachkonto == 0 || p.StandardAusTaxpool))
        {
            if (!d.Gegenkonten.TryGetValue(p.Konto, out var gegen)) continue;
            var best = gegen
                .Where(g => !bankKonten.Contains(g.Key) && !s.IstPersonenkonto(g.Key) && s.IstSachkonto(g.Key) && g.Key >= 1000)
                .OrderByDescending(g => g.Value).ThenBy(g => g.Key)
                .Select(g => g.Key).FirstOrDefault();
            if (best == 0) continue;
            p.StandardSachkonto = best;
            p.StandardBuSchluessel = Bu(best, p.Art == PersonenArt.Kreditor, -1, d);
            p.StandardAusTaxpool = true;
            standard++;
        }
        if (standard > 0)
            e.Hinweise.Add($"Für {standard} Personenkonten das übliche Gegenkonto aus den Taxpool-Buchungen als Standard-Sachkonto übernommen.");

        // Form der Rechnungsnummern je Personenkonto (für das Finden im Kontoauszug ohne "RE" davor)
        int formen = 0;
        foreach (var p in dir.Konten)
        {
            if (!d.Rechnungsnummern.TryGetValue(p.Konto, out var bsp) || bsp.Count == 0) { p.RechnungsnummerFormen = null; continue; }
            p.RechnungsnummerFormen = Rechnungsnummer.Formen(bsp);
            if (p.RechnungsnummerFormen.Count > 0) formen++;
        }
        if (formen > 0) e.Hinweise.Add($"Für {formen} Personenkonten die Form der Rechnungsnummern aus den Taxpool-Buchungstexten gelernt.");

        // EK-Belegnummern: Kreis und letzten Stand je Jahr übernehmen (das Tool zählt beim Export weiter)
        if (d.EkKreise.Count > 0)
        {
            var kreis = d.EkKreise.OrderByDescending(k => k.Value).First().Key;
            s.EkBelegkreis = kreis;
            foreach (var ((_, ekJahr), max) in d.EkMax.Where(x => x.Key.Kreis == kreis))
                if (max > s.EkLetzteNummer.GetValueOrDefault(ekJahr)) s.EkLetzteNummer[ekJahr] = max;
            var letztesJahr = s.EkLetzteNummer.Keys.DefaultIfEmpty(0).Max();
            if (letztesJahr > 0)
                e.Hinweise.Add($"Eingangsrechnungen: zuletzt {kreis} {s.EkLetzteNummer[letztesJahr]} ({letztesJahr}), das Tool vergibt ab {kreis} {s.EkLetzteNummer[letztesJahr] + 1}.");
        }

        // ------------------------------------------------------------- Vorlagen
        var sichtbar = d.Vorlagen.Where(v => v.Anzeigen && (v.BisJahr <= 0 || v.BisJahr >= aktJahr)).ToList();
        int ausgeblendet = d.Vorlagen.Count - sichtbar.Count;

        var teile = sichtbar.Where(v => v.IstSplit && v.ParentId >= 0).ToList();
        var koepfe = sichtbar.Where(v => v.IstSplit && v.ParentId < 0).OrderBy(v => v.Id).ToList();
        var einzel = sichtbar.Where(v => !v.IstSplit).ToList();

        int bank = s.Bankkonto;
        bool OhneBank(TaxpoolVorlage v) => v.SollKonto != bank && v.HabenKonto != bank;
        // Umbuchungsvorlagen (z. B. KFZ-1-%-Regel: 1880 an 8921/8924) haben weder im Kopf noch in den Teilen ein Bankkonto
        bool IstUmbuchungsKopf(TaxpoolVorlage k) => OhneBank(k) && (k.SollKonto != 0 || k.HabenKonto != 0);
        var umbuchungen = koepfe.Where(IstUmbuchungsKopf).ToList();
        koepfe = koepfe.Except(umbuchungen).ToList();

        // Teile einer Splitvorlage: gleiche ParentID; der Kopf ist die Splitvorlage mit der höchsten ID unterhalb der Teile
        // (in Taxpool steht beim Kopf keine Verweisnummer auf die Teile; neue Vorlagen haben teils gar keine ID)
        var gruppen = teile.GroupBy(v => v.ParentId).ToList();
        var teileJeKopf = new Dictionary<TaxpoolVorlage, List<TaxpoolVorlage>>();
        int umbuchungsTeile = 0;
        foreach (var g in gruppen)
        {
            int minTeil = g.Min(v => v.Id);
            var kopf = minTeil > 0 ? koepfe.LastOrDefault(k => k.Id > 0 && k.Id < minTeil && !teileJeKopf.ContainsKey(k)) : null;
            if (kopf is null)
            {
                if (g.All(OhneBank)) { umbuchungsTeile += g.Count(); continue; } // gehört zu einer Umbuchungsvorlage
                e.Uebersprungen += g.Count();
                e.Hinweise.Add($"Teile einer Splitvorlage ohne erkennbaren Kopf: {string.Join(", ", g.Select(v => v.Bezeichnung))} - übergangen.");
                continue;
            }
            teileJeKopf[kopf] = g.OrderByDescending(v => Math.Abs(v.Betrag ?? 0)).ThenBy(v => v.Id).ToList();
        }
        if (umbuchungen.Count > 0)
        {
            e.Uebersprungen += umbuchungen.Count;
            e.Hinweise.Add($"{umbuchungen.Count} Umbuchungsvorlage(n) ohne Bankkonto nicht übernommen (für den Kontoauszug nicht anwendbar): " +
                           string.Join(", ", umbuchungen.Select(v => v.Bezeichnung)) + ".");
        }

        var neu = new List<KontierungsRegel>();
        foreach (var v in einzel)
        {
            var (regel, grund) = Einzel(v, d, s);
            if (regel is null) { e.Uebersprungen++; e.Hinweise.Add($"Vorlage \"{v.Bezeichnung}\": {grund} - nicht übernommen."); }
            else neu.Add(regel);
        }
        foreach (var kopf in koepfe)
        {
            if (!teileJeKopf.TryGetValue(kopf, out var tl) || tl.Count == 0)
            {
                e.Uebersprungen++;
                e.Hinweise.Add($"Splitvorlage \"{kopf.Bezeichnung}\": keine Teile gefunden - nicht übernommen.");
                continue;
            }
            var (regel, grund) = Split(kopf, tl, d, s);
            if (regel is null) { e.Uebersprungen++; e.Hinweise.Add($"Splitvorlage \"{kopf.Bezeichnung}\": {grund} - nicht übernommen."); }
            else neu.Add(regel);
        }

        // ------------------------------------------------------------- in die Regeln einarbeiten
        var neueIds = new HashSet<string>(neu.Select(r => r.TaxpoolId!));
        foreach (var r in neu.OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            int i = s.Regeln.FindIndex(x => x.TaxpoolId == r.TaxpoolId);
            if (i < 0) i = s.Regeln.FindIndex(x => x.TaxpoolId is null && x.Name == r.Name); // früher per CSV übernommen
            if (i >= 0)
            {
                var alt = s.Regeln[i];
                // Was der Anwender ergänzt hat, bleibt: Stichwort, Kategorie, Richtung (falls automatisch), Personenkonto-Suche
                r.Stichwort = alt.Stichwort;
                r.Kategorie = alt.Kategorie;
                if (alt.IstAutomatisch) r.Richtung = alt.Richtung;
                r.PersonenkontoSuchen = alt.PersonenkontoSuchen || r.PersonenkontoSuchen;
                s.Regeln[i] = r;
                e.Aktualisiert++;
            }
            else
            {
                s.Regeln.Add(r);
                e.Vorlagen++;
            }
        }

        // In Taxpool gelöschte oder ausgeblendete Vorlagen entfernen - außer der Anwender hat sie automatisch gemacht
        foreach (var weg in s.Regeln.Where(r => r.TaxpoolId is not null && !neueIds.Contains(r.TaxpoolId)).ToList())
        {
            if (weg.IstAutomatisch)
            {
                e.Hinweise.Add($"\"{weg.Name}\" gibt es in Taxpool nicht mehr (oder sie ist ausgeblendet) - bleibt, weil ein Stichwort hinterlegt ist.");
                continue;
            }
            s.Regeln.Remove(weg);
            e.Entfernt++;
        }

        if (ausgeblendet > 0) e.Hinweise.Add($"{ausgeblendet} in Taxpool ausgeblendete oder abgelaufene Vorlage(n) nicht übernommen.");
        return e;
    }

    // ---------------------------------------------------------------------------------------------

    private static (KontierungsRegel? Regel, string Grund) Einzel(TaxpoolVorlage v, TaxpoolDaten d, AccountingSettings s)
    {
        var (konto, richtung, grund) = Seite(v.SollKonto, v.HabenKonto, s);
        if (konto == 0) return (null, grund);

        var regel = Basis(v, richtung);
        if (s.IstPersonenkonto(konto) && (v.SollKonto == s.Bankkonto || v.HabenKonto == s.Bankkonto || v.SollKonto == 0 || v.HabenKonto == 0))
        {
            // Bank ↔ Personenkonto: Ausgleich offener Posten
            regel.Personenkonto = konto;
            regel.RechnungEinbuchen = false;
            return (regel, "");
        }
        if (s.IstPersonenkonto(v.SollKonto) || s.IstPersonenkonto(v.HabenKonto))
        {
            // Personenkonto ↔ Sachkonto: Beleg auf dem Personenkonto einbuchen
            regel.Personenkonto = s.IstPersonenkonto(v.SollKonto) ? v.SollKonto : v.HabenKonto;
            regel.Sachkonto = regel.Personenkonto == v.SollKonto ? v.HabenKonto : v.SollKonto;
            regel.Richtung = regel.Personenkonto == v.HabenKonto ? Richtung.Ausgang : Richtung.Eingang;
            regel.RechnungEinbuchen = true;
            regel.BuSchluessel = Bu(regel.Sachkonto, regel.Richtung == Richtung.Ausgang, v.SteuerCode, d);
            return (regel, "");
        }
        regel.Sachkonto = konto;
        regel.RechnungEinbuchen = false;
        regel.BuSchluessel = Bu(konto, richtung == Richtung.Ausgang, v.SteuerCode, d);
        return (regel, "");
    }

    private static (KontierungsRegel? Regel, string Grund) Split(TaxpoolVorlage kopf, List<TaxpoolVorlage> teile, TaxpoolDaten d, AccountingSettings s)
    {
        int bank = s.Bankkonto;
        int KopfKonto(int k) => k == bank ? 0 : k;
        int kopfSachkonto = KopfKonto(kopf.SollKonto) != 0 ? kopf.SollKonto : KopfKonto(kopf.HabenKonto);

        Richtung richtung;
        if (kopf.HabenKonto == bank) richtung = Richtung.Ausgang;
        else if (kopf.SollKonto == bank) richtung = Richtung.Eingang;
        else if (kopfSachkonto != 0) return (null, "Umbuchungsvorlage ohne Bankkonto");
        else
        {
            var t0 = teile[0];
            richtung = t0.SollKonto != 0 && t0.SollKonto != bank ? Richtung.Ausgang : Richtung.Eingang;
        }
        bool ausgabe = richtung == Richtung.Ausgang;

        var anteile = new List<RegelAnteil>();
        foreach (var t in teile)
        {
            int konto = ausgabe ? KopfKonto(t.SollKonto) : KopfKonto(t.HabenKonto);
            if (konto == 0) konto = KopfKonto(t.SollKonto) != 0 ? t.SollKonto : KopfKonto(t.HabenKonto);
            if (konto == 0) return (null, $"Teil \"{t.Bezeichnung}\" hat kein Sachkonto");
            anteile.Add(new RegelAnteil
            {
                Sachkonto = konto,
                Anteil = t.Betrag is { } b && b != 0 ? Math.Abs(b) : 1m,
                BuSchluessel = Bu(konto, ausgabe, t.SteuerCode, d),
                Text = t.Buchungstext.Length > 0 ? t.Buchungstext : t.Bezeichnung,
            });
        }

        var regel = Basis(kopf, richtung);
        regel.Sachkonto = anteile[0].Sachkonto;
        regel.BuSchluessel = anteile[0].BuSchluessel;
        regel.Aufteilung = anteile;
        regel.RechnungEinbuchen = false;
        return (regel, "");
    }

    /// <summary>Welche Seite trägt das Gegenkonto zur Bank? Liefert (Konto, Richtung) oder (0, Grund).</summary>
    private static (int Konto, Richtung Richtung, string Grund) Seite(int soll, int haben, AccountingSettings s)
    {
        int bank = s.Bankkonto;
        if (soll == bank && haben != 0 && haben != bank) return (haben, Richtung.Eingang, "");
        if (haben == bank && soll != 0 && soll != bank) return (soll, Richtung.Ausgang, "");
        if (soll != 0 && haben == 0) return (soll, Richtung.Ausgang, "");
        if (haben != 0 && soll == 0) return (haben, Richtung.Eingang, "");
        if (soll == 0 && haben == 0) return (0, Richtung.Alle, "kein Konto hinterlegt");
        if (s.IstPersonenkonto(soll) || s.IstPersonenkonto(haben))
            return (s.IstPersonenkonto(soll) ? soll : haben, s.IstPersonenkonto(haben) ? Richtung.Ausgang : Richtung.Eingang, "");
        return (0, Richtung.Alle, $"Umbuchung {soll} an {haben} ohne Bankkonto");
    }

    private static KontierungsRegel Basis(TaxpoolVorlage v, Richtung richtung) => new()
    {
        Name = Praefix + v.Bezeichnung,
        TaxpoolId = v.Guid.Length > 0 ? v.Guid : "id:" + v.Id,
        Richtung = richtung,
        // ohne eigenen Buchungstext: Bezeichnung nehmen, wenn sie auf "Re-Nr.:" o. Ä. endet (dann kommt die Nummer dahinter)
        Buchungstext = v.Buchungstext.Length > 0 ? v.Buchungstext : Rechnungsnummer.HatPlatzhalter(v.Bezeichnung) ? v.Bezeichnung : "",
    };

    /// <summary>BU-Schlüssel: Automatikkonto → keiner; sonst Steuersatz der Vorlage, ersatzweise des Kontos.</summary>
    public static string Bu(int konto, bool ausgabe, int vorlagenSteuer, TaxpoolDaten d)
    {
        d.Konten.TryGetValue(konto, out var k);
        if (k is { Automatik: true }) return "";
        int code = vorlagenSteuer >= 0 ? vorlagenSteuer : k?.SteuerCode ?? -1;
        if (code <= 0 || d.SteuerFuerCode(code) is not { } st) return "";
        return ausgabe ? st.SchluesselVSt : st.SchluesselUSt;
    }
}
