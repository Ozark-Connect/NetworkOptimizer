namespace NetworkOptimizer.Web.Services;

/// <summary>Country flags and names for a GeoLite2 country code, shared by the pages that show where an address is.</summary>
public static class CountryDisplay
{
    /// <summary>The flag for a two-letter country code, as its regional indicator pair. Empty for anything else.</summary>
    public static string CountryFlag(string? countryCode)
    {
        if (string.IsNullOrEmpty(countryCode) || countryCode.Length != 2) return "";
        var upper = countryCode.ToUpperInvariant();
        return string.Concat(
            char.ConvertFromUtf32(0x1F1E6 + (upper[0] - 'A')),
            char.ConvertFromUtf32(0x1F1E6 + (upper[1] - 'A')));
    }

    /// <summary>English country names by two-letter code.</summary>
    public static readonly Dictionary<string, string> CountryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["AF"] = "Afghanistan", ["AL"] = "Albania", ["DZ"] = "Algeria", ["AR"] = "Argentina",
        ["AM"] = "Armenia", ["AU"] = "Australia", ["AT"] = "Austria", ["AZ"] = "Azerbaijan",
        ["BD"] = "Bangladesh", ["BY"] = "Belarus", ["BE"] = "Belgium", ["BA"] = "Bosnia and Herzegovina",
        ["BR"] = "Brazil", ["BG"] = "Bulgaria", ["KH"] = "Cambodia", ["CA"] = "Canada",
        ["CL"] = "Chile", ["CN"] = "China", ["CO"] = "Colombia", ["CR"] = "Costa Rica",
        ["HR"] = "Croatia", ["CU"] = "Cuba", ["CY"] = "Cyprus", ["CZ"] = "Czechia",
        ["DK"] = "Denmark", ["EC"] = "Ecuador", ["EG"] = "Egypt", ["EE"] = "Estonia",
        ["ET"] = "Ethiopia", ["FI"] = "Finland", ["FR"] = "France", ["GE"] = "Georgia",
        ["DE"] = "Germany", ["GH"] = "Ghana", ["GR"] = "Greece", ["HK"] = "Hong Kong",
        ["HU"] = "Hungary", ["IS"] = "Iceland", ["IN"] = "India", ["ID"] = "Indonesia",
        ["IR"] = "Iran", ["IQ"] = "Iraq", ["IE"] = "Ireland", ["IL"] = "Israel",
        ["IT"] = "Italy", ["JP"] = "Japan", ["JO"] = "Jordan", ["KZ"] = "Kazakhstan",
        ["KE"] = "Kenya", ["KP"] = "North Korea", ["KR"] = "South Korea", ["KW"] = "Kuwait",
        ["LV"] = "Latvia", ["LB"] = "Lebanon", ["LT"] = "Lithuania", ["LU"] = "Luxembourg",
        ["MY"] = "Malaysia", ["MX"] = "Mexico", ["MD"] = "Moldova", ["MN"] = "Mongolia",
        ["MA"] = "Morocco", ["MM"] = "Myanmar", ["NL"] = "Netherlands", ["NZ"] = "New Zealand",
        ["NG"] = "Nigeria", ["NO"] = "Norway", ["PK"] = "Pakistan", ["PA"] = "Panama",
        ["PE"] = "Peru", ["PH"] = "Philippines", ["PL"] = "Poland", ["PT"] = "Portugal",
        ["QA"] = "Qatar", ["RO"] = "Romania", ["RU"] = "Russia", ["SA"] = "Saudi Arabia",
        ["RS"] = "Serbia", ["SG"] = "Singapore", ["SK"] = "Slovakia", ["SI"] = "Slovenia",
        ["ZA"] = "South Africa", ["ES"] = "Spain", ["LK"] = "Sri Lanka", ["SE"] = "Sweden",
        ["CH"] = "Switzerland", ["TW"] = "Taiwan", ["TH"] = "Thailand", ["TR"] = "Turkey",
        ["UA"] = "Ukraine", ["AE"] = "UAE", ["GB"] = "United Kingdom", ["US"] = "United States",
        ["UY"] = "Uruguay", ["UZ"] = "Uzbekistan", ["VE"] = "Venezuela", ["VN"] = "Vietnam",
    };

    public static string CountryLabel(string? code)
    {
        if (string.IsNullOrEmpty(code)) return "-";
        var flag = CountryFlag(code);
        var name = CountryNames.TryGetValue(code, out var n) ? n : code.ToUpperInvariant();
        return flag;
    }

    /// <summary>"US - United States" for a tooltip; the bare code when the name is unknown.</summary>
    public static string CountryTooltip(string? code)
    {
        if (string.IsNullOrEmpty(code)) return "";
        var name = CountryNames.TryGetValue(code, out var n) ? n : code.ToUpperInvariant();
        return $"{code.ToUpperInvariant()} - {name}";
    }
}
