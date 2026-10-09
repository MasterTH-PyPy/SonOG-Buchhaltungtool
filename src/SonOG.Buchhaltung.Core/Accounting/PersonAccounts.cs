using System.Text.Json;
using System.Text.Json.Serialization;
using SonOG.Buchhaltung.Core.Matching;

namespace SonOG.Buchhaltung.Core.Accounting;

public enum PersonenArt
{
    Debitor,
    Kreditor,
}

/// <summary>Personenkonto (Kunde = Debitor, Lieferant = Kreditor), auf dem Belege ein- und Zahlungen ausgebucht werden.</summary>
public sealed class PersonAccount
{
    public int Konto { get; set; }
    public string Name { get; set; } = "";

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public PersonenArt Art { get; set; }

    /// <summary>Texte, an denen die Buchung im Kontoauszug erkannt wird (z. B. "AMAZON", "AMZN Mktp").</summary>
    public List<string> Suchbegriffe { get; set; } = new();

    /// <summary>Standard-Gegenkonto (Aufwand/Erlös), wenn keine Regel greift. 0 = keins.</summary>
    public int StandardSachkonto { get; set; }
    public string StandardBuSchluessel { get; set; } = "";

    public override string ToString() => $"{Konto} {Name}";
}

/// <summary>Verzeichnis der Personenkonten (personenkonten.json).</summary>
public sealed class PersonAccountDirectory
{
    public List<PersonAccount> Konten { get; set; } = new();

    public static PersonAccountDirectory Load(string path)
    {
        if (!File.Exists(path)) return new PersonAccountDirectory();
        return JsonSerializer.Deserialize<PersonAccountDirectory>(File.ReadAllText(path), AccountingSettings.Json) ?? new PersonAccountDirectory();
    }

    public void Save(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, JsonSerializer.Serialize(this, AccountingSettings.Json));
    }

    public PersonAccount? Get(int konto) => Konten.FirstOrDefault(k => k.Konto == konto);

    /// <summary>
    /// Personenkonto, dessen Suchbegriff im Buchungstext steht (längster Treffer gewinnt).
    /// Ohne Suchbegriff-Treffer: Kontoname als Wortbestandteil im Text.
    /// </summary>
    public PersonAccount? FindByText(string text, PersonenArt? art = null)
    {
        var candidates = Konten.Where(k => art is null || k.Art == art).ToList();
        var byTerm = candidates
            .SelectMany(k => k.Suchbegriffe.Where(s => s.Trim().Length > 0).Select(s => (Konto: k, Term: s.Trim())))
            .Where(x => text.Contains(x.Term, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(x => x.Term.Length)
            .FirstOrDefault();
        if (byTerm.Konto is not null) return byTerm.Konto;

        var byName = candidates.Where(k => k.Name.Trim().Length > 0 && NameMatcher.Score(k.Name, text) >= 0.99).ToList();
        return byName.Count == 1 ? byName[0] : null;
    }

    /// <summary>Debitor zu einem Namen (Rechnungsempfänger oder Zahler). Nur eindeutige, gute Treffer.</summary>
    public PersonAccount? FindDebitor(params string[] names)
    {
        foreach (var name in names.Where(n => !string.IsNullOrWhiteSpace(n)))
        {
            var scored = Konten.Where(k => k.Art == PersonenArt.Debitor)
                .Select(k => (Konto: k, Score: Math.Max(
                    NameMatcher.Score(k.Name, name),
                    k.Suchbegriffe.Count == 0 ? 0 : k.Suchbegriffe.Max(s => s.Trim().Length > 0 && name.Contains(s.Trim(), StringComparison.OrdinalIgnoreCase) ? 1.0 : 0.0))))
                .Where(x => x.Score >= 0.6)
                .OrderByDescending(x => x.Score)
                .ToList();
            if (scored.Count == 0) continue;
            if (scored.Count == 1 || scored[0].Score > scored[1].Score) return scored[0].Konto;
        }
        return null;
    }

    /// <summary>Nächste freie Kontonummer im Nummernkreis.</summary>
    public int NextFree(PersonenArt art, AccountingSettings s)
    {
        int von = art == PersonenArt.Debitor ? s.DebitorenVon : s.KreditorenVon;
        int bis = art == PersonenArt.Debitor ? s.DebitorenBis : s.KreditorenBis;
        var used = Konten.Where(k => k.Konto >= von && k.Konto <= bis).Select(k => k.Konto).ToList();
        int next = used.Count == 0 ? von : used.Max() + 1;
        if (next > bis)
        {
            next = Enumerable.Range(von, bis - von + 1).FirstOrDefault(n => !used.Contains(n));
            if (next == 0) throw new InvalidOperationException($"Der Nummernkreis {von}-{bis} ist voll.");
        }
        return next;
    }

    /// <summary>Legt ein Konto an oder aktualisiert Name/Art eines vorhandenen.</summary>
    public PersonAccount Upsert(int konto, string name, PersonenArt art)
    {
        var existing = Get(konto);
        if (existing is not null)
        {
            if (name.Trim().Length > 0) existing.Name = name.Trim();
            existing.Art = art;
            return existing;
        }
        var acc = new PersonAccount { Konto = konto, Name = name.Trim(), Art = art };
        Konten.Add(acc);
        Konten.Sort((a, b) => a.Konto.CompareTo(b.Konto));
        return acc;
    }
}
