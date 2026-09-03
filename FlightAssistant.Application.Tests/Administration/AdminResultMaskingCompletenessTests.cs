using System.Reflection;
using FlightAssistant.Application.Administration;
using FlightAssistant.Application.Queries.Admin.Travelers;
using FlightAssistant.Domain.Loyalty.Entities;
using FlightAssistant.Domain.Travelers.Entities;

namespace FlightAssistant.Application.Tests.Administration
{
    /// <summary>
    /// The #321 guard: fixing <c>GetAllTravelersQuery</c> and <c>GetAdminTravelerByIdQuery</c> stops
    /// nothing recurring on its own — it only closes the two leaks that already shipped. This is what
    /// stops the *next* admin query from reopening the same hole, in the same shape as
    /// <c>BrowsableTablesModelCompletenessTests</c> (Infrastructure.Tests): reflect over the real
    /// types and check them against an explicit, reviewed registry, rather than trusting whoever
    /// writes the next handler to remember A4/A5/A6's masking posture unprompted.
    ///
    /// <para>
    /// Every <c>*Result</c> record under <c>FlightAssistant.Application.Queries.Admin.*</c> is
    /// scanned for a public property whose *name* collides with one registered in
    /// <see cref="SensitiveProperties"/> for *any* entity (not just whichever entity that particular
    /// query happens to read) — a name collision is the cheapest possible signal that a handler might
    /// be handing out a sensitive value, and cheap-but-loud beats precise-but-silent for a guard like
    /// this. A colliding property must appear in <see cref="ReviewedAsMasked"/>, an explicit list a
    /// human added only after confirming the handler actually masks it (the behavioural, raw-response
    /// tests in <c>FlightAssistant.API.Tests</c> are that confirmation) — so a new admin result
    /// exposing <c>Email</c>, <c>Token</c> or <c>AccountNumber</c> fails the build until someone
    /// classifies it, exactly the way an unclassified entity or column already fails
    /// <c>BrowsableTablesModelCompletenessTests</c>.
    /// </para>
    ///
    /// <para>
    /// This lives beside <see cref="SensitivePropertiesTests"/> and <see cref="BrowsableTablesTests"/>
    /// rather than in Infrastructure.Tests: unlike <c>BrowsableTablesModelCompletenessTests</c>' entity
    /// and column guards, this one needs no EF model — only reflection over the Application assembly
    /// and the registry itself, both of which Application.Tests already references.
    /// </para>
    /// </summary>
    public class AdminResultMaskingCompletenessTests
    {
        /// <summary>
        /// Every property name registered as sensitive for any entity — sourced from
        /// <see cref="SensitiveProperties.RegisteredEntityTypes"/> rather than retyped as a literal
        /// <c>{"Email", "Token", "AccountNumber"}</c> set, so this guard tracks the registry
        /// automatically instead of becoming a second copy of it that can silently fall behind.
        /// </summary>
        private static readonly IReadOnlySet<string> SensitivePropertyNames =
            SensitiveProperties.RegisteredEntityTypes
                .SelectMany(SensitiveProperties.GetSensitiveProperties)
                .ToHashSet(StringComparer.Ordinal);

        /// <summary>
        /// (result type, property name) pairs that legitimately expose a sensitive-named property
        /// because the owning handler masks it before the result is ever constructed. Adding an entry
        /// here is a deliberate decision, not busywork to silence the test — it must be paired with a
        /// handler that actually masks the value (proved by a behavioural test asserting against the
        /// raw response body, per <c>AdminTravelerEndpointTests</c>), because this list existing is
        /// not itself proof of anything.
        /// </summary>
        private static readonly IReadOnlySet<(Type ResultType, string Property)> ReviewedAsMasked =
            new HashSet<(Type, string)>
            {
                // #321 — GetAllTravelersQuery / GetAdminTravelerByIdQuery share this result type;
                // AdminTravelerResult.FromEntity masks Email for both.
                (typeof(AdminTravelerResult), nameof(AdminTravelerResult.Email)),

                // #322/A6 — GetAdminTravelerDetailQueryHandler masks all three explicitly.
                (typeof(AdminTravelerDetailResult), nameof(AdminTravelerDetailResult.Email)),
                (typeof(AdminDeviceTokenResult), nameof(AdminDeviceTokenResult.Token)),
                (typeof(AdminMembershipResult), nameof(AdminMembershipResult.AccountNumber)),
            };

