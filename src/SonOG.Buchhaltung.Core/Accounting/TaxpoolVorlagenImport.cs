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

        // ------------------------------------------------------------- Personenkonten
        foreach (var (konto, name) in d.Personen)
        {
            if (!s.IstPersonenkonto(konto))
            {
                e.Hinweise.Add($"Personenkonto {konto} {name}: liegt außerhalb der Nummernkreise in buchhaltung.json - übergangen.");
                continue;
            }
            var art = konto >= s.DebitorenVon && konto <= s.DebitorenBis ? PersonenArt.Debitor : PersonenArt.Kreditor;
            dir.Upsert(konto, name, art);
            e.Personenkonten++;
        }

        // ------------------------------------------------------------- Vorlagen
        var sichtbar = d.Vorlagen.Where(v => v.Anzeigen && (v.BisJahr <= 0 || v.BisJahr >= aktJahr)).ToList();
        int ausgeblendet = d.Vorlagen.Count - sichtbar.Count;

        var teile = sichtbar.Where(v => v.IstSplit && v.ParentId >= 0).ToList();
        var koepfe = sichtbar.Where(v => v.IstSplit && v.ParentId < 0).OrderBy(v => v.Id).ToList();
        var einzel = sichtbar.Where(v => !v.IstSplit).ToList();

        // Teile einer Splitvorlage: gleiche ParentID; der Kopf ist die Splitvorlage mit der höchsten ID unterhalb der Teile
        var gruppen = teile.GroupBy(v => v.ParentId).ToList();
        var teileJeKopf = new Dictionary<TaxpoolVorlage, List<TaxpoolVorlage>>();
        foreach (var g in gruppen)
        {
            int minTeil = g.Min(v => v.Id);
            var kopf = koepfe.LastOrDefault(k => k.Id < minTeil && !teileJeKopf.ContainsKey(k));
            if (kopf is null)
            {
                e.Uebersprungen += g.Count();
                e.Hinweise.Add($"Splitvorlage ohne Kopf: {string.Join(", ", g.Select(v => v.Bezeichnung))} - übergangen.");
                continue;
            }
            teileJeKopf[kopf] = g.OrderByDescending(v => Math.Abs(v.Betrag ?? 0)).ThenBy(v => v.Id).ToList();
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
        Buchungstext = v.Buchungstext,
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
