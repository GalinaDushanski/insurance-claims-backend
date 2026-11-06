using System.Text.Json;
using System.Text.Json.Serialization;
using Claims.Api.Contracts;
using Xunit;
using ClaimContract = Claims.Api.Contracts.Claim;
using CoverContract = Claims.Api.Contracts.Cover;

namespace Claims.Tests.Unit;

public class ContractMappingTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    [Fact]
    public void Claim_mapping_preserves_every_field_and_isolates_mutations()
    {
        var dto = new ClaimContract
        {
            Id = "client-id", CoverId = "cover-id", Name = "Fire claim", Type = ClaimType.Fire,
            Created = new DateTime(2030, 1, 1, 12, 0, 0, DateTimeKind.Utc).AddTicks(1234), DamageCost = 12345.67m
        };
        var domain = dto.ToDomain();
        var result = domain.ToDto();
        Assert.Equal(dto.Id, domain.Id);
        Assert.Equal(dto.CoverId, domain.CoverId);
        Assert.Equal(dto.Name, domain.Name);
        Assert.Equal(dto.Type, domain.Type);
        Assert.Equal(dto.Created.Ticks, domain.Created.Ticks);
        Assert.Equal(DateTimeKind.Utc, domain.Created.Kind);
        Assert.Equal(dto.DamageCost, domain.DamageCost);
        Assert.Equal(JsonSerializer.Serialize(dto, JsonOptions), JsonSerializer.Serialize(result, JsonOptions));
        domain.Id = "generated-id";
        result.Name = "Changed response";
        Assert.Equal("client-id", dto.Id);
        Assert.Equal("Fire claim", domain.Name);
        Assert.NotSame(dto, result);
    }

    [Fact]
    public void Cover_mapping_preserves_every_field_and_isolates_server_assignments()
    {
        var dto = new CoverContract
        {
            Id = "client-id", StartDate = new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddTicks(1),
            EndDate = new DateTime(2030, 2, 1, 0, 0, 0, DateTimeKind.Utc).AddTicks(2),
            Type = CoverType.PassengerShip, Premium = 123.45m
        };
        var domain = dto.ToDomain();
        var result = domain.ToDto();
        Assert.Equal(dto.Id, domain.Id);
        Assert.Equal(dto.StartDate.Ticks, domain.StartDate.Ticks);
        Assert.Equal(dto.EndDate.Ticks, domain.EndDate.Ticks);
        Assert.Equal(DateTimeKind.Utc, domain.StartDate.Kind);
        Assert.Equal(DateTimeKind.Utc, domain.EndDate.Kind);
        Assert.Equal(dto.Type, domain.Type);
        Assert.Equal(dto.Premium, domain.Premium);
        Assert.Equal(JsonSerializer.Serialize(dto, JsonOptions), JsonSerializer.Serialize(result, JsonOptions));
        domain.Id = "generated-id";
        domain.Premium = 46470m;
        Assert.Equal("client-id", dto.Id);
        Assert.Equal(123.45m, dto.Premium);
        Assert.Equal(123.45m, result.Premium);
        Assert.NotSame(dto, result);
    }

    [Fact]
    public void Contracts_preserve_property_names_enum_strings_and_utc_date_normalization()
    {
        var claim = JsonSerializer.Deserialize<ClaimContract>("""
            {"id":"id","coverId":"cover","created":"2030-01-01T02:00:00+02:00",
             "name":"Collision","type":"Collision","damageCost":42}
            """, JsonOptions)!;
        var cover = JsonSerializer.Deserialize<CoverContract>("""
            {"id":"cover","startDate":"2030-01-01","endDate":"2030-01-02T02:00:00+02:00",
             "type":"Yacht","premium":1375}
            """, JsonOptions)!;
        using var claimJson = JsonDocument.Parse(JsonSerializer.Serialize(claim.ToDomain().ToDto(), JsonOptions));
        using var coverJson = JsonDocument.Parse(JsonSerializer.Serialize(cover.ToDomain().ToDto(), JsonOptions));
        Assert.Equal(new[] { "id", "coverId", "created", "name", "type", "damageCost" },
            claimJson.RootElement.EnumerateObject().Select(x => x.Name));
        Assert.Equal(new[] { "id", "startDate", "endDate", "type", "premium" },
            coverJson.RootElement.EnumerateObject().Select(x => x.Name));
        Assert.Equal("2030-01-01T00:00:00Z", claimJson.RootElement.GetProperty("created").GetString());
        Assert.Equal("Collision", claimJson.RootElement.GetProperty("type").GetString());
        Assert.Equal("2030-01-01T00:00:00Z", coverJson.RootElement.GetProperty("startDate").GetString());
        Assert.Equal("2030-01-02T00:00:00Z", coverJson.RootElement.GetProperty("endDate").GetString());
        Assert.Equal("Yacht", coverJson.RootElement.GetProperty("type").GetString());
    }
}
