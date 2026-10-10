using SonOG.Buchhaltung.Core.Matching;
using SonOG.Buchhaltung.Core.Models;

namespace SonOG.Buchhaltung.Tests;

public class ManualAssignmentsTests
{
    private static string TempFile()
    {
        var p = Path.Combine(Path.GetTempPath(), "sonog-test-" + Guid.NewGuid().ToString("N") + ".pdf");
        File.WriteAllText(p, "x");
        return p;
    }

    [Fact]
    public void Identical_bookings_get_different_keys()
    {
        var a = Fx.Classify("Lastschrift", -10.00m, "Musterfirma GmbH");
        var b = Fx.Classify("Lastschrift", -10.00m, "Musterfirma  GmbH");
        var c = Fx.Classify("Lastschrift", -11.00m, "Musterfirma GmbH");
        var keys = ManualAssignments.Keys(new[] { a, b, c });
        Assert.Equal(3, keys.Values.Distinct().Count());
    }

    [Fact]
    public void Manual_receipt_survives_a_fresh_matching()
    {
        var file = TempFile();
        try
        {
            var before = Fx.Classify("Lastschrift", -10.00m, "Musterfirma GmbH");
            var keysBefore = ManualAssignments.Keys(new[] { before });
            before.ReceiptFiles.Add(file);
            var m = new ManualAssignments();
            m.Record(keysBefore[before], before);

            // "Dokumente neu einlesen": frische Buchung, automatischer Abgleich hat nichts gefunden
            var after = Fx.Classify("Lastschrift", -10.00m, "Musterfirma GmbH");
            var applied = m.Apply(ManualAssignments.Keys(new[] { after }));

            Assert.Equal(1, applied);
            Assert.Equal(new[] { file }, after.ReceiptFiles.ToArray());
            Assert.Contains("Manuell", after.ReceiptNote);
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void Manually_removed_receipt_stays_removed()
    {
        var file = TempFile();
        try
        {
            var b = Fx.Classify("Lastschrift", -10.00m, "Musterfirma GmbH");
            var keys = ManualAssignments.Keys(new[] { b });
            var m = new ManualAssignments();
            m.Record(keys[b], b); // Belegliste leer = bewusst entfernt

            var again = Fx.Classify("Lastschrift", -10.00m, "Musterfirma GmbH");
            again.ReceiptFiles.Add(file); // der automatische Abgleich würde ihn wieder zuordnen
            m.Apply(ManualAssignments.Keys(new[] { again }));

            Assert.Empty(again.ReceiptFiles);
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void Missing_manual_file_is_reported()
    {
        var b = Fx.Classify("Lastschrift", -10.00m, "Musterfirma GmbH");
        var keys = ManualAssignments.Keys(new[] { b });
        b.ReceiptFiles.Add("Z:/gibt-es-nicht.pdf");
        var m = new ManualAssignments();
        m.Record(keys[b], b);

        var again = Fx.Classify("Lastschrift", -10.00m, "Musterfirma GmbH");
        m.Apply(ManualAssignments.Keys(new[] { again }));

        Assert.Contains("Datei fehlt", again.ReceiptNote);
    }

    [Fact]
    public void Assignments_are_saved_and_loaded()
    {
        var path = Path.Combine(Path.GetTempPath(), "sonog-test-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var b = Fx.Classify("Lastschrift", -10.00m, "Musterfirma GmbH");
            var keys = ManualAssignments.Keys(new[] { b });
            b.ReceiptFiles.Add("a.pdf");
            var m = new ManualAssignments();
            m.Record(keys[b], b);
            m.Save(path);

            var loaded = ManualAssignments.Load(path);
            Assert.Equal(new[] { "a.pdf" }, loaded.Entries[keys[b]].Files.ToArray());
        }
        finally { File.Delete(path); }
    }
}
