namespace CrxFetch.Tests;

/// <summary>
/// Resolves proxy endpoints for the end-to-end suite from the environment, so tests can run
/// against real egress without hard-coding anyone's infrastructure.
/// </summary>
/// <remarks>
/// Recognised inputs, all optional:
/// <list type="bullet">
///   <item><description><c>CRXFETCH_TEST_PROXIES</c> — list separated by comma, semicolon or newline</description></item>
///   <item><description><c>CRXFETCH_TEST_PROXY_FILE</c> — path to a file with one endpoint per line</description></item>
///   <item><description><c>CRXFETCH_TEST_PROXY</c> — a single endpoint</description></item>
/// </list>
/// </remarks>
internal static class ProxyPool
{
    public const string SchemesEnvVar = "CRXFETCH_TEST_PROXIES";
    public const string FileEnvVar = "CRXFETCH_TEST_PROXY_FILE";
    public const string SingleEnvVar = "CRXFETCH_TEST_PROXY";

    /// <summary>Every scheme the end-to-end suite exercises, including direct egress.</summary>
    public static IReadOnlyList<string> ExercisedSchemes { get; } = ["none", "http", "https", "socks4", "socks5"];

    private static readonly char[] Separators = [',', ';', '\n', '\r', '\t', ' '];

    /// <summary>All configured endpoints, in the order they were supplied.</summary>
    public static IReadOnlyList<string> All => ReadAll();

    /// <summary>
    /// The first usable endpoint for a scheme, or null when none is configured. The
    /// <c>none</c> scheme means direct egress and is always available.
    /// </summary>
    public static string? For(string scheme)
    {
        if (string.Equals(scheme, "none", StringComparison.OrdinalIgnoreCase)) { return null; }

        return ReadAll().FirstOrDefault(endpoint =>
            Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) &&
            string.Equals(uri.Scheme, scheme, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>A short description of what is configured, for skip messages.</summary>
    public static string Describe()
    {
        var all = ReadAll();
        return all.Count == 0
            ? string.Join("\n", ["No proxies configured, skipping this test.",
                                $" -> set/export {SchemesEnvVar} or {SingleEnvVar}",
                                " -> append \" && dotnet test\".\n"])
            : string.Join(", ", all);
    }

    private static IReadOnlyList<string> ReadAll()
    {
        var values = new List<string>();

        AddFrom(SingleEnvVar);
        AddFrom(SchemesEnvVar);

        var file = Environment.GetEnvironmentVariable(FileEnvVar);
        if (!string.IsNullOrWhiteSpace(file) && File.Exists(file))
        {
            values.AddRange(File.ReadAllLines(file).Select(l => l.Trim()));
        }

        return values
            .SelectMany(v => v.Split(Separators, StringSplitOptions.RemoveEmptyEntries))
            .Select(v => v.Trim())
            .Where(v => v.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        void AddFrom(string variable)
        {
            var raw = Environment.GetEnvironmentVariable(variable);
            if (!string.IsNullOrWhiteSpace(raw))
            {
                values.Add(raw);
            }
        }
    }
}