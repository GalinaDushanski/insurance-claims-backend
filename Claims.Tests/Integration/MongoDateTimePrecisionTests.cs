using Claims.Data;
using Claims.Repositories;
using Claims.Services;
using Claims.Tests.Unit;
using Claims.Validation;
using Microsoft.EntityFrameworkCore;
using MongoDB.Bson;
using MongoDB.Driver;
using Xunit;
using Testcontainers.MongoDb;

namespace Claims.Tests.Integration;

public class MongoDateTimePrecisionTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("one-tick cover", 0L, 1L)]
    [InlineData("claim at sub-millisecond end", 1234L, TimeSpan.TicksPerDay + 5678L)]
    [InlineData("millisecond-aligned control", 123 * TimeSpan.TicksPerMillisecond,
        TimeSpan.TicksPerDay + 456 * TimeSpan.TicksPerMillisecond)]
    public async Task Real_provider_round_trip_uses_bson_millisecond_precision(
        string scenario, long startTicks, long endTicks)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        await using var mongo = new MongoDbBuilder("mongo:latest").Build();
        await mongo.StartAsync(timeout.Token);
        var database = new MongoClient(mongo.GetConnectionString()).GetDatabase("precision-tests");
        var options = new DbContextOptionsBuilder<ClaimsContext>()
            .UseMongoDB(database.Client, database.DatabaseNamespace.DatabaseName).Options;
        var origin = new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var cover = new Cover
        {
            Id = "cover-id", StartDate = origin.AddTicks(startTicks),
            EndDate = origin.AddTicks(endTicks), Type = CoverType.Yacht, Premium = 1375m
        };
        var claim = new Claim
        {
            Id = "claim-id", CoverId = cover.Id, Name = "Boundary claim", Type = ClaimType.Collision,
            Created = cover.EndDate, DamageCost = 42m
        };

        await using (var saving = new ClaimsContext(options))
        {
            Assert.Equal("startDate", saving.Model.FindEntityType(typeof(Cover))!
                .FindProperty(nameof(Cover.StartDate))!.GetElementName());
            Assert.Equal("endDate", saving.Model.FindEntityType(typeof(Cover))!
                .FindProperty(nameof(Cover.EndDate))!.GetElementName());
            Assert.Equal("created", saving.Model.FindEntityType(typeof(Claim))!
                .FindProperty(nameof(Claim.Created))!.GetElementName());
            saving.Covers.Add(cover);
            await saving.SaveChangesAsync(timeout.Token);
            saving.Claims.Add(claim);
            await saving.SaveChangesAsync(timeout.Token);
        }

        var storedCover = await database.GetCollection<BsonDocument>("covers")
            .Find(new BsonDocument("_id", cover.Id)).SingleAsync(timeout.Token);
        var storedClaim = await database.GetCollection<BsonDocument>("claims")
            .Find(new BsonDocument("_id", claim.Id)).SingleAsync(timeout.Token);
        await using var reading = new ClaimsContext(options);
        var reloadedCover = await reading.Covers.AsNoTracking().SingleAsync(timeout.Token);
        var reloadedClaim = await reading.Claims.AsNoTracking().SingleAsync(timeout.Token);

        AssertDateRoundTrip("Cover.StartDate", cover.StartDate, storedCover["startDate"], reloadedCover.StartDate);
        AssertDateRoundTrip("Cover.EndDate", cover.EndDate, storedCover["endDate"], reloadedCover.EndDate);
        AssertDateRoundTrip("Claim.Created", claim.Created, storedClaim["created"], reloadedClaim.Created);
        Assert.Equal(reloadedCover.EndDate.Ticks, reloadedClaim.Created.Ticks);
        Assert.True(cover.EndDate > cover.StartDate);

        if (endTicks == 1)
        {
            Assert.Equal(reloadedCover.StartDate.Ticks, reloadedCover.EndDate.Ticks);
            Assert.Throws<ArgumentOutOfRangeException>(() => new PremiumCalculator()
                .ComputePremium(reloadedCover.StartDate, reloadedCover.EndDate, reloadedCover.Type));
        }

        var calls = new List<string>();
        var service = new ClaimService(new ClaimRepository(reading), new RecordingAuditer(calls),
            new CoverRepository(reading));
        var submission = new Claim
        {
            Id = "client-id", CoverId = cover.Id, Name = "Same original boundary",
            Type = ClaimType.Collision, Created = claim.Created, DamageCost = 42m
        };
        if (claim.Created.Ticks % TimeSpan.TicksPerMillisecond != 0)
        {
            var error = await Assert.ThrowsAsync<BusinessValidationException>(() => service.CreateAsync(submission));
            Assert.Contains("created", error.Errors.Keys);
            Assert.Equal("client-id", submission.Id);
            Assert.Equal(claim.Created.Ticks, submission.Created.Ticks);
            Assert.Empty(calls);
            Assert.Equal(1, await reading.Claims.CountAsync(timeout.Token));
        }
        else
        {
            await service.CreateAsync(submission);
            Assert.Equal(2, await reading.Claims.CountAsync(timeout.Token));
            Assert.Equal($"audit-claim:POST:{submission.Id}", Assert.Single(calls));
        }

        output.WriteLine($"Scenario: {scenario}");
    }

    private void AssertDateRoundTrip(string field, DateTime submitted, BsonValue stored, DateTime reloaded)
    {
        var expectedTicks = submitted.Ticks - submitted.Ticks % TimeSpan.TicksPerMillisecond;
        Assert.Equal(BsonType.DateTime, stored.BsonType);
        Assert.Equal(expectedTicks, stored.AsBsonDateTime.ToUniversalTime().Ticks);
        Assert.Equal(expectedTicks, reloaded.Ticks);
        Assert.Equal(DateTimeKind.Utc, reloaded.Kind);
        output.WriteLine($"{field}: submitted={submitted:O}; BSON={stored.AsBsonDateTime.ToUniversalTime():O}; " +
            $"reloaded={reloaded:O}; lost ticks={submitted.Ticks - reloaded.Ticks}");
    }
}
