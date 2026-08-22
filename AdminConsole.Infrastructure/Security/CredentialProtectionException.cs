namespace AdminConsole.Infrastructure.Security;

/// <summary>
/// Аудит Зона 4, Знахідка №2 (2026-08-22): CredentialStore.Protect() кидав
/// голий CryptographicException, що летів неспійманим аж до HTTP 500 без
/// жодного пояснення (на відміну від Unprotect(), яка вже коректно
/// деградує до "секрет недоступний"). Цей тип дає CredentialsController
/// щось конкретне для відлову — з людяним повідомленням замість голого 500.
/// </summary>
public sealed class CredentialProtectionException(string message, Exception inner)
    : Exception(message, inner);
