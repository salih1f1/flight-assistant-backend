using FlightAssistant.Domain.Loyalty.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FlightAssistant.Infrastructure.Persistance.Configurations.Loyalty
{
    public sealed class LoyaltyProgramKindConfiguration : IEntityTypeConfiguration<LoyaltyProgramKind>
    {
        public void Configure(EntityTypeBuilder<LoyaltyProgramKind> b)
        {
            b.ToTable("loyalty_program_kinds");
            b.HasKey(x => x.Id);
            b.Property(x => x.Code).IsRequired().HasMaxLength(24);
            b.Property(x => x.Name).IsRequired().HasMaxLength(100);
            b.Property(x => x.Description).HasMaxLength(4000);
            b.HasIndex(x => x.Code).IsUnique();
        }
    }
}
