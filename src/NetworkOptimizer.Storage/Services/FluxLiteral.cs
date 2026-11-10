using System.Globalization;
using System.Text;

namespace NetworkOptimizer.Storage.Services;

/// <summary>
/// Flux string literals for values placed in a query. InfluxDB OSS 2.x has no query parameters, so
/// every string value is escaped into one literal: a quote, backslash, newline or <c>${</c> in a tag
/// value or field name can then never end the literal or open an interpolation.
/// </summary>
internal static class FluxLiteral
{
    /// <summary>
    /// The value as a quoted Flux string literal. Escapes per the Flux spec: <c>\\</c>, <c>\"</c>,
    /// <c>\n</c>, <c>\r</c>, <c>\t</c>, and <c>\${</c>; other control characters as <c>\xNN</c>.
    /// A lone <c>$</c> is literal text and is left as is (<c>\$</c> alone is not a Flux escape).
    /// Ordinary values (letters, digits, <c>. - _ : /</c>, space) come back unchanged inside the quotes.
    /// </summary>
    public static string Quote(string? value)
    {
        value ??= "";
        StringBuilder? sb = null;
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            string? escape = c switch
            {
                '\\' => @"\\",
                '"' => "\\\"",
                '\n' => @"\n",
                '\r' => @"\r",
                '\t' => @"\t",
                '$' when i + 1 < value.Length && value[i + 1] == '{' => @"\$",
                < ' ' or '\x7f' => @"\x" + ((int)c).ToString("x2", CultureInfo.InvariantCulture),
                _ => null,
            };
            if (escape == null)
            {
                sb?.Append(c);
                continue;
            }
            sb ??= new StringBuilder(value.Length + 8).Append(value, 0, i);
            sb.Append(escape);
        }
        return "\"" + (sb?.ToString() ?? value) + "\"";
    }
}
