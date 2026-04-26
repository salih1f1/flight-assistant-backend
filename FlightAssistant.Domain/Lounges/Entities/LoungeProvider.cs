using FlightAssistant.Domain.Lounges.Enums;

namespace FlightAssistant.Domain.Lounges.Entities
{
    public sealed class LoungeProvider
    {
        public int Id { get; private set; }
        public string Code { get; private set; } = default!;
        public string Name { get; private set; } = default!;
        public LoungeProviderKind Kind { get; private set; }
        public string? Description { get; private set; }
        public ICollection<LoungeProviderLink> LoungeLinks { get; private set; } = new List<LoungeProviderLink>();

        private LoungeProvider() { }

        public static LoungeProvider Create(string code, string name, LoungeProviderKind kind, string? description = null) =>
            new() { Code = code, Name = name, Kind = kind, Description = description };
    }
}
