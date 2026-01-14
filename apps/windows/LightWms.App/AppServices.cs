using System.IO;
using LightWms.Core.Abstractions;
using LightWms.Core.Services;
using LightWms.Data;

namespace LightWms.App;

public sealed class AppServices
{
    public IDataStore DataStore { get; }
    public CatalogService Catalog { get; }
    public DocumentService Documents { get; }
    public ImportService Import { get; }
    public string DatabasePath { get; }

    private AppServices(IDataStore dataStore, string databasePath)
    {
        DataStore = dataStore;
        Catalog = new CatalogService(dataStore);
        Documents = new DocumentService(dataStore);
        Import = new ImportService(dataStore);
        DatabasePath = databasePath;
    }

    public static AppServices CreateDefault()
    {
        var baseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LightWms.Local");
        var dbPath = Path.Combine(baseDir, "lightwms.db");

        var dataStore = new SqliteDataStore(dbPath);
        dataStore.Initialize();

        return new AppServices(dataStore, dbPath);
    }
}
