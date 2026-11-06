using Microsoft.EntityFrameworkCore;
using MongoDB.EntityFrameworkCore.Extensions;

namespace Claims.Data
{
    public class ClaimsContext : DbContext
    {

        public DbSet<Claim> Claims { get; init; }
        public DbSet<Cover>  Covers { get; init; }

        public ClaimsContext(DbContextOptions options)
            : base(options)
        {
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.Entity<Claim>().ToCollection("claims");
            modelBuilder.Entity<Cover>().ToCollection("covers");

            // Keep the original BSON schema without persistence attributes in Domain.
            var claim = modelBuilder.Entity<Claim>();
            claim.HasKey(x => x.Id);
            claim.Property(x => x.Id).HasElementName("_id");
            claim.Property(x => x.CoverId).HasElementName("coverId");
            claim.Property(x => x.Created).HasElementName("created");
            claim.Property(x => x.Name).HasElementName("name");
            claim.Property(x => x.Type).HasElementName("claimType");
            claim.Property(x => x.DamageCost).HasElementName("damageCost");

            var cover = modelBuilder.Entity<Cover>();
            cover.HasKey(x => x.Id);
            cover.Property(x => x.Id).HasElementName("_id");
            cover.Property(x => x.StartDate).HasElementName("startDate");
            cover.Property(x => x.EndDate).HasElementName("endDate");
            cover.Property(x => x.Type).HasElementName("claimType");
            cover.Property(x => x.Premium).HasElementName("premium");
        }
    }
}
