using SonOG.Buchhaltung.Core.Matching;

namespace SonOG.Buchhaltung.Tests;

public class NameMatcherTests
{
    [Theory]
    [InlineData("Erika Beispiel", "Erika Beispiel\nMusterweg 1\n64319 Musterstadt", true)]
    [InlineData("Beispiel Erika", "Frau Erika Beispiel\nMusterweg 1", true)]
    [InlineData("Mueller GmbH", "Müller GmbH\nHauptstr. 5", true)]
    [InlineData("Max Mustermann", "Erika Beispiel\nMusterweg 1", false)]
    [InlineData("Muster Immobilien GmbH", "Muster Immobilien GmbH & Co. KG\nWeg 3", true)]
    [InlineData("", "Erika Beispiel", false)]
    public void Score_decides_whether_payer_fits_recipient(string payer, string recipient, bool expected)
        => Assert.Equal(expected, NameMatcher.Score(payer, recipient) >= 0.5);

    [Fact]
    public void Umlauts_and_accents_are_normalized()
        => Assert.Equal("mueller strasse", NameMatcher.Normalize("Müller-Straße"));

    [Fact]
    public void Small_typos_are_tolerated_for_long_names()
        => Assert.True(NameMatcher.Score("Beispiell", "Beispiel") >= 0.5);

    [Fact]
    public void ContainsAnyToken_finds_customer_name_in_text()
    {
        Assert.True(NameMatcher.ContainsAnyToken("Zahlung von Beispiel Erika Rechnung", "Erika Beispiel"));
        Assert.False(NameMatcher.ContainsAnyToken("Zahlung Stadtwerke", "Erika Beispiel"));
    }
}
