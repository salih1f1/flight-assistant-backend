namespace FlightAssistant.Contracts.Paging;

public record PagedResult<T>(IReadOnlyList<T> Items, int Total, int Page, int PageSize);
