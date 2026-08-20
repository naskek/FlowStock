using Microsoft.AspNetCore.Http;

namespace FlowStock.Server.Tests.Catalog;

public sealed class CatalogAuthorizationTests
{
    private const string MachineKey = "test-catalog-machine-key-at-least-32-characters";

    [Fact]
    public void TrustedWpf_IsAllowedWithoutPcSession()
    {
        var request = new DefaultHttpContext().Request;
        request.Headers[WpfMachineAuthorization.KeyHeader] = MachineKey;

        var result = CreateAuthorization(null).RequireManageCatalog(request);

        Assert.Null(result);
    }

    [Fact]
    public void PcAdmin_IsAllowed()
    {
        var request = new DefaultHttpContext().Request;

        Assert.Null(CreateAuthorization(Identity(PcAccessRole.Admin)).RequireManageCatalog(request));
    }

    [Fact]
    public void PcOperator_GetsManageCatalogRequired()
    {
        var request = new DefaultHttpContext().Request;

        var forbidden = Assert.IsAssignableFrom<IStatusCodeHttpResult>(
            CreateAuthorization(Identity(PcAccessRole.Operator)).RequireManageCatalog(request));
        Assert.Equal(StatusCodes.Status403Forbidden, forbidden.StatusCode);
        var payload = Assert.IsType<ApiResult>(Assert.IsAssignableFrom<IValueHttpResult>(forbidden).Value);
        Assert.Equal("MANAGE_CATALOG_REQUIRED", payload.Error);
    }

    [Fact]
    public void MissingOrInvalidPcSession_GetsInvalidSession()
    {
        var result = Assert.IsAssignableFrom<IStatusCodeHttpResult>(
            CreateAuthorization(null).RequireManageCatalog(new DefaultHttpContext().Request));

        Assert.Equal(StatusCodes.Status401Unauthorized, result.StatusCode);
        var payload = Assert.IsType<ApiResult>(Assert.IsAssignableFrom<IValueHttpResult>(result).Value);
        Assert.Equal("INVALID_SESSION", payload.Error);
    }

    [Fact]
    public void AdminCapabilities_IncludeCatalogAndPendingRequestManagement()
    {
        var admin = Identity(PcAccessRole.Admin);
        var operatorIdentity = Identity(PcAccessRole.Operator);

        Assert.True(admin.CanManageCatalog);
        Assert.True(admin.CanManagePendingRequests);
        Assert.False(operatorIdentity.CanManageCatalog);
        Assert.False(operatorIdentity.CanManagePendingRequests);
    }

    private static CatalogAuthorization CreateAuthorization(PcWebIdentity? identity) =>
        new(new WpfMachineAuthorization(MachineKey), new FixedResolver(identity));

    private static PcWebIdentity Identity(string role) => new(1, "PC-1", "login", "PC", role);

    private sealed class FixedResolver(PcWebIdentity? identity) : IPcWebSessionResolver
    {
        public PcWebIdentity? Resolve(HttpRequest request) => identity;
    }
}
