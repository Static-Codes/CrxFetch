namespace CrxFetch.Cli;

/// <summary>
/// Process exit codes. These are part of the tool's contract with callers and scripts, so
/// they are unchanged.
/// </summary>
internal static class ExitCodes
{
    /// <summary>Success, and for <c>--inspect</c> a verified signature.</summary>
    public const int Ok = 0;

    /// <summary>Bad invocation: missing, unknown or unparsable arguments.</summary>
    public const int Usage = 64;

    /// <summary>Payload was not a valid CRX, or failed validation.</summary>
    public const int BadPayload = 65;

    /// <summary>No package was offered, or the service could not be reached.</summary>
    public const int NoPackage = 69;
}