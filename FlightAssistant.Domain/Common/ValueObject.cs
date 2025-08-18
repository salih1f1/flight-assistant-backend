namespace FlightAssistant.Domain.Common
{
    public abstract class ValueObject
    {
        protected abstract IEnumerable<object?> GetEqualityComponents();

        public override bool Equals(object? obj)
        {
            if (obj is not ValueObject other) return false;
            return GetEqualityComponents().SequenceEqual(other.GetEqualityComponents());
        }

        public override int GetHashCode() =>
            GetEqualityComponents()
                .Aggregate(0, (acc, comp) => HashCode.Combine(acc, comp));

        public static bool operator ==(ValueObject? a, ValueObject? b) => Equals(a, b);
        public static bool operator !=(ValueObject? a, ValueObject? b) => !Equals(a, b);
    }
}
