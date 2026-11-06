using System.Text.Json.Serialization;
using Claims.Serialization;

namespace Claims.Api.Contracts;

public class Cover
{
    public string Id { get; set; } = null!;

    [JsonConverter(typeof(UtcDateTimeConverter))]
    public DateTime StartDate { get; set; }

    [JsonConverter(typeof(UtcDateTimeConverter))]
    public DateTime EndDate { get; set; }

    public CoverType Type { get; set; }

    public decimal Premium { get; set; }
}
