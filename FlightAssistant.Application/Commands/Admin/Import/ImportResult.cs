namespace FlightAssistant.Application.Commands.Admin.Import;

public record ImportResult(int Inserted, int Updated, IReadOnlyList<string> Errors);
