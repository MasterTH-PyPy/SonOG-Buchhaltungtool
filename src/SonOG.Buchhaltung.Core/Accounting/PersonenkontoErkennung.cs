using SonOG.Buchhaltung.Core.Matching;

namespace SonOG.Buchhaltung.Core.Accounting;

/// <summary>Ergebnis der Personenkonto-Erkennung mit Begründung (wird als Hinweis angezeigt).</summary>
public sealed record PersonTreffer(PersonAccount Konto, string Grund);

/// <summary>
/// Findet das Personenkonto zu einem Buchungstext.
/// <list type="number">
/// <item>Suchbegriffe der Personenkonten (z. B. "AMAZON") - stärkster Hinweis.</item>
/// <item>Alle Namensteile stehen im Text ("Toom Baumarkt GmbH" in "TOOM BAUMARKT SAGT DANKE").</item>
/// <item>Das erste markante Wort des Namens steht als ganzes Wort im Text ("TOOM", "ADN"). Allgemeine Wörter (Baumarkt,
/// Handel, Technik ...) und Wörter, die in mehreren Kontennamen vorkommen, zählen dabei nicht. Bei Kunden mit Vor- und
/// Nachname muss der Nachname passen, damit nicht jeder "Michael" trifft.</item>
/// </list>
/// Richtung: Bei Abbuchungen gewinnen bei gleicher Güte die Lieferanten (Kreditoren), bei Eingängen die Kunden
/// (Debitoren). Ein klar besserer Treffer im anderen Kreis gewinnt trotzdem (z. B. Amazon-Erstattung als Eingang).
/// Gleich gute Treffer im selben Kreis gelten als mehrdeutig - dann wird nichts vorgeschlagen.
/// </summary>
public sealed class PersonenkontoErkennung
{
    /// <summary>Wörter, die in Firmennamen häufig sind und allein kein Konto erkennen lassen.</summary>
    private static readonly HashSet<string> Allgemein = new(StringComparer.Ordinal)
    {
        "baumarkt", "markt", "handel", "handels", "handelsgesellschaft", "vertrieb", "vertriebs", "service", "services", "solutions",
        "technik", "technology", "technologies", "tech", "systems", "system", "software", "computer", "computers", "online", "shop", "store",
        "deutschland", "germany", "german", "europe", "european", "international", "digital", "group", "gruppe", "holding", "versicherung",
        "versicherungen", "bank", "sparkasse", "elektro", "elektronik", "electronic", "electronics", "stadt", "verlag", "media", "consulting",
        "energie", "netze", "netz", "gesellschaft", "company", "inc", "sarl", "sas", "llc", "limited", "verwaltung", "zentrale", "filiale",
        "partner", "beratung", "kfz", "auto", "bau", "fachhandel", "grosshandel", "einkauf", "network", "networks", "services", "dienstleistungen",
        "stiftung", "verein", "freundeskreis", "kirche", "schule", "debitor", "kreditor", "kunde", "lieferant", "standard", "paypal",
    };

    /// <summary>Wörter, an denen eine Firma/ein Verein erkannt wird (dann gilt die Nachnamen-Regel für Kunden nicht).</summary>
    private static readonly HashSet<string> Rechtsform = new(StringComparer.Ordinal)
    {
        "gmbh", "mbh", "ug", "ag", "kg", "ohg", "gbr", "ev", "eg", "se", "inh", "inhaber", "co", "ltd", "verein", "schule", "verwaltung",
        "hausverwaltung", "gesellschaft", "stiftung", "werk", "verlag", "praxis", "kanzlei", "seminare", "bv", "sarl", "llc", "inc",
    };

    private readonly List<(PersonAccount Konto, List<string> Tokens)> _konten;
    private readonly Dictionary<PersonAccount, (List<string> Kurz, bool Person, string Norm)> _info = new();
    private readonly Dictionary<string, int> _haeufigkeit;

