namespace FlightAssistant.Domain.Common
{
    public class AuditableEntity : BaseEntity
    {
        public DateTime CreatedUtc { get; protected set; } = DateTime.UtcNow;

        public DateTime? UpdatedUtc { get; protected set; }

        public void SetCreated()
        {
            CreatedUtc = DateTime.UtcNow;
        }

        public void SetUpdated()
        {
            UpdatedUtc = DateTime.UtcNow;
        }
    }
}
