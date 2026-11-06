using System.Runtime.InteropServices;
using Testcontainers.MongoDb;
using Testcontainers.MsSql;

namespace Claims.Infrastructure;

// Local assignment hosting uses temporary databases, as before the project split.
public sealed class LocalDatabases
{
    private readonly MsSqlContainer _sql = new MsSqlBuilder(RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
        ? "mcr.microsoft.com/mssql/server:2022-latest"
        : "mcr.microsoft.com/mssql/server:2019-CU18-ubuntu-20.04").Build();
    private readonly MongoDbContainer _mongo = new MongoDbBuilder("mongo:latest").Build();

    public string SqlConnectionString => _sql.GetConnectionString();
    public string MongoConnectionString => _mongo.GetConnectionString();

    public async Task StartAsync()
    {
        await _sql.StartAsync();
        await _mongo.StartAsync();
    }
}
