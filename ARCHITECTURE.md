# Architecture (Flight Assistant)

## Layering
- API: Controllers, auth, request shaping. No business rules.
- Application: Use-cases, MediatR handlers, DTO mapping, transactions.
- Domain: Aggregates, entities, value objects, invariants, domain events.
- Infrastructure: EF Core, external API clients, caching, messaging.

## Key use-cases
- Track flight manually (flight number + date) -> fetch status -> persist -> notify on changes.
- Visa lookup -> cache result -> attach to itinerary / destination.
- Lounge eligibility -> evaluate based on loyalty tier + airline + terminal.

## Bounded Contexts (initial)
- Flights
- Visa
- Loyalty
- Lounges
