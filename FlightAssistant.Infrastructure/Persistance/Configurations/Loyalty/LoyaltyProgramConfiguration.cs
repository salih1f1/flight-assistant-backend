using FlightAssistant.Domain.Loyalty.Entities;
using FlightAssistant.Domain.ReferenceData.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FlightAssistant.Infrastructure.Persistance.Configurations.Loyalty
{
    public sealed class LoyaltyProgramConfiguration : IEntityTypeConfiguration<LoyaltyProgram>
    {
        public void Configure(EntityTypeBuilder<LoyaltyProgram> b)
        {
            b.ToTable("loyalty_programs");
            b.HasKey(x => x.Id);

            b.Property(x => x.Code).IsRequired().HasMaxLength(16);
            b.Property(x => x.Name).IsRequired().HasMaxLength(200);
            b.Property(x => x.AllianceCode).HasMaxLength(8);

            b.HasIndex(x => x.Code).IsUnique();

            b.HasOne<LoyaltyProgramKind>(x => x.Kind)
                .WithMany(k => k.Programs)
                .HasForeignKey(x => x.KindId)
                .OnDelete(DeleteBehavior.Restrict);

            b.HasOne<Airline>()
                .WithMany()
                .HasForeignKey(x => x.AirlineId)
                .OnDelete(DeleteBehavior.SetNull);
        }
    }
}
