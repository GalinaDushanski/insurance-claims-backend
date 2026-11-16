using Claims.Auditing;
using Claims.Data;
using Claims.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;

namespace Claims.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddClaimsInfrastructure(this IServiceCollection services,
        string sqlConnectionString, string mongoConnectionString, string databaseName)
    {
        services.AddDbContext<AuditContext>(options => options.UseSqlServer(sqlConnectionString));
        services.AddDbContext<ClaimsContext>(options =>
        {
            var database = new MongoClient(mongoConnectionString).GetDatabase(databaseName);
            options.UseMongoDB(database.Client, database.DatabaseNamespace.DatabaseName);
        });
        services.AddScoped<IClaimRepository, ClaimRepository>();
        services.AddScoped<ICoverRepository, CoverRepository>();
        services.AddSingleton<IAuditer, Auditer>();
        services.AddSingleton<AuditQueue>();
        services.AddScoped<IAuditWriter, SqlAuditWriter>();
        services.AddHostedService<AuditWorker>();
        return services;
    }

    public static void ApplyAuditMigrations(this IServiceProvider services)
    {
        using var scope = services.CreateScope();
        scope.ServiceProvider.GetRequiredService<AuditContext>().Database.Migrate();
    }
}
