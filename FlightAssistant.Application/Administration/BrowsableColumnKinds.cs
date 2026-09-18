namespace FlightAssistant.Application.Administration
{
    /// <summary>
    /// What the admin data browser can do with a column, as <c>GET /admin/data/tables</c> publishes
    /// it. Each kind is a promise to the panel about the control to render — and, because
    /// <c>BrowsableDataRepository.ApplyFilter</c> branches on the same classifier, a promise the
    /// repository will honour rather than answer with a 400.
    /// </summary>
    public enum BrowsableColumnKind
    {
        /// <summary>Case-insensitive <c>contains</c>, plus the <c>*</c> "has a value" sentinel.</summary>
        Text,
        /// <summary>Exact match on a parsed number.</summary>
        Number,
        /// <summary>Exact match on <c>true</c>/<c>false</c>.</summary>
        Bool,
        /// <summary>Exact match, or "on or after" with a leading <c>&gt;=</c> (#334).</summary>
        Date,
        /// <summary>Exact match on one of <see cref="BrowsableColumn.Values"/>, parsed by name.</summary>
        Enum,
        /// <summary>Exact match on a parsed GUID.</summary>
        Guid,
        /// <summary>Shown, never filtered: <c>ConvertFilterValue</c> has no conversion for this type,
        /// so a filter on it is a 400. Published rather than hidden — a column the browser can show
        /// but not filter is a real state, and the panel disables the control for it.</summary>
        Unsupported,
    }

    /// <summary>One column of a browsable table, as the panel needs to see it.</summary>
    /// <param name="Values">The names a value may take, for <see cref="BrowsableColumnKind.Enum"/>
    /// alone; null for every other kind, which is what tells the panel to render something other
    /// than a picker.</param>
    /// <param name="Sensitive">Whether <c>SensitiveProperties</c> masks this column — its value is
    /// replaced with the redacted placeholder in every response, and a filter on it is a 400
    /// (#485). Independent of <paramref name="Kind"/>, which stays the column's real type:
    /// a client that only learned "sensitive" by noticing a whole page of redacted values cannot
    /// learn it at all from a page with no rows.</param>
    public sealed record BrowsableColumn(
        string Name, BrowsableColumnKind Kind, IReadOnlyList<string>? Values, bool Sensitive);

    /// <summary>
    /// Classifies a browsable column's CLR type. The single source of truth for which types the data
    /// browser can filter: <c>BrowsableDataRepository.ApplyFilter</c> reads it to choose its
    /// comparison, and <c>GetBrowsableTablesQuery</c> reads it to tell the panel what to render. Two
    /// separate judgements about the same type would drift, and the drift would be a control that
    /// promises a filter the repository rejects.
    /// </summary>
    public static class BrowsableColumnKinds
    {
        /// <summary>The kind for <paramref name="type"/>, unwrapping <c>Nullable&lt;T&gt;</c> first:
        /// a nullable column filters exactly like its underlying type.</summary>
        public static BrowsableColumnKind Classify(Type type)
        {
            var underlying = Nullable.GetUnderlyingType(type) ?? type;

            if (underlying == typeof(string)) return BrowsableColumnKind.Text;
            if (underlying == typeof(System.Guid)) return BrowsableColumnKind.Guid;
            if (underlying == typeof(bool)) return BrowsableColumnKind.Bool;
            if (underlying.IsEnum) return BrowsableColumnKind.Enum;
            if (IsDate(underlying)) return BrowsableColumnKind.Date;
            if (IsNumber(underlying)) return BrowsableColumnKind.Number;
            return BrowsableColumnKind.Unsupported;
        }

        /// <summary>The types a <c>&gt;=</c> filter compares as dates rather than rejecting.</summary>
        public static bool IsDate(Type type) =>
            type == typeof(DateTime) || type == typeof(DateTimeOffset) || type == typeof(DateOnly);

        private static bool IsNumber(Type type) =>
            type == typeof(int) || type == typeof(long) || type == typeof(short)
            || type == typeof(double) || type == typeof(float) || type == typeof(decimal);

        /// <summary>
        /// The column as the listing publishes it, values and all. <paramref name="sensitive"/> is
        /// passed in rather than worked out here: it is a fact about the property on its entity,
        /// which this class — which knows only CLR types — has no way to ask about.
        /// </summary>
        public static BrowsableColumn Describe(Type type, string name, bool sensitive)
        {
            var kind = Classify(type);
            var values = kind == BrowsableColumnKind.Enum
                ? Enum.GetNames(Nullable.GetUnderlyingType(type) ?? type)
                : null;

            return new BrowsableColumn(name, kind, values, sensitive);
        }
    }
}
