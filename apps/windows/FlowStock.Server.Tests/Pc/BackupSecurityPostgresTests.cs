using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using FlowStock.Core.Abstractions;
using FlowStock.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace FlowStock.Server.Tests.Pc;

// Реальный PostgreSQL + password login + persisted cookies. Нет mock identity/resolver.
public sealed class BackupSecurityPostgresTests
{
    private static readonly string[] Permissions = [BackupPermission.Read, BackupPermission.Create,
        BackupPermission.Configure, BackupPermission.Probe, BackupPermission.Replicate];

    [Fact]
    public async Task Each_explicit_grant_is_independent_and_admin_machine_claims_never_grant_access()
    {
        await using var fixture = await Fixture.Start();
        var account = fixture.CreateAccount("ADMIN");
        var anonymous = fixture.Request(null);
        anonymous.Headers[WpfMachineAuthorization.KeyHeader] = new string('k', 40);
        foreach (var p in Permissions)
            Assert.Equal(BackupAccessOutcome.InvalidSession, fixture.Gate.Require(anonymous, p).Outcome);

        var cookie = await fixture.Login(account);
        foreach (var p in Permissions)
            Assert.Equal(BackupAccessOutcome.Forbidden, fixture.Gate.Require(fixture.Request(cookie), p).Outcome);

        foreach (var granted in Permissions)
        {
            BackupSecurityCommand.ReplaceGrants(fixture.Cs, account.Id, [granted]);
            Assert.Equal(BackupAccessOutcome.InvalidSession, fixture.Gate.Require(fixture.Request(cookie), granted).Outcome);
            cookie = await fixture.Login(account);
            foreach (var p in Permissions)
            {
                var decision = fixture.Gate.Require(fixture.Request(cookie), p);
                Assert.Equal(p == granted ? BackupAccessOutcome.Allowed : BackupAccessOutcome.Forbidden, decision.Outcome);
                Assert.Equal(account.Id, decision.AccountId);
            }
        }
        Assert.Equal(1L, fixture.Scalar("SELECT count(*) FROM backup_grants WHERE account_id=@id", account.Id));

        var before = fixture.Scalar("SELECT max(expires_at) FROM pc_web_sessions WHERE account_id=@id", account.Id);
        fixture.Gate.Require(fixture.Request(cookie), BackupPermission.Read);
        Assert.Equal(before, fixture.Scalar("SELECT max(expires_at) FROM pc_web_sessions WHERE account_id=@id", account.Id));
        Assert.Throws<ArgumentException>(() => BackupSecurityCommand.ReplaceGrants(fixture.Cs, account.Id, ["backup.download"]));
        Assert.Throws<ArgumentException>(() => BackupSecurityCommand.ReplaceGrants(fixture.Cs, account.Id, ["backup.read", "backup.read"]));
        Assert.Equal(1L, fixture.Scalar("SELECT count(*) FROM backup_grants WHERE account_id=@id", account.Id));

        // No HTTP provisioning surface, including self-grant with ADMIN + machine key.
        using var selfGrant = new HttpRequestMessage(HttpMethod.Post, "/api/admin/backups/grants");
        selfGrant.Headers.Add("Cookie", cookie);
        selfGrant.Headers.Add(WpfMachineAuthorization.KeyHeader, new string('k', 40));
        selfGrant.Content = JsonContent.Create(new { permission = BackupPermission.Read, actor_id = account.Id });
        using var response = await fixture.Client.SendAsync(selfGrant);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Admin_demotion_and_refresh_cannot_reactivate_old_backup_session()
    {
        await using var f = await Fixture.Start();
        var account = f.CreateAccount("ADMIN");
        BackupSecurityCommand.ReplaceGrants(f.Cs, account.Id, [BackupPermission.Read]);
        var cookie = await f.Login(account);
        f.Execute("UPDATE tsd_devices SET access_role='OPERATOR' WHERE id=@id", account.Id);
        var sessions = new PcWebSessionStore(f.Cs);
        Assert.NotNull(sessions.Refresh(f.Request(cookie), DateTimeOffset.UtcNow));
        Assert.False(f.Gate.Require(f.Request(cookie), BackupPermission.Read).IsAllowed);
        Assert.Equal(0L, f.Scalar("SELECT count(*) FROM backup_grants WHERE account_id=@id", account.Id));
        Assert.Equal(BackupAccessOutcome.Forbidden,
            f.Gate.Require(f.Request(await f.Login(account)), BackupPermission.Read).Outcome);
        Assert.Equal(1L, f.Scalar("SELECT count(*) FROM backup_security_events WHERE account_id=@id AND action='GRANTS_INVALIDATED'", account.Id));
    }

    [Theory]
    [InlineData("password_hash = password_hash || 'changed'")]
    [InlineData("access_role = 'ADMIN'")]
    [InlineData("is_active = FALSE")]
    [InlineData("platform = 'TSD'")]
    public async Task Legacy_account_edits_remove_backup_grants_and_reverting_does_not_restore_them(string change)
    {
        await using var f = await Fixture.Start();
        var account = f.CreateAccount();
        BackupSecurityCommand.ReplaceGrants(f.Cs, account.Id, Permissions);
        var cookie = await f.Login(account);
        Assert.True(f.Gate.Require(f.Request(cookie), BackupPermission.Read).IsAllowed);
        f.Execute($"UPDATE tsd_devices SET {change} WHERE id=@id", account.Id);
        Assert.Equal(BackupAccessOutcome.InvalidSession, f.Gate.Require(f.Request(cookie), BackupPermission.Read).Outcome);
        Assert.Equal(0L, f.Scalar("SELECT count(*) FROM backup_grants WHERE account_id=@id", account.Id));
        f.RestoreAccount(account);
        Assert.False(f.Gate.Require(f.Request(cookie), BackupPermission.Read).IsAllowed);
        var newCookie = await f.Login(account);
        Assert.Equal(BackupAccessOutcome.Forbidden, f.Gate.Require(f.Request(newCookie), BackupPermission.Read).Outcome);
        BackupSecurityCommand.ReplaceGrants(f.Cs, account.Id, [BackupPermission.Read]);
        Assert.False(f.Gate.Require(f.Request(newCookie), BackupPermission.Read).IsAllowed);
        Assert.True(f.Gate.Require(f.Request(await f.Login(account)), BackupPermission.Read).IsAllowed);
    }

    [Fact]
    public async Task Session_revocation_expiry_logout_and_multi_client_grant_rotation_are_immediate()
    {
        await using var f = await Fixture.Start();
        var account = f.CreateAccount();
        Assert.False(new PcWebSessionStore(f.Cs).Login(account.Login, "wrong-password", DateTimeOffset.UtcNow).IsSuccess);
        Assert.Equal(BackupAccessOutcome.InvalidSession, f.Gate.Require(f.Request("flowstock_pc_session=invalid"), BackupPermission.Read).Outcome);
        BackupSecurityCommand.ReplaceGrants(f.Cs, account.Id, Permissions);
        var one = await f.Login(account);
        var two = await f.Login(account);
        Assert.True(f.Gate.Require(f.Request(one), BackupPermission.Read).IsAllowed);
        Assert.True(new BackupAuthorization(f.Cs, f.Log).Require(f.Request(two), BackupPermission.Read).IsAllowed);
        BackupSecurityCommand.ReplaceGrants(f.Cs, account.Id, [BackupPermission.Create]);
        foreach (var cookie in new[] { one, two })
            Assert.False(f.Gate.Require(f.Request(cookie), BackupPermission.Read).IsAllowed);
        one = await f.Login(account);
        Assert.Equal(BackupAccessOutcome.Forbidden, f.Gate.Require(f.Request(one), BackupPermission.Read).Outcome);
        Assert.True(f.Gate.Require(f.Request(one), BackupPermission.Create).IsAllowed);
        f.Execute("UPDATE pc_web_sessions SET expires_at=CURRENT_TIMESTAMP - interval '1 second' WHERE account_id=@id", account.Id);
        Assert.Equal(BackupAccessOutcome.InvalidSession, f.Gate.Require(f.Request(one), BackupPermission.Create).Outcome);
        one = await f.Login(account);
        new PcWebSessionStore(f.Cs).Revoke(f.Request(one), DateTimeOffset.UtcNow);
        Assert.Equal(BackupAccessOutcome.InvalidSession, f.Gate.Require(f.Request(one), BackupPermission.Create).Outcome);
        one = await f.Login(account);
        // Pre-migration sessions remain usable for legacy PC, but cannot enter backup.
        f.Execute("UPDATE pc_web_sessions SET backup_auth_version=NULL WHERE account_id=@id", account.Id);
        Assert.NotNull(new PcWebSessionStore(f.Cs).Resolve(f.Request(one)));
        Assert.Equal(BackupAccessOutcome.InvalidSession, f.Gate.Require(f.Request(one), BackupPermission.Create).Outcome);
        one = await f.Login(account);
        BackupSecurityCommand.ReplaceGrants(f.Cs, account.Id, []);
        Assert.False(f.Gate.Require(f.Request(one), BackupPermission.Create).IsAllowed);
    }

    [Fact]
    public async Task Request_ownership_and_safe_audit_use_only_server_account_id()
    {
        await using var f = await Fixture.Start();
        var account = f.CreateAccount();
        var other = f.CreateAccount();
        BackupSecurityCommand.ReplaceGrants(f.Cs, account.Id, [BackupPermission.Read]);
        var request = f.Request(await f.Login(account));
        Assert.True(f.Gate.RequireOwned(request, BackupPermission.Read, account.Id).IsAllowed);
        Assert.Equal(BackupAccessOutcome.NotFound, f.Gate.RequireOwned(request, BackupPermission.Read, other.Id).Outcome);
        Assert.Equal(BackupAccessOutcome.NotFound, f.Gate.RequireOwned(request, BackupPermission.Read, null).Outcome);
        Assert.Equal(BackupAccessOutcome.Forbidden, f.Gate.RequireOwned(request, BackupPermission.Create, account.Id).Outcome);
        Assert.All(f.Log.Messages, m =>
        {
            Assert.DoesNotContain(account.Login, m);
            Assert.DoesNotContain("spoofed", m);
            Assert.DoesNotContain("/private/", m);
            Assert.DoesNotContain("secret", m);
            Assert.DoesNotContain("flowstock_pc_session", m);
            Assert.DoesNotContain(other.Id.ToString(), m);
        });
        Assert.Contains(f.Log.Messages, m => m.Contains($"actor={account.Id}") && m.Contains("permission=backup.read") && m.Contains("outcome=Allowed"));
    }

    [Fact]
    public async Task Opaque_refs_require_operator_registration_current_revision_available_state_and_expiry()
    {
        await using var f = await Fixture.Start();
        var provider = new BackupCredentialReferences(f.Cs);
        Assert.Null(provider.Resolve(Guid.NewGuid(), Guid.NewGuid()));
        var created = BackupSecurityCommand.CreateReference(f.Cs, TimeSpan.FromHours(1));
        f.References.Add(created.Reference);
        Assert.Null(provider.Resolve(created.Reference, created.Revision));
        var configured = BackupSecurityCommand.UpdateReference(f.Cs, created.Reference, created.Revision, "CONFIGURED", TimeSpan.FromHours(1));
        Assert.Equal(configured, provider.Resolve(configured.Reference, configured.Revision));
        Assert.Null(provider.Resolve(created.Reference, created.Revision));
        Assert.Throws<InvalidOperationException>(() => BackupSecurityCommand.UpdateReference(f.Cs, created.Reference, created.Revision, "CONFIGURED", TimeSpan.FromHours(1)));
        var rotated = BackupSecurityCommand.UpdateReference(f.Cs, configured.Reference, configured.Revision, "CONFIGURED", TimeSpan.FromHours(1));
        Assert.Null(provider.Resolve(configured.Reference, configured.Revision));
        Assert.NotNull(provider.Resolve(rotated.Reference, rotated.Revision));
        var revoked = BackupSecurityCommand.UpdateReference(f.Cs, rotated.Reference, rotated.Revision, "REVOKED", TimeSpan.FromHours(1));
        Assert.Null(provider.Resolve(revoked.Reference, revoked.Revision));
        var recovered = BackupSecurityCommand.UpdateReference(f.Cs, revoked.Reference, revoked.Revision, "CONFIGURED", TimeSpan.FromHours(1));
        using var connection = new NpgsqlConnection(f.Cs);
        connection.Open();
        using var expire = new NpgsqlCommand("UPDATE backup_credential_refs SET expires_at = CURRENT_TIMESTAMP - interval '1 second' WHERE credential_ref=@ref", connection);
        expire.Parameters.AddWithValue("ref", recovered.Reference);
        expire.ExecuteNonQuery();
        Assert.Null(provider.Resolve(recovered.Reference, recovered.Revision));
        Assert.Throws<ArgumentException>(() => BackupSecurityCommand.UpdateReference(f.Cs, recovered.Reference, recovered.Revision, "/private/secret", TimeSpan.FromHours(1)));
    }

    private sealed record Account(long Id, string Login, string Salt, string Hash);

    private sealed class AuditLog : ILogger<BackupAuthorization>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Messages.Add(formatter(state, exception));
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public string Cs { get; } = Environment.GetEnvironmentVariable("FLOWSTOCK_POSTGRES_TEST_CONNECTION")
            ?? throw new InvalidOperationException("Isolated PostgreSQL test connection required.");
        public AuditLog Log { get; } = new();
        public BackupAuthorization Gate => new(Cs, Log);
        public HttpClient Client { get; private set; } = null!;
        private WebApplication App { get; set; } = null!;
        private List<long> Accounts { get; } = [];
        public List<Guid> References { get; } = [];

        public static async Task<Fixture> Start()
        {
            var f = new Fixture();
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Services.AddSingleton(new PcWebSessionStore(f.Cs));
            builder.Services.AddSingleton<IPcWebSessionResolver>(sp => sp.GetRequiredService<PcWebSessionStore>());
            builder.Services.AddSingleton<IDataStore>(new PostgresDataStore(f.Cs));
            f.App = builder.Build();
            PcWebSessionEndpoints.Map(f.App);
            await f.App.StartAsync();
            f.Client = f.App.GetTestClient();
            return f;
        }

        public Account CreateAccount(string role = "OPERATOR")
        {
            var login = "backup-" + Guid.NewGuid().ToString("N");
            var salt = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
            var hash = Convert.ToBase64String(Rfc2898DeriveBytes.Pbkdf2("test-password", Convert.FromBase64String(salt), 100000, HashAlgorithmName.SHA256, 32));
            using var connection = new NpgsqlConnection(Cs);
            connection.Open();
            using var command = new NpgsqlCommand(@"
INSERT INTO tsd_devices(device_id,login,password_salt,password_hash,password_iterations,platform,is_active,access_role,created_at)
VALUES(@login,@login,@salt,@hash,100000,'PC',TRUE,@role,'test') RETURNING id;", connection);
            command.Parameters.AddWithValue("login", login);
            command.Parameters.AddWithValue("salt", salt);
            command.Parameters.AddWithValue("hash", hash);
            command.Parameters.AddWithValue("role", role);
            var id = (long)command.ExecuteScalar()!;
            Accounts.Add(id);
            return new(id, login, salt, hash);
        }

        public async Task<string> Login(Account account)
        {
            using var response = await Client.PostAsJsonAsync("/api/pc/login", new { login = account.Login, password = "test-password" });
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var header = Assert.Single(response.Headers.GetValues("Set-Cookie"));
            Assert.Contains("secure", header);
            Assert.Contains("httponly", header);
            Assert.Contains("samesite=strict", header);
            return header.Split(';')[0];
        }

        public HttpRequest Request(string? cookie)
        {
            var context = new DefaultHttpContext();
            if (cookie != null) context.Request.Headers.Cookie = cookie;
            context.Request.Headers[WpfMachineAuthorization.KeyHeader] = new string('k', 40);
            context.Request.Headers[WpfMachineAuthorization.AuditActorHeader] = "spoofed-/private/secret";
            context.Request.Headers["X-Device-Id"] = "spoofed";
            context.Request.Headers["X-Role"] = "ADMIN";
            return context.Request;
        }

        public void RestoreAccount(Account account)
        {
            using var connection = new NpgsqlConnection(Cs);
            connection.Open();
            using var command = new NpgsqlCommand("UPDATE tsd_devices SET password_hash=@hash,platform='PC',is_active=TRUE,access_role='OPERATOR' WHERE id=@id", connection);
            command.Parameters.AddWithValue("id", account.Id);
            command.Parameters.AddWithValue("hash", account.Hash);
            command.ExecuteNonQuery();
        }

        public object? Scalar(string sql, long id)
        {
            using var connection = new NpgsqlConnection(Cs);
            connection.Open();
            using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue("id", id);
            return command.ExecuteScalar();
        }
        public void Execute(string sql, long id) => Scalar(sql, id);

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await App.DisposeAsync();
            foreach (var id in Accounts) Execute("DELETE FROM tsd_devices WHERE id=@id", id);
            using var connection = new NpgsqlConnection(Cs);
            connection.Open();
            foreach (var reference in References)
            {
                using var command = new NpgsqlCommand("DELETE FROM backup_credential_refs WHERE credential_ref=@ref", connection);
                command.Parameters.AddWithValue("ref", reference);
                command.ExecuteNonQuery();
            }
        }
    }
}
