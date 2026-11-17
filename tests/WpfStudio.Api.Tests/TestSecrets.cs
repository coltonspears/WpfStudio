namespace WpfStudio.Api.Tests;

/// <summary>
/// Stand-ins for passwords and tokens, made fresh by each run. A literal would read as a leaked credential to secret
/// scanners, and a random value also proves a test is not passing on something it happened to hard-code.
/// </summary>
internal static class TestSecrets
{
    public static string Make(string purpose) => purpose + "-" + Guid.NewGuid().ToString("N")[..12];
}
