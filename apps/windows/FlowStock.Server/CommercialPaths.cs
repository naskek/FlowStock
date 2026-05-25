namespace FlowStock.Server;

public static class CommercialPaths
{
    private const string CommercialRootEnvKey = "FLOWSTOCK_COMMERCIAL_ROOT";

    public static string CommercialRoot
    {
        get
        {
            var configured = Environment.GetEnvironmentVariable(CommercialRootEnvKey);
            if (!string.IsNullOrWhiteSpace(configured))
            {
                return Path.GetFullPath(configured.Trim());
            }

            return Path.Combine(ServerPaths.BaseDir, "commercial");
        }
    }

    public static string TemplateDirectory(long templateId, int versionNo) =>
        Path.Combine(CommercialRoot, "templates", templateId.ToString(), $"v{versionNo}");
}
