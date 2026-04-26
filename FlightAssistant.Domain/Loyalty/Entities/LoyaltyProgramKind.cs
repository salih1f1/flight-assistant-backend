namespace FlightAssistant.Domain.Loyalty.Entities
{
    public sealed class LoyaltyProgramKind
    {
        public int Id { get; private set; }
        public string Code { get; private set; } = default!;
        public string Name { get; private set; } = default!;
        public string? Description { get; private set; }
        public ICollection<LoyaltyProgram> Programs { get; private set; } = new List<LoyaltyProgram>();

        private LoyaltyProgramKind() { }

        public static LoyaltyProgramKind Create(string code, string name, string? description = null) =>
            new() { Code = code, Name = name, Description = description };
    }
}
