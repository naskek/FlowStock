using Microsoft.AspNetCore.Http;

namespace FlowStock.Server.Tests.Pc;

public sealed class PcAccessRoleValidationTests
{
    [Theory]
    [InlineData(null, PcAccessRole.Operator)]
    [InlineData("", PcAccessRole.Operator)]
    [InlineData("   ", PcAccessRole.Operator)]
    [InlineData("OPERATOR", PcAccessRole.Operator)]
    [InlineData(" operator ", PcAccessRole.Operator)]
    [InlineData("ADMIN", PcAccessRole.Admin)]
    [InlineData(" admin ", PcAccessRole.Admin)]
    public void SupportedOrMissingRole_ReturnsNormalizedValue(string? input, string expected)
    {
        var error = PcAccessRole.ValidateInput(input, out var accessRole);

        Assert.Null(error);
        Assert.Equal(expected, accessRole);
    }

    [Theory]
    [InlineData("ADMN")]
    [InlineData("SUPERADMIN")]
    public void InvalidNonEmptyRole_ReturnsBadRequest(string input)
    {
        var error = PcAccessRole.ValidateInput(input, out var accessRole);

        var statusResult = Assert.IsAssignableFrom<IStatusCodeHttpResult>(error);
        Assert.Equal(StatusCodes.Status400BadRequest, statusResult.StatusCode);
        var valueResult = Assert.IsAssignableFrom<IValueHttpResult>(error);
        var payload = Assert.IsType<ApiResult>(valueResult.Value);
        Assert.False(payload.Ok);
        Assert.Equal("INVALID_ACCESS_ROLE", payload.Error);
        Assert.Equal(PcAccessRole.Operator, accessRole);
    }
}
