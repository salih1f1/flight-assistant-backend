namespace FlightAssistant.Domain.Common
{
    internal class SoftDeletableEntity : AuditableEntity
    {
        public bool IsDeleted { get; protected set; }
        public DateTime? DeletedUtc { get; protected set; }
        public Guid? DeletedBy { get; protected set; }

        public void SoftDelete(Guid? userId = null)
        {
            if (IsDeleted) return;
            IsDeleted = true;
            DeletedUtc = DateTime.UtcNow;
            DeletedBy = userId;
        }
    }
}
