using FlightAssistant.Domain.Lounges.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FlightAssistant.Infrastructure.Persistance.Configurations.Loinges
{
    public sealed class LoungeProviderRefConfiguration : IEntityTypeConfiguration<LoungeProvider>
    {
        public void Configure(EntityTypeBuilder<LoungeProvider> b)
        {
            b.ToTable("lounge_providers");
            b.HasKey(x => x.Id);
            b.Property(x => x.Code).IsRequired().HasMaxLength(32);
            b.Property(x => x.Name).IsRequired().HasMaxLength(100);
            b.Property(x => x.Kind).HasConversion<int>();
            b.Property(x => x.Description).HasMaxLength(4000);
            b.HasIndex(x => x.Code).IsUnique();
        }
    }
}
