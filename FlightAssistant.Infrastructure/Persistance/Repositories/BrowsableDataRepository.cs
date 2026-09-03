using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;
using FlightAssistant.Application.Administration;
using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Domain.Common.Exceptions;
using Microsoft.EntityFrameworkCore;

namespace FlightAssistant.Infrastructure.Persistance.Repositories
{
    /// <summary>
    /// Implements <see cref="IBrowsableDataRepository"/> for any entity type registered in
    /// <c>BrowsableTables</c> — one implementation, dispatched at runtime to a strongly-typed
    /// generic method per <see cref="BrowsableTable.EntityType"/> via reflection
    /// (<see cref="MakeGenericQuery{TResult}"/>), because <see cref="FlightAssistantDbContext"/>
    /// only exposes a strongly-typed <c>Set&lt;TEntity&gt;()</c> and every LINQ operator this class
    /// needs (<c>Where</c>, <c>OrderBy</c>, <c>CountAsync</c>) requires that strongly-typed
    /// <see cref="IQueryable{T}"/> to compose and translate correctly. Everything below the
    /// dispatch point is ordinary generic C#; the only place a column name (a runtime string) has
    /// to become part of a LINQ expression tree is <see cref="PropertyKeySelector{TEntity}"/> and
    /// <see cref="ApplyFilter{TEntity}"/>, both built on <c>EF.Property&lt;TProperty&gt;</c> so the
    /// column resolves to the mapped column regardless of the generic <c>TEntity</c> in play.
    /// </summary>
    public class BrowsableDataRepository : IBrowsableDataRepository
    {
        private readonly FlightAssistantDbContext _db;

        public BrowsableDataRepository(FlightAssistantDbContext db) => _db = db;

        public Task<int> CountAsync(BrowsableTable table, CancellationToken ct = default) =>
            MakeGenericQuery<Task<int>>(table.EntityType, nameof(CountGenericAsync), ct);

        public Task<(IReadOnlyList<IReadOnlyDictionary<string, object?>> Items, int Total)> QueryAsync(
            BrowsableTable table,
            int page,
            int pageSize,
            string? sortColumn,
            bool sortDescending,
            string? filterColumn,
            string? filterValue,
            CancellationToken ct = default) =>
            MakeGenericQuery<Task<(IReadOnlyList<IReadOnlyDictionary<string, object?>>, int)>>(
                table.EntityType, nameof(QueryGenericAsync),
                table, page, pageSize, sortColumn, sortDescending, filterColumn, filterValue, ct);

        /// <summary>Invokes the named private generic method (<paramref name="methodName"/>) of
        /// this class with its generic parameter bound to <paramref name="entityType"/>, forwarding
        /// <paramref name="args"/> positionally. The one reflection seam in this class — every
        /// per-request query composition below it is ordinary compiled C#.</summary>
        private TResult MakeGenericQuery<TResult>(Type entityType, string methodName, params object?[] args)
        {
            var method = typeof(BrowsableDataRepository)
                .GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Instance)!
                .MakeGenericMethod(entityType);
            return (TResult)method.Invoke(this, args)!;
        }

        /// <summary>
        /// Test-only seam (see <c>InternalsVisibleTo("FlightAssistant.Infrastructure.Tests")</c>):
        /// builds the exact filtered/sorted query <see cref="QueryAsync"/> composes, without
        /// executing it, so a Postgres-backed test can assert on
        /// <see cref="EntityFrameworkQueryableExtensions.ToQueryString{TSource}(IQueryable{TSource})"/>
        /// that the case-insensitive string filter genuinely translates to SQL — proof the
        /// production code path takes, not a hand-rebuilt stand-in query that would only prove the
        /// pattern is translatable in the abstract.
        /// </summary>
        internal string BuildQueryString(
            BrowsableTable table, string? sortColumn, bool sortDescending, string? filterColumn, string? filterValue) =>
            MakeGenericQuery<string>(
                table.EntityType, nameof(BuildQueryStringGeneric), sortColumn, sortDescending, filterColumn, filterValue);

        private string BuildQueryStringGeneric<TEntity>(
            string? sortColumn, bool sortDescending, string? filterColumn, string? filterValue) where TEntity : class
        {
            IQueryable<TEntity> query = _db.Set<TEntity>().AsNoTracking();
            if (filterColumn is not null)
                query = ApplyFilter(query, filterColumn, filterValue ?? string.Empty);
            query = ApplySort(query, sortColumn, sortDescending);
            return query.ToQueryString();
        }

