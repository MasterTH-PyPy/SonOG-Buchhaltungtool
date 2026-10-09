using SonOG.Buchhaltung.Core.Rules;

namespace SonOG.Buchhaltung.Tests;

public class AppRulesTests
{
    private static string TempFile() => Path.Combine(Path.GetTempPath(), "sonog-rules-" + Guid.NewGuid().ToString("N") + ".json");

    [Fact]
    public void Default_file_is_created_on_first_load_with_readable_names()
    {
        var path = TempFile();
        try
        {
            var rules = AppRules.Load(path);
            var json = File.ReadAllText(path);

            Assert.Contains("\"belegloseStichwoerter\"", json);
            Assert.Contains("\"stichwort\": \"FINANZAMT\"", json);
            Assert.Contains("Entgeltabrechnung", json);
            Assert.Equal("{year}-{n:0000}", rules.NummernFormat);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Edited_rules_are_read_back()
    {
        var path = TempFile();
        try
        {
            var rules = AppRules.Load(path);
            rules.BelegloseStichwoerter.Add(new KeywordRule { Stichwort = "AUTO LEASING", Grund = "Leasing privat" });
            rules.Rechnung.Empfaenger.X1 = 400;
            rules.Save(path);

            var loaded = AppRules.Load(path);
            Assert.Equal(2, loaded.BelegloseStichwoerter.Count);
            Assert.Equal("Leasing privat", loaded.BelegloseStichwoerter[1].Grund);
            Assert.Equal(400, loaded.Rechnung.Empfaenger.X1);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Hand_written_file_with_other_casing_is_accepted()
    {
        var path = TempFile();
        try
        {
            File.WriteAllText(path, "{ \"NummernFormat\": \"B{n:000}\", \"BelegloseStichwoerter\": [ { \"Stichwort\": \"X\", \"Grund\": \"y\" } ] }");
            var rules = AppRules.Load(path);
            Assert.Equal("B{n:000}", rules.NummernFormat);
            Assert.Equal("X", rules.BelegloseStichwoerter[0].Stichwort);
        }
        finally { File.Delete(path); }
    }
}
