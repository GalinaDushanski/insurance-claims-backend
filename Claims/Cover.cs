using System.Text.Json.Serialization;
using Claims.Serialization;
using MongoDB.Bson.Serialization.Attributes;

namespace Claims;

public class Cover
{
    [BsonId]
    public string Id { get; set; }

    [BsonElement("startDate")]
    [JsonConverter(typeof(UtcDateTimeConverter))]
    public DateTime StartDate { get; set; }

    [BsonElement("endDate")]
    [JsonConverter(typeof(UtcDateTimeConverter))]
    public DateTime EndDate { get; set; }

    [BsonElement("claimType")]
    public CoverType Type { get; set; }

    [BsonElement("premium")]
    public decimal Premium { get; set; }
}

public enum CoverType
{
    Yacht = 0,
    PassengerShip = 1,
    ContainerShip = 2,
    BulkCarrier = 3,
    Tanker = 4
}