        public Task<RevealedValue?> GetValueAsync(
            BrowsableTable table, string id, string column, CancellationToken ct = default) =>
            MakeGenericQuery<Task<RevealedValue?>>(
                table.EntityType, nameof(GetValueGenericAsync), table, id, column, ct);

        private Task<int> CountGenericAsync<TEntity>(CancellationToken ct) where TEntity : class =>
            _db.Set<TEntity>().AsNoTracking().CountAsync(ct);

        /// <summary>
        /// The <see cref="IBrowsableDataRepository.GetValueAsync"/> implementation. Deliberately
        /// does not reuse <see cref="ApplyFilter{TEntity}"/> for the equality half of this, even
        /// though that method already builds an <c>EF.Property</c> equality expression off a string
        /// value for a non-string column: <see cref="ApplyFilter{TEntity}"/>'s string branch is a
        /// case-insensitive <em>contains</em>, not an exact match, and a handful of browsable
        /// tables key on a string column (<c>Country.Code</c>). An id lookup that quietly degraded
        /// to "contains" for such a table would be a correctness bug, not just a style
        /// inconsistency, so the predicate here is built directly instead — it does reuse
        /// <see cref="ConvertFilterValue"/>, the actual value-parsing logic <see cref="ApplyFilter{TEntity}"/>
        /// itself calls internally, so there remains exactly one place a filter/id value is ever
        /// converted from its raw string form. <see cref="ConvertFilterValue"/> parses a string key
        /// part as itself (#318 final review, finding 1) rather than throwing, so this exact-match
        /// guarantee actually applies to every key type this codebase uses, not just the ones
        /// <see cref="ApplyFilter{TEntity}"/>'s non-string branch already covered.
        /// </summary>
        private async Task<RevealedValue?> GetValueGenericAsync<TEntity>(
            BrowsableTable table, string id, string column, CancellationToken ct) where TEntity : class
        {
            var entityType = _db.Model.FindEntityType(typeof(TEntity))
                ?? throw new InvalidOperationException($"{typeof(TEntity).Name} is not part of the EF model.");
            var keyProperties = entityType.FindPrimaryKey()?.Properties
                ?? throw new InvalidOperationException($"{typeof(TEntity).Name} has no primary key.");

            // Composite keys stringify with '|' between parts (AdminAuditInterceptor.GetEntityId
            // uses the same separator, for the same reason: neither format ever appears inside a
            // Guid, int, or any other key part this codebase uses). A part count that doesn't match
            // the key's own arity cannot be this table's id in any interpretation, so it is "no
            // such row" rather than an error.
            var idParts = id.Split('|');
            if (idParts.Length != keyProperties.Count) return null;

            Expression? predicateBody = null;
            var parameter = Expression.Parameter(typeof(TEntity), "e");

            for (var i = 0; i < keyProperties.Count; i++)
            {
                var propertyType = keyProperties[i].ClrType;
                var underlyingType = Nullable.GetUnderlyingType(propertyType) ?? propertyType;

                object typedValue;
                try
                {
                    // Reuses the exact conversion ApplyFilter's non-string branch calls — the only
                    // difference here is what the resulting Expression.Equal is combined into
                    // (AndAlso across every key part, for a composite key) and that a value which
                    // fails to parse means "not found", not a 400: an id the caller mistyped is a
                    // 404 like any other unmatched id, not a client error about a filter's shape.
                    typedValue = ConvertFilterValue(idParts[i], underlyingType);
                }
                catch (DomainException)
                {
                    return null;
                }

                var propertyAccess = Expression.Call(
                    typeof(EF), nameof(EF.Property), new[] { propertyType },
                    parameter, Expression.Constant(keyProperties[i].Name));
                Expression valueExpression = Expression.Constant(typedValue, underlyingType);
                if (underlyingType != propertyType)
                    valueExpression = Expression.Convert(valueExpression, propertyType);

                var equals = Expression.Equal(propertyAccess, valueExpression);
                predicateBody = predicateBody is null ? equals : Expression.AndAlso(predicateBody, equals);
            }

            var predicate = Expression.Lambda<Func<TEntity, bool>>(predicateBody!, parameter);
            var entity = await _db.Set<TEntity>().AsNoTracking().FirstOrDefaultAsync(predicate, ct);
            if (entity is null) return null;

            // Exactly one column is ever read off the entity here — this is what makes
            // GetValueAsync safe to call for a reveal (#318 final review, finding 4: this used to
            // project every column table.Columns exposes into a whole-row dictionary, materializing
            // far more than the one value a reveal ever wants). Never masked: masking is applied by
            // every other consumer of this repository (GetBrowsableDataQueryHandler.Mask); the
            // reveal handler is the one caller allowed to see a raw value, and only for the single
            // column it has already confirmed is both exposed and sensitive.
            var property = typeof(TEntity).GetProperty(column)
                ?? throw new InvalidOperationException(
                    $"BrowsableTables registers column '{column}' for '{table.Slug}' but {typeof(TEntity).Name} has no such property.");
            var value = property.GetValue(entity);

            // The canonical id, read off the same EF key metadata (keyProperties, above) that
            // AdminAuditInterceptor.GetEntityId reads off EntityEntry.Metadata.FindPrimaryKey() —
            // .ToString() on each part, joined with '|' for a composite key, identically. Built from
            // the loaded entity's own key values, never from the caller's raw id string: id may
            // have resolved this row while carrying a non-canonical spelling (e.g. a
            // braced/uppercase Guid), and echoing that back would let a reveal's audit row drift
            // from the format every interceptor-written row uses (#318 final review, finding 2).
            var entityId = string.Join("|", keyProperties.Select(p =>
                typeof(TEntity).GetProperty(p.Name)!.GetValue(entity)?.ToString() ?? string.Empty));

            return new RevealedValue(entityId, value);
        }