        /// <summary>
        /// Every <c>*Result</c> record type under <c>FlightAssistant.Application.Queries.Admin.*</c>
        /// — reflection over the real assembly, not a hand-typed list of "the queries we know about",
        /// so a new file in that namespace is picked up automatically. Scoped to <c>Queries.Admin</c>
        /// only, matching the #321 brief: traveler-facing self-service queries (e.g.
        /// <c>GetTravelerMembershipsQuery</c>, under <c>Queries.Loyalty</c>) are deliberately out of
        /// reach of this guard — a traveler reading their own membership number is not the bypass
        /// #321 closes, and masking it there would break the mobile app for no security gain.
        /// </summary>
        private static IEnumerable<Type> AdminResultTypes() =>
            typeof(SensitiveProperties).Assembly.GetTypes()
                .Where(t => t.IsClass
                    && t.Namespace is not null
                    && (t.Namespace == "FlightAssistant.Application.Queries.Admin"
                        || t.Namespace.StartsWith("FlightAssistant.Application.Queries.Admin.", StringComparison.Ordinal))
                    && t.Name.EndsWith("Result", StringComparison.Ordinal));

        [Fact]
        public void EveryAdminResultProperty_MatchingASensitivePropertyName_IsReviewedAsMasked()
        {
            var unreviewed = new List<string>();

            foreach (var resultType in AdminResultTypes())
            {
                foreach (var property in resultType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (!SensitivePropertyNames.Contains(property.Name)) continue;
                    if (ReviewedAsMasked.Contains((resultType, property.Name))) continue;

                    unreviewed.Add($"{resultType.Name}.{property.Name}");
                }
            }

            Assert.True(
                unreviewed.Count == 0,
                "Admin result propert(y/ies) expose a name registered as sensitive in " +
                "SensitiveProperties without a reviewed masking decision. Confirm the owning handler " +
                "actually masks the value (see AdminTravelerEndpointTests for the raw-response-body " +
                "pattern), then add (type, property) to ReviewedAsMasked: " +
                string.Join(", ", unreviewed));
        }

        // ── Sanity checks on the guard's own inputs ─────────────────────────────────
        // Mirrors BrowsableTablesModelCompletenessTests' own habit of testing the guard's building
        // blocks, not just relying on the one guard test above to exercise them correctly.

        [Fact]
        public void AdminResultTypes_FindsTheKnownTravelerResults()
        {
            var found = AdminResultTypes().ToHashSet();

            Assert.Contains(typeof(AdminTravelerResult), found);
            Assert.Contains(typeof(AdminTravelerDetailResult), found);
            Assert.Contains(typeof(AdminDeviceTokenResult), found);
            Assert.Contains(typeof(AdminMembershipResult), found);
        }

        [Fact]
        public void AdminResultTypes_ExcludesTravelerFacingSelfServiceResults()
        {
            // MembershipResult (Queries.Loyalty.GetTravelerMembershipsQuery) is deliberately out of
            // scope — see the AdminResultTypes() doc comment. If this ever starts failing because
            // MembershipResult got renamed into the Admin namespace, that is exactly the kind of
            // change this guard exists to catch, not a false positive to silence.
            var found = AdminResultTypes().ToHashSet();

            Assert.DoesNotContain(found, t => t.Name == "MembershipResult");
        }

        [Fact]
        public void SensitivePropertyNames_ContainsEmailTokenAndAccountNumber()
        {
            Assert.Contains(nameof(Traveler.Email), SensitivePropertyNames);
            Assert.Contains(nameof(DeviceToken.Token), SensitivePropertyNames);
            Assert.Contains(nameof(TravelerLoyaltyMembership.AccountNumber), SensitivePropertyNames);
        }

        [Fact]
        public void EveryReviewedEntry_NamesARealPropertyOnItsResultType()
        {
            foreach (var (resultType, property) in ReviewedAsMasked)
            {
                var found = resultType.GetProperty(property, BindingFlags.Public | BindingFlags.Instance);
                Assert.True(found is not null, $"{resultType.Name}.{property} does not resolve via reflection.");
            }
        }

        [Fact]
        public void EveryReviewedEntry_NamesAnActualAdminResultType()
        {
            // Catches a stale ReviewedAsMasked entry left behind after a result type is renamed or
            // removed — otherwise the entry silently stops doing anything (nothing in AdminResultTypes()
            // would ever match it) rather than failing loudly.
            var adminResultTypes = AdminResultTypes().ToHashSet();

            foreach (var (resultType, _) in ReviewedAsMasked)
            {
                Assert.Contains(resultType, adminResultTypes);
            }
        }
    }
}
