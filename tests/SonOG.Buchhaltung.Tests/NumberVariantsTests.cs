using SonOG.Buchhaltung.Core.Matching;

namespace SonOG.Buchhaltung.Tests;

public class NumberVariantsTests
{
    [Fact]
    public void Adjacent_swaps_come_before_other_swaps()
    {
        var list = NumberVariants.Of("202634659").ToList();
        Assert.Contains("202634695", list);
        Assert.True(list.IndexOf("202634695") < list.IndexOf("602234659"));
    }

    [Fact]
    public void Single_wrong_digit_is_found()
        => Assert.Contains("202634658", NumberVariants.Of("202634659").ToList());

    [Fact]
    public void Variants_never_contain_original_or_duplicates()
    {
        var list = NumberVariants.Of("202634659").ToList();
        Assert.DoesNotContain("202634659", list);
        Assert.Equal(list.Count, list.Distinct().Count());
    }

    [Theory]
    [InlineData(45.90, 54.90, true)]
    [InlineData(1234.56, 1234.65, true)]
    [InlineData(100.00, 100.00, false)]
    [InlineData(45.90, 55.90, false)]
    [InlineData(10.50, 15.60, false)]
    [InlineData(10.50, 15.00, true)]
    public void Amount_transposition_detection(double a, double b, bool expected)
        => Assert.Equal(expected, NumberVariants.AmountsDifferByTransposition((decimal)a, (decimal)b));
}
