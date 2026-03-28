# DDD Rules

## Aggregate invariants
- Aggregate methods enforce invariants; state is not set directly from outside.
- Construction via factories or static Create methods if invariants exist.

## Domain events
- Added when a business fact occurs (past tense).
- Stored in aggregate and published by application layer after persistence.

## Cross-context
- No direct references between contexts.
- Use anti-corruption layer or integration events if needed.
