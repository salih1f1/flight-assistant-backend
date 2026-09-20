using System.Security.Claims;
using FlightAssistant.Domain.Common.Exceptions;

namespace FlightAssistant.API.Authentication
{
    public static class ClaimsPrincipalExtensions
    {
        private static readonly string[] EmailClaimTypes = ["email", "preferred_username", "emails"];

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
        /// The traveler's email address, read from the validated token rather than from anything the
        /// caller sent. The one copy of that rule.
        ///
        /// <para>
        /// Three claims are accepted because the tenant emits different ones: Entra External ID puts
        /// the sign-in address in <c>preferred_username</c> (and only there — a captured dev token
        /// carries no <c>email</c> claim at all), while a B2C local account emits <c>emails</c>, an
        /// array, of which the first entry is the sign-in address. <c>email</c> is preferred where the
        /// tenant is configured to emit the optional claim, since it is the one that means an address
        /// and nothing else. A blank claim is treated as absent rather than as an answer, so a
        /// tenant emitting an empty <c>email</c> still falls through to the claim that has a value.
        /// </para>
        ///
        /// <para>
        /// Throws 401, like <see cref="GetTravelerId"/> does for a missing <c>oid</c>, rather than
        /// registering a traveler with no address: every one of these claims being absent means the
        /// token is not the one this API was configured for, which is a loud failure, not a blank
        /// profile field. It is deliberately not a validation 400 — the caller sends no body here, so
        /// an <c>Email</c> error would point them at a field they never controlled.
        /// </para>
        /// </summary>
        public static string GetEmail(this ClaimsPrincipal user)
        {
            foreach (var claimType in EmailClaimTypes)
            {
                var value = user.FindFirstValue(claimType);
                if (!string.IsNullOrWhiteSpace(value))
                    return value;
            }

            throw new DomainException("No traveler email claim found.", 401);
        }

        /// <summary>
        /// The traveler's display name, from the token's <c>name</c> claim — null when the tenant
        /// does not emit one, since a display name is optional everywhere in this domain. Read from
        /// the token for the same reason <see cref="GetEmail"/> is: Azure owns it, and
        /// <c>PATCH /travelers/me</c> already refuses to edit it.
        /// </summary>
        public static string? GetDisplayName(this ClaimsPrincipal user)
        {
            var name = user.FindFirstValue("name");
            return string.IsNullOrWhiteSpace(name) ? null : name;
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
