using FlightAssistant.Domain.ReferenceData.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FlightAssistant.Infrastructure.Persistance.Configurations.ReferenceData
{
    public class CountryConfiguration : IEntityTypeConfiguration<Country>
    {
        public void Configure(EntityTypeBuilder<Country> b)
        {
            b.ToTable("countries");
            b.HasKey(x => x.Code);

            b.Property(x => x.Code).HasMaxLength(2).IsFixedLength();
            b.Property(x => x.Name).IsRequired().HasMaxLength(128);
            b.Property(x => x.Alpha3).HasMaxLength(3).IsFixedLength();
            b.Property(x => x.NumericCode);
            b.Property(x => x.Region).HasMaxLength(64);
            b.Property(x => x.Subregion).HasMaxLength(64);

            b.HasIndex(x => x.Alpha3).IsUnique().HasFilter("\"Alpha3\" IS NOT NULL");
            b.HasIndex(x => x.NumericCode).IsUnique().HasFilter("\"NumericCode\" IS NOT NULL");
        }
    }
}