    public PersonenkontoErkennung(IEnumerable<PersonAccount> konten)
    {
        _konten = konten.Where(k => k.Name.Trim().Length > 0 || k.Suchbegriffe.Count > 0)
            .Select(k => (k, NameMatcher.Tokens(k.Name).Distinct().ToList()))
            .ToList();
        _haeufigkeit = _konten.SelectMany(k => k.Tokens).GroupBy(t => t).ToDictionary(g => g.Key, g => g.Count());
        foreach (var (k, tokens) in _konten)
        {
            var woerter = NameMatcher.Normalize(k.Name).Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var kurz = woerter.Where(w => w.Length == 2 && !Rechtsform.Contains(w)).Distinct().ToList();
            bool person = tokens.Count == 2 && !woerter.Any(Rechtsform.Contains) && !woerter.Any(w => w.Any(char.IsDigit));
            _info[k] = (kurz, person, string.Join(" ", tokens));
        }
    }

    /// <param name="betrag">Vorzeichen bestimmt den bevorzugten Kreis: negativ = Lieferant, positiv = Kunde, 0 = keiner.</param>
    public PersonTreffer? Finden(string text, decimal betrag = 0)
    {
        if (string.IsNullOrWhiteSpace(text) || _konten.Count == 0) return null;
        PersonenArt? bevorzugt = betrag < 0 ? PersonenArt.Kreditor : betrag > 0 ? PersonenArt.Debitor : null;
        var textTokens = NameMatcher.Tokens(text);
        var textWoerter = new HashSet<string>(NameMatcher.Normalize(text).Split(' ', StringSplitOptions.RemoveEmptyEntries));
        var kandidaten = new List<(PersonAccount Konto, int Guete, string Grund)>();

        foreach (var (konto, tokens) in _konten)
        {
            int guete = 0;
            string grund = "";

            // 1. Suchbegriff
            foreach (var s in konto.Suchbegriffe.Select(x => x.Trim()).Where(x => x.Length > 0))
            {
                if (!text.Contains(s, StringComparison.OrdinalIgnoreCase)) continue;
                int g = 1000 + s.Length;
                if (g > guete) { guete = g; grund = $"Suchbegriff \"{s}\""; }
            }

            // 2./3. Name
            if (guete == 0 && tokens.Count > 0)
            {
                var gewicht = tokens.ToDictionary(t => t, t => Gewicht(t, textTokens));
                var treffer = tokens.Where(t => gewicht[t] > 0).ToList();
                if (treffer.Count == tokens.Count)
                {
                    // ganze Wörter zählen doppelt, Wortanfänge (gekürzte Namen im Auszug) einfach
                    guete = 500 + 10 * gewicht.Values.Sum() + tokens.Where(t => gewicht[t] == 2).Sum(t => t.Length);
                    // Kürzel aus zwei Buchstaben ("HP-Hausverwaltung" / "EH-Hausverwaltung") entscheiden mit
                    var kurz = _info[konto].Kurz;
                    guete += 5 * kurz.Count(textWoerter.Contains) - 5 * kurz.Count(w => !textWoerter.Contains(w));
                    grund = $"Name \"{konto.Name}\"";
                }
                else if (treffer.Count > 0)
                {
                    // Nur das erste markante Wort des Namens zählt allein (Marke/Firmenkern: "Toom", "ADN", "IKEA"),
                    // nicht ein Vorname weiter hinten ("Der Dübelshop Inh. Andreas Sommer" darf nicht auf "Andreas" anspringen)
                    var erstes = tokens.FirstOrDefault(t => IstMarkant(t, tokens));
                    var markant = treffer.Where(t => t == erstes || (treffer.Count >= 2 && IstMarkant(t, tokens))).ToList();
                    bool zulaessig = markant.Count > 0;
                    // Privatpersonen (Vor- und Nachname, keine Rechtsform): Nachname muss passen oder beide Namensteile
                    if (zulaessig && _info[konto].Person)
                        zulaessig = treffer.Count >= 2 || treffer.Contains(tokens[^1]);
                    if (zulaessig)
                    {
                        guete = 200 + 10 * treffer.Sum(t => gewicht[t]) + markant.Where(t => gewicht[t] == 2).Sum(t => t.Length);
                        grund = $"Namensteil \"{string.Join(" ", markant).ToUpperInvariant()}\" von \"{konto.Name}\"";
                    }
                }
            }

            if (guete == 0) continue;
            if (bevorzugt is not null && konto.Art == bevorzugt) guete += 50;
            kandidaten.Add((konto, guete, grund));
        }

        if (kandidaten.Count == 0) return null;
        var sortiert = kandidaten.OrderByDescending(k => k.Guete).ThenByDescending(k => k.Konto.Nutzung).ToList();
        var best = sortiert[0];
        if (sortiert.Count > 1 && sortiert[1].Guete == best.Guete && sortiert[1].Konto.Konto != best.Konto.Konto)
        {
            // Gleichnamige Konten (z. B. doppelt angelegt): das zuletzt häufiger bebuchte gewinnt, sonst mehrdeutig
            var zweit = sortiert[1];
            if (_info[best.Konto].Norm == _info[zweit.Konto].Norm && best.Konto.Nutzung > zweit.Konto.Nutzung)
                return new PersonTreffer(best.Konto, best.Grund + $" (gleichnamig mit {zweit.Konto.Konto}, häufiger genutzt)");
            return null;
        }
        return new PersonTreffer(best.Konto, best.Grund);
    }

