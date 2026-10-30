using System.Globalization;
using DropWheel.Core.Conversion;

namespace DropWheel.Core.Wheel;

/// <summary>
/// One option a built-in item takes from a recipe step's <c>options</c>, such as compress-image's quality. A value
/// arrives as the string the manifest reader kept; <see cref="Accepts"/> is what the catalog checks it with and
/// <see cref="RequestOptions"/> is how the item reads it. Either a whole-number range or a fixed set of values.
/// </summary>
public sealed record OptionSpec(string Name, string Description, int? Min, int? Max, IReadOnlyList<string>? Values)
{
    public static OptionSpec Range(string name, string description, int min, int max) => new(name, description, min, max, null);

    public static OptionSpec OneOf(string name, string description, params string[] values) => new(name, description, null, null, values);

    /// <summary>What a value may be, for a problem message: "1..100" or "90 / 180 / 270".</summary>
    public string Expected => Values is not null ? string.Join(" / ", Values) : $"{Min}..{Max}";

    public bool Accepts(string value) => Values is not null
        ? Values.Contains(value, StringComparer.Ordinal)
        : int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) && number >= Min && number <= Max;
}

/// <summary>Reads a request's <see cref="ConversionRequest.Options"/>; a missing or unreadable value is the fallback.</summary>
public static class RequestOptions
{
    public static int Number(ConversionRequest request, string name, int fallback) =>
        request.Options is not null
        && request.Options.TryGetValue(name, out var text)
        && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : fallback;
}
