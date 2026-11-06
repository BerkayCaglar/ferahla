using System.Globalization;

namespace Ferahla.Core.Text;

public static class Fmt
{
    /// <summary>Human size in the UI culture: "12,6 GB" (tr) / "12.6 GB" (en). Binary units, shown as KB/MB/GB.</summary>
    public static string Size(long bytes, CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentUICulture;
        var neg = bytes < 0;
        double b = Math.Abs(bytes);
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var u = 0;
        while (b >= 1024 && u < units.Length - 1) { b /= 1024; u++; }
        var s = u == 0 ? b.ToString("0", culture) : b.ToString(b >= 100 ? "0" : "0.0", culture);
        return (neg ? "-" : "") + s + " " + units[u];
    }

    public static string SignedSize(long bytes, CultureInfo? culture = null) =>
        (bytes > 0 ? "+" : "") + Size(bytes, culture);

    public static string Count(long n, CultureInfo? culture = null) =>
        n.ToString("N0", culture ?? CultureInfo.CurrentUICulture);

    public static string Percent(double fraction, CultureInfo? culture = null) =>
        (fraction * 100).ToString("0.#", culture ?? CultureInfo.CurrentUICulture) + "%";
}
