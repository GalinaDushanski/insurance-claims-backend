namespace Claims.Api.Contracts;

// HTTP contracts and domain entities are independent objects in both directions.
public static class ContractMapping
{
    public static global::Claims.Claim ToDomain(this Claim dto) => new()
    {
        Id = dto.Id, CoverId = dto.CoverId, Created = dto.Created,
        Name = dto.Name, Type = dto.Type, DamageCost = dto.DamageCost
    };

    public static Claim ToDto(this global::Claims.Claim entity) => new()
    {
        Id = entity.Id, CoverId = entity.CoverId, Created = entity.Created,
        Name = entity.Name, Type = entity.Type, DamageCost = entity.DamageCost
    };

    public static global::Claims.Cover ToDomain(this Cover dto) => new()
    {
        Id = dto.Id, StartDate = dto.StartDate, EndDate = dto.EndDate,
        Type = dto.Type, Premium = dto.Premium
    };

    public static Cover ToDto(this global::Claims.Cover entity) => new()
    {
        Id = entity.Id, StartDate = entity.StartDate, EndDate = entity.EndDate,
        Type = entity.Type, Premium = entity.Premium
    };
}