    /// <summary>Kandidaten zur Anzeige (z. B. wenn mehrdeutig), beste zuerst.</summary>
    public IReadOnlyList<PersonAccount> Kandidaten(string text, decimal betrag = 0, int max = 5)
    {
        var textTokens = NameMatcher.Tokens(text);
        PersonenArt? bevorzugt = betrag < 0 ? PersonenArt.Kreditor : betrag > 0 ? PersonenArt.Debitor : null;
        return _konten
            .Select(k => (k.Konto, Treffer: k.Tokens.Count(t => NameMatcher.ContainsToken(textTokens, t) && IstMarkant(t, k.Tokens))))
            .Where(x => x.Treffer > 0)
            .OrderByDescending(x => x.Treffer).ThenByDescending(x => x.Konto.Art == bevorzugt)
            .Take(max).Select(x => x.Konto).ToList();
    }

    /// <summary>
    /// 2 = Wort steht so im Text (oder mit einem Tippfehler ab 5 Zeichen), 1 = nur als Wortanfang (z. B. "DISTRIB" für
    /// "Distribution" oder "TOOMBAUMARKT" für "Toom"), 0 = nicht enthalten. Kurze Wortanfänge zählen nicht, damit
    /// "Lohn" nicht auf "Lohncompany" passt.
    /// </summary>
    private static int Gewicht(string nameToken, IReadOnlyList<string> textTokens)
    {
        int best = 0;
        foreach (var t in textTokens)
        {
            if (t == nameToken) return 2;
            if (nameToken.Length >= 5 && Math.Abs(t.Length - nameToken.Length) <= 1 && NameMatcher.ContainsToken(new[] { t }, nameToken)
                && !t.StartsWith(nameToken, StringComparison.Ordinal) && !nameToken.StartsWith(t, StringComparison.Ordinal))
                return 2; // ein Tippfehler
            if (nameToken.StartsWith(t, StringComparison.Ordinal) && t.Length >= 5 && t.Length * 10 >= nameToken.Length * 6) best = 1;      // gekürzt
            else if (t.StartsWith(nameToken, StringComparison.Ordinal) && nameToken.Length >= 4) best = 1;                                   // zusammengeschrieben
        }
        return best;
    }

    /// <summary>Kein allgemeines Wort, in höchstens zwei Kontennamen; 3 Zeichen nur als erstes Wort (z. B. "ADN", "ATU").</summary>
    private bool IstMarkant(string token, List<string> nameTokens)
    {
        if (Allgemein.Contains(token)) return false;
        if (_haeufigkeit.TryGetValue(token, out var n) && n > 2) return false;
        if (token.Length < 3) return false;
        if (token.Length == 3) return nameTokens.Count > 0 && nameTokens[0] == token;
        return true;
    }
}
