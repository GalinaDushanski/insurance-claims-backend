namespace Claims
{
    public class Claim
    {
        public string Id { get; set; } = null!;

        public string CoverId { get; set; } = null!;

        public DateTime Created { get; set; }

        public string Name { get; set; } = null!;

        public ClaimType Type { get; set; }

        public decimal DamageCost { get; set; }
    }
}
