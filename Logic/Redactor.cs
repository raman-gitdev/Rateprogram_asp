using System;
using System.Linq;
using System.Text.RegularExpressions;

namespace TariffHub.Logic;

/// <summary>
/// Withholds any displayed value that contains a real party name. The names themselves must never be committed,
/// so they come from the TariffHub.RedactTerms setting in Secrets.config (comma-separated, case-insensitive,
/// matched as whole words where letters are the word characters: "_" and "-" count as separators).
/// </summary>
public sealed class Redactor
{
    public const string Placeholder = "[withheld]";

    private readonly Regex? _pattern;

    public Redactor(string? termsCsv)
    {
        var terms = (termsCsv ?? "").Split(',').Select(t => t.Trim()).Where(t => t.Length > 0).ToArray();
        if (terms.Length > 0)
            _pattern = new Regex("(?<![A-Za-z])(" + string.Join("|", terms.Select(Regex.Escape)) + ")(?![A-Za-z])",
                                 RegexOptions.IgnoreCase | RegexOptions.Compiled);
    }

    public bool IsConfigured => _pattern is not null;

    public bool Hits(string? value) => value is not null && _pattern is not null && _pattern.IsMatch(value);

    /// <summary>Returns the value, or the placeholder (and counts it) when it contains a redacted term.</summary>
    public string? Mask(string? value, ref int withheld)
    {
        if (!Hits(value)) return value;
        withheld++;
        return Placeholder;
    }
}
