using System.Globalization;

namespace SonOG.Buchhaltung.Core.Util;

/// <summary>Formatierung unabhängig von installierten Kulturen (deutsches Zahlenformat).</summary>
public static class Fmt
{
    public static string Money(decimal value)
    {
        var s = value.ToString("#,##0.00", CultureInfo.InvariantCulture);
        return s.Replace(",", "\0").Replace(".", ",").Replace("\0", ".");
    }

    public static decimal ParseGerman(string s) =>
        decimal.Parse(s.Replace(".", "").Replace(",", "."), NumberStyles.Number | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
}
