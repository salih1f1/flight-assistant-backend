namespace FlightAssistant.Domain.Common
{
    public class AuditableEntity : BaseEntity
    {
        public DateTime CreatedUtc { get; protected set; } = DateTime.UtcNow;
        public Guid? CreatedBy { get; protected set; }

        public DateTime? UpdatedUtc { get; protected set; }
        public Guid? UpdatedBy { get; protected set; }

        public void SetCreated(Guid? userId = null)
        {
            CreatedUtc = DateTime.UtcNow;
            CreatedBy = userId;
        }

        public void SetUpdated(Guid? userId = null)
        {
            UpdatedUtc = DateTime.UtcNow;
            UpdatedBy = userId;
        }
    }
}
