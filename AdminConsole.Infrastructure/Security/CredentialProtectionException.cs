namespace AdminConsole.Infrastructure.Security;

/// <summary>
/// Audit Zone 4, Finding #2 (2026-08-22): CredentialStore.Protect() used to
/// throw a bare CryptographicException that flew uncaught all the way to
/// HTTP 500 with no explanation (unlike Unprotect(), which already degrades
/// correctly to "secret unavailable"). This type gives CredentialsController
/// something concrete to catch — with a human-readable message instead of a bare 500.
/// </summary>
public sealed class CredentialProtectionException(string message, Exception inner)
    : Exception(message, inner);
