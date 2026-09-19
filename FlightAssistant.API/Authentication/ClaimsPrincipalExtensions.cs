using System.Security.Claims;
using FlightAssistant.Domain.Common.Exceptions;

namespace FlightAssistant.API.Authentication
{
    public static class ClaimsPrincipalExtensions
    {
        public static Guid GetTravelerId(this ClaimsPrincipal user)
        {
            var id = user.FindFirstValue("oid") ?? user.FindFirstValue("sub");
            if (string.IsNullOrEmpty(id))
                throw new DomainException("No traveler identity claim found.", 401);
            if (!Guid.TryParse(id, out var guid))
                throw new DomainException("Traveler identity claim is not a valid identifier.", 401);
            return guid;
        }

        /// <summary>
        /// The admin actor an admin endpoint records as <c>VerifiedBy</c> or <c>requested_by</c>: the
        /// <c>oid</c> claim, then <c>sub</c>, then <c>"admin"</c>. The one copy of that rule for endpoints;
        /// the <c>/admin</c> group filter reads the same two claims for <c>IAdminContext.ActorId</c> but
        /// keeps it null, because no actor there means no audit row.
        /// </summary>
        public static string GetAdminActorId(this ClaimsPrincipal user) =>
            user.FindFirstValue("oid") ?? user.FindFirstValue("sub") ?? "admin";

        /// <summary>
        /// Whether the token carries the given API scope. The one copy of this rule (#413), read by
        /// the <c>AccessAsUser</c> authorization policy that replaced the per-handler
        /// <c>VerifyUserHasAnyAcceptedScope</c> calls.
        ///
        /// <para>
        /// Both claim types are accepted because both occur: Entra issues <c>scp</c> on a v2 access
        /// token, while the v1 URI form is what <c>ClaimsIdentity</c> ends up with in some
        /// configurations, and Microsoft.Identity.Web's own scope check reads both. The value is
        /// <b>space-delimited</b> — a token granted more than one scope carries
        /// <c>"access_as_user Files.Read"</c> in a single claim — so this splits rather than
        /// comparing the whole string, which would reject exactly the multi-scope tokens it should
        /// admit.
        /// </para>
        /// </summary>
        public static bool HasScope(this ClaimsPrincipal user, string scope)
        {
            foreach (var claim in user.Claims)
            {
                if (claim.Type is not ("scp" or "http://schemas.microsoft.com/identity/claims/scope"))
                    continue;

                foreach (var granted in claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (string.Equals(granted, scope, StringComparison.Ordinal))
                        return true;
                }
            }

            return false;
        }
    }
}