        private async Task<(IReadOnlyList<IReadOnlyDictionary<string, object?>> Items, int Total)> QueryGenericAsync<TEntity>(
            BrowsableTable table,
            int page,
            int pageSize,
            string? sortColumn,
            bool sortDescending,
            string? filterColumn,
            string? filterValue,
            CancellationToken ct) where TEntity : class
        {
            IQueryable<TEntity> query = _db.Set<TEntity>().AsNoTracking();

            if (filterColumn is not null)
                query = ApplyFilter(query, filterColumn, filterValue ?? string.Empty);

            // Total must be counted after the filter above and before Skip/Take — the classic bug
            // in this shape is a Total computed against the unfiltered table.
            var total = await query.CountAsync(ct);

            query = ApplySort(query, sortColumn, sortDescending);

            var entities = await query
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync(ct);

            // Projected in memory, not via another EF.Property expression: the rows are already
            // materialized POCOs at this point, so plain reflection over table.Columns is enough,
            // and it keeps the exposed-column allowlist doing double duty as the projection list —
            // a column absent from table.Columns is never read off the entity at all.
            var properties = table.Columns
                .Select(c => typeof(TEntity).GetProperty(c)
                    ?? throw new InvalidOperationException(
                        $"BrowsableTables registers column '{c}' for '{table.Slug}' but {typeof(TEntity).Name} has no such property."))
                .ToArray();

            var items = entities
                .Select(entity =>
                {
                    var row = new Dictionary<string, object?>(properties.Length);
                    foreach (var property in properties)
                        row[property.Name] = property.GetValue(entity);
                    return (IReadOnlyDictionary<string, object?>)row;
                })
                .ToList();

            return (items, total);
        }

