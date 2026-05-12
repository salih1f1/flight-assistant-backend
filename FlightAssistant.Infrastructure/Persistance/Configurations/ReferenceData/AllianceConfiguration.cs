using FlightAssistant.Domain.ReferenceData.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FlightAssistant.Infrastructure.Persistance.Configurations.ReferenceData
{
    public sealed class AllianceConfiguration : IEntityTypeConfiguration<Alliance>
    {
        public void Configure(EntityTypeBuilder<Alliance> b)
        {
            b.ToTable("alliances");
            b.HasKey(x => x.Code);
            b.Property(x => x.Code).IsRequired().HasMaxLength(8);
            b.Property(x => x.Name).IsRequired().HasMaxLength(200);

            b.HasIndex(x => x.Code).IsUnique();

            b.Property(x => x.IsVerified).HasColumnName("is_verified").HasDefaultValue(false);
            b.Property(x => x.VerifiedAt).HasColumnName("verified_at");
            b.Property(x => x.VerifiedBy).HasColumnName("verified_by").HasMaxLength(255);
        }
    }
}
