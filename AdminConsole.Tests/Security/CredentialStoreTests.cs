using System.Security.Cryptography;
using AdminConsole.Domain.Abstractions;
using AdminConsole.Domain.Models;
using AdminConsole.Infrastructure.Data;
using AdminConsole.Infrastructure.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AdminConsole.Tests.Security;

/// <summary>
/// CredentialStore round-trips secrets through a real temp SQLite DB (the
/// same pattern as every other repository-backed test in this project) and
/// a fake, in-process IDataProtector (a reversible XOR transform, not
/// DPAPI-NG) — this avoids pulling in the full
/// Microsoft.AspNetCore.DataProtection package just for tests, and lets
/// individual tests deterministically force an encrypt/decrypt failure,
/// which real DPAPI-NG doesn't offer an easy hook for.
/// </summary>
public sealed class CredentialStoreTests : IAsyncLifetime
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"AdminConsoleCredentialStoreTests_{Guid.NewGuid():N}.db");
    private ServiceProvider _provider = null!;
    private FakeDataProtector _protector = null!;

    public async Task InitializeAsync()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAdminConsoleDb($"Data Source={_dbPath}");
        _provider = services.BuildServiceProvider();
        _protector = new FakeDataProtector();

        await using var scope = _provider.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<AdminConsoleDbContext>().Database.MigrateAsync();
    }

    public Task DisposeAsync()
    {
        _provider.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
        foreach (var ext in new[] { "-shm", "-wal" })
            if (File.Exists(_dbPath + ext)) File.Delete(_dbPath + ext);
        return Task.CompletedTask;
    }

    /// <summary>
    /// A fresh CredentialStore instance sharing the same DB and protector —
    /// mirrors the real app restarting (in-memory cache empty, must load
    /// from the repository) and proves a round trip actually persisted,
    /// rather than only living in the first instance's in-memory fields.
    /// </summary>
    private CredentialStore NewStore() =>
        new(_provider.GetRequiredService<IServiceScopeFactory>(), new FakeDataProtectionProvider(_protector));

    [Fact]
    public async Task StoreZabbixToken_ThenLoadFromAFreshStore_RoundTripsTheToken()
    {
        await NewStore().StoreZabbixTokenAsync("api-token-123");

        var reloaded = NewStore();
        await reloaded.LoadZabbixFromStoreAsync();

        Assert.True(reloaded.HasZabbixCredentials);
        Assert.True(reloaded.ZabbixUsesApiToken);
        var (username, token) = reloaded.GetZabbix();
        Assert.Equal(string.Empty, username);
        Assert.Equal("api-token-123", token);
    }

    [Fact]
    public async Task StoreZabbixCredentials_UsernameAndPassword_ZabbixUsesApiToken_IsFalse()
    {
        await NewStore().StoreZabbixCredentialsAsync("DOMAIN\\svc", "hunter2");

        var reloaded = NewStore();
        await reloaded.LoadZabbixFromStoreAsync();

        Assert.False(reloaded.ZabbixUsesApiToken);
        var (username, token) = reloaded.GetZabbix();
        Assert.Equal("DOMAIN\\svc", username);
        Assert.Equal("hunter2", token);
    }

    [Fact]
    public async Task StoreTelegramToken_ThenLoadFromAFreshStore_RoundTripsTheToken()
    {
        await NewStore().StoreTelegramTokenAsync("bot-token-xyz");

        var reloaded = NewStore();
        await reloaded.LoadTelegramFromStoreAsync();

        Assert.True(reloaded.HasTelegramCredentials);
        Assert.Equal("bot-token-xyz", reloaded.GetTelegramToken());
    }

    [Fact]
    public async Task ClearZabbixAsync_RemovesFromStore_SoAFreshLoadFindsNothing()
    {
        var store = NewStore();
        await store.StoreZabbixTokenAsync("api-token-123");

        await store.ClearZabbixAsync();
        Assert.False(store.HasZabbixCredentials);

        var reloaded = NewStore();
        await reloaded.LoadZabbixFromStoreAsync();
        Assert.False(reloaded.HasZabbixCredentials);
    }

    [Fact]
    public async Task LoadZabbixFromStoreAsync_WithACorruptedBlob_DegradesToNoCredentials_WithoutThrowing()
    {
        await NewStore().StoreZabbixTokenAsync("api-token-123");

        // Directly corrupt the persisted blob — simulates a key-ring
        // rotation/loss (the scenario CredentialStore.Unprotect's doc
        // comment describes): the bytes no longer decrypt under any key.
        await using (var scope = _provider.CreateAsyncScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<ICredentialRepository>();
            var existing = await repo.GetAsync("Zabbix");
            await repo.UpsertAsync(new StoredCredential
            {
                Target = "Zabbix", Username = existing!.Username, ProtectedSecret = [0xDE, 0xAD, 0xBE, 0xEF]
            });
        }

        var reloaded = NewStore();
        var exception = await Record.ExceptionAsync(() => reloaded.LoadZabbixFromStoreAsync());

        Assert.Null(exception);
        Assert.False(reloaded.HasZabbixCredentials);
    }

    [Fact]
    public async Task StoreZabbixTokenAsync_WhenEncryptionFails_ThrowsCredentialProtectionException()
    {
        _protector.ForceProtectFailure = true;

        await Assert.ThrowsAsync<CredentialProtectionException>(
            () => NewStore().StoreZabbixTokenAsync("api-token-123"));
    }

    [Theory]
    [InlineData("ab3f", "••••")]              // <= 4 chars: fully masked, same length
    [InlineData("supersecrettokenab3f", "••••••••ab3f")]
    public async Task GetZabbixTokenMasked_MasksCorrectly(string token, string expectedMasked)
    {
        var store = NewStore();
        await store.StoreZabbixTokenAsync(token);

        Assert.Equal(expectedMasked, store.GetZabbixTokenMasked());
    }

    [Fact]
    public void GetZabbixTokenMasked_NoCredentials_ReturnsEmptyString()
    {
        Assert.Equal(string.Empty, NewStore().GetZabbixTokenMasked());
    }

    [Fact]
    public async Task StoreZabbixTokenAsync_ResetsThePreviouslyCancelledFlag()
    {
        var store = NewStore();
        store.MarkZabbixCancelled();
        Assert.True(store.UserCancelledZabbixPrompt);

        await store.StoreZabbixTokenAsync("api-token-123");

        Assert.False(store.UserCancelledZabbixPrompt);
    }

    /// <summary>
    /// Reversible XOR transform with a marker byte — not real cryptography,
    /// but enough to prove CredentialStore's round-trip/masking/error-
    /// mapping logic without depending on the full
    /// Microsoft.AspNetCore.DataProtection package. ForceProtectFailure
    /// lets a test deterministically simulate a broken key ring, which real
    /// DPAPI-NG has no test hook for. Public (not file-scoped) because
    /// FakeDataProtectionProvider, a separate top-level type, needs to name it.
    /// </summary>
    public sealed class FakeDataProtector : IDataProtector
    {
        private const byte Marker = 0xAB;
        private const byte XorKey = 0x5A;

        public bool ForceProtectFailure { get; set; }

        public IDataProtector CreateProtector(string purpose) => this;

        public byte[] Protect(byte[] plaintext)
        {
            if (ForceProtectFailure)
                throw new CryptographicException("simulated key-ring failure");

            var result = new byte[plaintext.Length + 1];
            result[0] = Marker;
            for (int i = 0; i < plaintext.Length; i++)
                result[i + 1] = (byte)(plaintext[i] ^ XorKey);
            return result;
        }

        public byte[] Unprotect(byte[] protectedData)
        {
            if (protectedData.Length == 0 || protectedData[0] != Marker)
                throw new CryptographicException("simulated corrupted/foreign blob");

            var result = new byte[protectedData.Length - 1];
            for (int i = 0; i < result.Length; i++)
                result[i] = (byte)(protectedData[i + 1] ^ XorKey);
            return result;
        }
    }
}

/// <summary>Always hands out the same FakeDataProtector, matching CredentialStore's real usage (one CreateProtector call in its constructor).</summary>
internal sealed class FakeDataProtectionProvider(CredentialStoreTests.FakeDataProtector protector) : IDataProtectionProvider
{
    public IDataProtector CreateProtector(string purpose) => protector;
}