        /// <summary>
        /// Applies the caller's sort column (if any) and then always appends the entity's primary
        /// key, read from live EF metadata rather than assumed to be a property called "Id" — several
        /// browsable entities key on something else entirely (<c>Country</c> on <c>Code</c>,
        /// <c>SeedVersion</c> on <c>(Dataset, Version)</c>, <c>LoungeProviderLink</c> on
        /// <c>(LoungeId, ProviderId, Role)</c>). Without this tiebreaker, PostgreSQL does not
        /// guarantee stable ordering across separate query executions for rows tied on the
        /// requested sort column alone — a table with no naturally unique sort would let a row
        /// appear on two pages, or on none, as pages are fetched one at a time.
        /// </summary>
        private IQueryable<TEntity> ApplySort<TEntity>(IQueryable<TEntity> query, string? sortColumn, bool sortDescending)
            where TEntity : class
        {
            IOrderedQueryable<TEntity>? ordered = null;

            if (sortColumn is not null)
            {
                var keySelector = PropertyKeySelector<TEntity>(sortColumn);
                ordered = sortDescending ? query.OrderByDescending(keySelector) : query.OrderBy(keySelector);
            }

            var entityType = _db.Model.FindEntityType(typeof(TEntity))
                ?? throw new InvalidOperationException($"{typeof(TEntity).Name} is not part of the EF model.");
            var primaryKeyProperties = entityType.FindPrimaryKey()?.Properties
                ?? throw new InvalidOperationException($"{typeof(TEntity).Name} has no primary key.");

            foreach (var keyProperty in primaryKeyProperties)
            {
                // Never add the exact same column the caller already sorted on as a redundant
                // secondary key — harmless either way, but pointless.
                if (string.Equals(keyProperty.Name, sortColumn, StringComparison.Ordinal)) continue;

                var keySelector = PropertyKeySelector<TEntity>(keyProperty.Name);
                ordered = ordered is null ? query.OrderBy(keySelector) : ordered.ThenBy(keySelector);
            }

            // ordered is only ever null if TEntity's primary key has zero properties, which EF Core
            // itself does not allow — kept as a fallback rather than a throw so a future relaxation
            // of that EF invariant degrades to "unsorted" instead of a 500.
            return ordered ?? query;
        }

        /// <summary>
        /// <c>e =&gt; EF.Property&lt;object&gt;(e, propertyName)</c> — boxes every property value
        /// to <c>object</c> so one method builds the key selector for any column regardless of its
        /// real CLR type. This is safe specifically for <c>ORDER BY</c>: EF Core's query
        /// translator resolves <c>EF.Property</c> calls by looking up the named property against
        /// the entity's mapping, not by the generic type argument used to call it, so the boxing
        /// here does not change what column or collation the generated SQL sorts on.
        /// </summary>
        private static Expression<Func<TEntity, object>> PropertyKeySelector<TEntity>(string propertyName)
        {
            var parameter = Expression.Parameter(typeof(TEntity), "e");
            var propertyAccess = Expression.Call(
                typeof(EF), nameof(EF.Property), new[] { typeof(object) }, parameter, Expression.Constant(propertyName));
            return Expression.Lambda<Func<TEntity, object>>(propertyAccess, parameter);
        }

        /// <summary>
        /// Exact match for a non-string column; case-insensitive <c>contains</c> for a string
        /// column. Unlike <see cref="PropertyKeySelector{TEntity}"/>, this resolves the column's
        /// real CLR type via reflection and calls <c>EF.Property&lt;TProperty&gt;</c> with that
        /// exact type — needed so <c>.ToLower().Contains(...)</c> is available on a string column,
        /// and so an exact-match comparison on a non-string column compares against a
        /// correctly-typed constant instead of an opaque boxed <see cref="object"/> (which would
        /// either fail to translate or translate to the wrong SQL type). The string branch composes
        /// entirely out of translatable calls (<see cref="string.ToLower()"/>,
        /// <see cref="string.Contains(string)"/>) with no client-evaluated step, so it becomes real
        /// SQL rather than pulling the table into memory first — the usual way this pattern goes
        /// wrong.
        /// </summary>
        private static IQueryable<TEntity> ApplyFilter<TEntity>(IQueryable<TEntity> query, string columnName, string rawValue)
            where TEntity : class
        {
            var propertyInfo = typeof(TEntity).GetProperty(columnName)
                ?? throw new InvalidOperationException($"Column '{columnName}' does not exist on {typeof(TEntity).Name}.");
            var propertyType = propertyInfo.PropertyType;

            var parameter = Expression.Parameter(typeof(TEntity), "e");
            var propertyAccess = Expression.Call(
                typeof(EF), nameof(EF.Property), new[] { propertyType }, parameter, Expression.Constant(columnName));

            Expression body;
            if (propertyType == typeof(string))
            {
                var lowerValue = rawValue.ToLowerInvariant();
                var toLowerCall = Expression.Call(propertyAccess, typeof(string).GetMethod(nameof(string.ToLower), Type.EmptyTypes)!);
                var containsCall = Expression.Call(
                    toLowerCall, typeof(string).GetMethod(nameof(string.Contains), new[] { typeof(string) })!, Expression.Constant(lowerValue));
                // A null string column is never "contains"-matched, and skipping the null check
                // would let EF Core's null-semantics rewriting turn this into something that no
                // longer reads as a plain translated LIKE.
                var notNull = Expression.NotEqual(propertyAccess, Expression.Constant(null, typeof(string)));
                body = Expression.AndAlso(notNull, containsCall);
            }
            else
            {
                var underlyingType = Nullable.GetUnderlyingType(propertyType) ?? propertyType;
                var typedValue = ConvertFilterValue(rawValue, underlyingType);
                Expression valueExpression = Expression.Constant(typedValue, underlyingType);
                if (underlyingType != propertyType)
                    valueExpression = Expression.Convert(valueExpression, propertyType);
                body = Expression.Equal(propertyAccess, valueExpression);
            }

            var predicate = Expression.Lambda<Func<TEntity, bool>>(body, parameter);
            return query.Where(predicate);
        }

