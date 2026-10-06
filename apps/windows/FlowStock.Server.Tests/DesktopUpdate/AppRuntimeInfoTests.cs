using FlowStock.App;

namespace FlowStock.Server.Tests.DesktopUpdate;

public sealed class AppRuntimeInfoTests
{
    [Theory]
    [InlineData(@"D:\Projects\FlowStock\apps\windows\FlowStock.App\bin\Debug\net8.0-windows", true)]
    [InlineData(@"D:\FlowStock-dev\apps\windows\FlowStock.App\bin\Debug\net8.0-windows", true)]
    [InlineData(@"C:\Users\operator\AppData\Local\FlowStock\Desktop\versions\0123456789abcdef0123456789abcdef01234567\app", false)]
    [InlineData(@"D:\Projects\FlowStock-old\apps\windows\FlowStock.App", false)]
    public void IsSourceCheckoutDirectory_OnlyAllowsCanonicalSourceRoots(string path, bool expected)
    {
        Assert.Equal(expected, AppRuntimeInfo.IsSourceCheckoutDirectory(path));
    }
}
