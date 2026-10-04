using System.Net;

namespace CrxFetch;

/// <summary>Why a CRX fetch did not produce a usable package.</summary>
public enum CrxFailureReason
{
    /// <summary>The update service could not be reached at all.</summary>
    Transport,

    /// <summary>The update service was reached but offered no package url.</summary>
    NoPackage,

    /// <summary>A package was returned but failed validation.</summary>
    Validation,
}

/// <summary>Raised when a CRX cannot be retrieved or fails validation.</summary>
public class CrxException : Exception
{
    /// <summary>Creates an exception with a message.</summary>
    /// <param name="reason">Why the fetch failed.</param>
    /// <param name="message">Human readable description.</param>
    public CrxException(CrxFailureReason reason, string message)
        : base(message)
    {
        Reason = reason;
    }

    /// <summary>Creates an exception with a message and an inner cause.</summary>
    /// <param name="reason">Why the fetch failed.</param>
    /// <param name="message">Human readable description.</param>
    /// <param name="innerException">The underlying failure.</param>
    public CrxException(CrxFailureReason reason, string message, Exception innerException)
        : base(message, innerException)
    {
        Reason = reason;
    }

    /// <summary>Why the fetch failed.</summary>
    public CrxFailureReason Reason { get; }
}

/// <summary>Raised when the update service cannot be reached.</summary>
public sealed class CrxTransportException : CrxException
{
    /// <summary>Creates a transport exception.</summary>
    /// <param name="message">Human readable description.</param>
    /// <param name="innerException">The underlying network failure.</param>
    public CrxTransportException(string message, Exception innerException)
        : base(CrxFailureReason.Transport, message, innerException)
    {
    }
}

/// <summary>Raised when the update service returns no package url for an extension.</summary>
public sealed class CrxNoPackageException : CrxException
{
    /// <summary>Creates a no-package exception.</summary>
    /// <param name="extensionId">The extension that was requested.</param>
    /// <param name="attempts">Every request shape that was tried, with its outcome.</param>
    public CrxNoPackageException(string extensionId, IReadOnlyList<GupAttempt> attempts)
        : base(CrxFailureReason.NoPackage, BuildMessage(extensionId, attempts))
    {
        ExtensionId = extensionId;
        Attempts = attempts;
    }

    /// <summary>The extension that was requested.</summary>
    public string ExtensionId { get; }

    /// <summary>Every request shape that was tried, with its outcome.</summary>
    public IReadOnlyList<GupAttempt> Attempts { get; }

    /// <summary>
    /// True when the service answered 204 No Content, which is how it declines to serve a
    /// package. This is a deliberate refusal, not a malformed request: the body is empty and
    /// no <c>Location</c> header is sent.
    /// </summary>
    public bool DeclinedWithNoContent =>
        Attempts.Any(a => a.Status == HttpStatusCode.NoContent);

    private static string BuildMessage(string extensionId, IReadOnlyList<GupAttempt> attempts)
    {
        var statuses = string.Join(", ", attempts.Select(a => $"{a.Description}={(int)a.Status}"));
        return $"No package url for {extensionId}. Attempts: {statuses}";
    }
}

/// <summary>Raised when a returned package is not a valid CRX for the requested extension.</summary>
public sealed class CrxValidationException : CrxException
{
    /// <summary>Creates a validation exception.</summary>
    /// <param name="message">Human readable description.</param>
    public CrxValidationException(string message)
        : base(CrxFailureReason.Validation, message)
    {
    }

    /// <summary>Creates a validation exception caused by a parsing failure.</summary>
    /// <param name="message">Human readable description.</param>
    /// <param name="innerException">The underlying parsing failure.</param>
    public CrxValidationException(string message, Exception innerException)
        : base(CrxFailureReason.Validation, message, innerException)
    {
    }
}