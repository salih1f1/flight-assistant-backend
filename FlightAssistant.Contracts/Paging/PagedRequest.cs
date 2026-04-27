namespace FlightAssistant.Contracts.Paging;

public record PagedRequest(int Page = 1, int PageSize = 50);