        /// <summary>
        /// Converts the raw filter value string to <paramref name="targetType"/> for an exact-match
        /// comparison. A <see cref="DateTime"/> column is parsed as <see cref="DateTimeOffset"/> and
        /// projected through <c>.UtcDateTime</c> — never <see cref="DateTime.Parse(string)"/>
        /// directly — because a bare date/time string with no offset (e.g. <c>2026-09-01</c>) binds
        /// as <see cref="DateTimeKind.Unspecified"/>, which Npgsql refuses to write against a
        /// <c>timestamptz</c> column: an unhandled <see cref="ArgumentException"/>, surfaced as a
        /// 500. This is the same rule <c>AdminAuditEndpoints</c> follows at the API boundary
        /// (binding <c>DateTimeOffset?</c> there); here it is the equivalent choke point for a
        /// filter value that arrives as one opaque string rather than a typed query parameter. A
        /// value that fails to parse, or a column type this browser has no conversion for, is a 400
        /// <see cref="DomainException"/> — never an unhandled parse exception surfacing as a 500.
        ///
        /// <para>
        /// <b>A <see cref="string"/> target parses as itself.</b> Before #318's final review this
        /// method had no <see cref="string"/> branch at all, so a string-typed key column (e.g.
        /// <c>Country.Code</c>) fell through to the "not supported" throw below — caught by
        /// <see cref="GetValueGenericAsync{TEntity}"/> and turned into "no such row" for every id,
        /// even a correct one. <see cref="ApplyFilter{TEntity}"/>'s own string handling (a
        /// case-insensitive <c>contains</c>) never reaches this method — it branches before ever
        /// calling it — so this addition changes nothing about filter/sort semantics; it only makes
        /// an id lookup's string key part behave like every other key part instead of throwing.
        /// </para>
        /// </summary>
        private static object ConvertFilterValue(string rawValue, Type targetType)
        {
            if (targetType == typeof(string)) return rawValue;

            try
            {
                if (targetType == typeof(Guid)) return Guid.Parse(rawValue);
                if (targetType == typeof(bool)) return bool.Parse(rawValue);
                if (targetType.IsEnum) return Enum.Parse(targetType, rawValue, ignoreCase: true);
                if (targetType == typeof(DateTime))
                    return DateTimeOffset.Parse(
                        rawValue, CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal).UtcDateTime;
                if (targetType == typeof(DateTimeOffset))
                    return DateTimeOffset.Parse(rawValue, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
                if (targetType == typeof(DateOnly))
                    return DateOnly.Parse(rawValue, CultureInfo.InvariantCulture);
                if (targetType == typeof(int)) return int.Parse(rawValue, CultureInfo.InvariantCulture);
                if (targetType == typeof(long)) return long.Parse(rawValue, CultureInfo.InvariantCulture);
                if (targetType == typeof(short)) return short.Parse(rawValue, CultureInfo.InvariantCulture);
                if (targetType == typeof(double)) return double.Parse(rawValue, CultureInfo.InvariantCulture);
                if (targetType == typeof(float)) return float.Parse(rawValue, CultureInfo.InvariantCulture);
                if (targetType == typeof(decimal)) return decimal.Parse(rawValue, CultureInfo.InvariantCulture);
            }
            // ArgumentException is what Enum.Parse throws for a name it doesn't recognise — including
            // an empty string — not FormatException; without it here, an unparseable enum filter value
            // (e.g. filter=Action:NotARealAction) escaped uncaught and surfaced as an unhandled 500.
            catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentException)
            {
                throw new DomainException($"Invalid filter value '{rawValue}' for a {targetType.Name} column.", 400);
            }

            throw new DomainException($"Filtering on a {targetType.Name} column is not supported.", 400);
        }
    }
}
