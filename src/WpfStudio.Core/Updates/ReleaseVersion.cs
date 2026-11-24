using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace WpfStudio.Core.Updates;

/// <summary>
/// A release's version as Semantic Versioning orders it: <c>major.minor.patch</c> with an optional pre-release such as
/// <c>-beta.2</c>. A leading <c>v</c> (as in a tag) and build metadata after <c>+</c> (as in an assembly's informational
/// version) are accepted and not kept.
/// </summary>
public sealed class ReleaseVersion : IComparable<ReleaseVersion>, IEquatable<ReleaseVersion>
{
    private ReleaseVersion(int major, int minor, int patch, string prerelease) { Major = major; Minor = minor; Patch = patch; Prerelease = prerelease; }

    public int Major { get; }
    public int Minor { get; }
    public int Patch { get; }
    /// <summary>The part after <c>-</c>, empty for a release.</summary>
    public string Prerelease { get; }
    public bool IsPrerelease => Prerelease.Length > 0;

    public static ReleaseVersion Parse(string text) => TryParse(text, out var version) ? version : throw new FormatException($"'{text}' is not a version such as 1.2.3.");

    public static bool TryParse([NotNullWhen(true)] string? text, [NotNullWhen(true)] out ReleaseVersion? version)
    {
        version = null;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var value = text.Trim();
        if (value[0] is 'v' or 'V') value = value[1..];
        var plus = value.IndexOf('+', StringComparison.Ordinal);
        if (plus >= 0) value = value[..plus];
        var dash = value.IndexOf('-', StringComparison.Ordinal);
        var prerelease = dash >= 0 ? value[(dash + 1)..] : "";
        var core = (dash >= 0 ? value[..dash] : value).Split('.');
        // Two parts are accepted as x.y.0; System.Version's four parts are not, so 1.2.3.4 is not mistaken for a release.
        if (core.Length is < 2 or > 3 || (dash >= 0 && !ValidPrerelease(prerelease))) return false;
        var numbers = new int[3];
        for (var i = 0; i < core.Length; i++)
            if (core[i].Length == 0 || !core[i].All(char.IsAsciiDigit) || !int.TryParse(core[i], NumberStyles.None, CultureInfo.InvariantCulture, out numbers[i])) return false;
        version = new ReleaseVersion(numbers[0], numbers[1], numbers[2], prerelease);
        return true;
    }

    private static bool ValidPrerelease(string prerelease) =>
        prerelease.Length > 0 && prerelease.Split('.').All(part => part.Length > 0 && part.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'));

    public int CompareTo(ReleaseVersion? other)
    {
        if (other is null) return 1;
        var result = Major.CompareTo(other.Major);
        if (result == 0) result = Minor.CompareTo(other.Minor);
        if (result == 0) result = Patch.CompareTo(other.Patch);
        return result != 0 ? result : ComparePrerelease(Prerelease, other.Prerelease);
    }

    /// <summary>A release follows its own pre-releases; pre-release parts compare numerically when both are numbers, otherwise by text, and more parts win a tie.</summary>
    private static int ComparePrerelease(string left, string right)
    {
        if (left.Length == 0 || right.Length == 0) return right.Length.CompareTo(left.Length);
        var a = left.Split('.'); var b = right.Split('.');
        for (var i = 0; i < Math.Min(a.Length, b.Length); i++)
        {
            var aNumber = int.TryParse(a[i], NumberStyles.None, CultureInfo.InvariantCulture, out var x);
            var bNumber = int.TryParse(b[i], NumberStyles.None, CultureInfo.InvariantCulture, out var y);
            var result = (aNumber, bNumber) switch
            {
                (true, true) => x.CompareTo(y),
                (true, false) => -1,
                (false, true) => 1,
                _ => string.CompareOrdinal(a[i], b[i]),
            };
            if (result != 0) return result;
        }
        return a.Length.CompareTo(b.Length);
    }

    public bool Equals(ReleaseVersion? other) => other is not null && CompareTo(other) == 0;
    public override bool Equals(object? obj) => obj is ReleaseVersion other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(Major, Minor, Patch, Prerelease);
    public override string ToString() => IsPrerelease ? $"{Major}.{Minor}.{Patch}-{Prerelease}" : $"{Major}.{Minor}.{Patch}";

    public static bool operator ==(ReleaseVersion? left, ReleaseVersion? right) => left is null ? right is null : left.Equals(right);
    public static bool operator !=(ReleaseVersion? left, ReleaseVersion? right) => !(left == right);
    public static bool operator <(ReleaseVersion? left, ReleaseVersion? right) => left is null ? right is not null : left.CompareTo(right) < 0;
    public static bool operator <=(ReleaseVersion? left, ReleaseVersion? right) => left is null || left.CompareTo(right) <= 0;
    public static bool operator >(ReleaseVersion? left, ReleaseVersion? right) => left is not null && left.CompareTo(right) > 0;
    public static bool operator >=(ReleaseVersion? left, ReleaseVersion? right) => left is null ? right is null : left.CompareTo(right) >= 0;
}
