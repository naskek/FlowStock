using FlowStock.DesktopUpdate;

namespace FlowStock.Server.Tests.DesktopUpdate;

public sealed class BuildIdentityTests
{
    private const string Commit = "0123456789abcdef0123456789abcdef01234567";

    [Theory]
    [InlineData("1.0.0")]
    [InlineData("2.14.3-beta.2")]
    public void ParseInformationalVersion_SeparatesProductVersionAndCommit(string version)
    {
        var identity = BuildIdentity.ParseInformationalVersion($"{version}+{Commit}");

        Assert.Equal(version, identity.ProductVersion);
        Assert.Equal(Commit, identity.SourceCommit);
        Assert.Equal("01234567", identity.ShortCommit);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("1.0.0")]
    [InlineData("1.0.0+0123")]
    [InlineData("1.0.0+0123456789ABCDEF0123456789ABCDEF01234567")]
    [InlineData("01.0.0+0123456789abcdef0123456789abcdef01234567")]
    public void ParseInformationalVersion_RejectsAmbiguousIdentity(string? value)
    {
        Assert.Throws<InvalidOperationException>(() => BuildIdentity.ParseInformationalVersion(value));
    }

    [Fact]
    public void AssemblyVersionAndMvid_DoNotParticipateInIdentity()
    {
        var identity = BuildIdentity.Create("1.0.0", Commit);
        var assemblyVersion = typeof(BuildIdentity).Assembly.GetName().Version;
        var mvid = typeof(BuildIdentity).Module.ModuleVersionId;

        Assert.NotEqual(assemblyVersion?.ToString(), identity.ProductVersion);
        Assert.DoesNotContain(mvid.ToString("N"), identity.SourceCommit, StringComparison.OrdinalIgnoreCase);
    }
}
