using System.Globalization;

namespace Ferahla.Core.Text;

/// <summary>UI language: Turkish if Windows' UI language is Turkish, otherwise English. Override with FERAHLA_LANG=en|tr.</summary>
public static class Lang
{
    public static string Current { get; set; } = Detect();

    public static bool IsTurkish => Current == "tr";

    public static string Detect()
    {
        var env = Environment.GetEnvironmentVariable("FERAHLA_LANG");
        if (env is "en" or "tr") return env;
        return CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "tr" ? "tr" : "en";
    }

    /// <summary>Pick the string for the current language.</summary>
    public static string T(string en, string tr) => IsTurkish ? tr : en;

    /// <summary>Culture used for number formatting in human output.</summary>
    public static CultureInfo Culture => IsTurkish ? CultureInfo.GetCultureInfo("tr-TR") : CultureInfo.GetCultureInfo("en-US");
}
