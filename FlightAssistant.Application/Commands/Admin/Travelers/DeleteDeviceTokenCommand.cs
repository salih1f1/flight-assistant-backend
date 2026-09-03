using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Domain.Common.Exceptions;
using FluentValidation;
using MediatR;

namespace FlightAssistant.Application.Commands.Admin.Travelers
{
    /// <summary>
    /// Removes one stale APNs device token from a traveler. Backs
    /// <c>DELETE /admin/travelers/{travelerId}/device-tokens/{tokenId}</c>.
    ///
    /// <para>
    /// <b>Not a device revocation.</b> This only stops APNs pushes from reaching that token; the
    /// client re-registers a fresh one on its next launch. An operator reaching for this to "sign a
    /// device out" wants something this command does not do.
    /// </para>
    /// </summary>
    public sealed record DeleteDeviceTokenCommand(Guid TravelerId, Guid DeviceTokenId) : IRequest;

    /// <summary>
    /// Deliberately does not reuse <c>Commands.Travelers.RemoveDeviceTokenCommandHandler</c> (the
    /// traveler-facing self-service handler): that handler treats an unknown token as a silent
    /// no-op (<c>Traveler.RemoveDeviceToken</c> is itself a no-op for an id it doesn't find), so it
    /// always saves and always returns success. #322 requires an
    /// unknown token to 404 with **no** audit row — which means detecting "not found" and returning
    /// *before* <see cref="IAdminTravelerRepository.SaveChangesAsync"/> is ever called, since
    /// <c>AdminAuditInterceptor</c> only ever writes a row for a save that actually happens.
    /// </summary>
    public sealed class DeleteDeviceTokenCommandHandler : IRequestHandler<DeleteDeviceTokenCommand>
    {
        private readonly IAdminTravelerRepository _travelers;

        public DeleteDeviceTokenCommandHandler(IAdminTravelerRepository travelers) => _travelers = travelers;

        public async Task Handle(DeleteDeviceTokenCommand cmd, CancellationToken ct)
        {
            // Tracked (not GetDetailAsync's AsNoTracking) — RemoveDeviceToken below must mutate a
            // change-tracked instance so AdminAuditInterceptor sees the resulting Deleted entry.
            var traveler = await _travelers.GetByIdAsync(cmd.TravelerId, ct)
                ?? throw new DomainException($"Traveler {cmd.TravelerId} not found.", 404);

            if (traveler.DeviceTokens.All(t => t.Id != cmd.DeviceTokenId))
                throw new DomainException($"Device token {cmd.DeviceTokenId} not found.", 404);

            // DeviceToken's FK to Traveler is configured OnDelete(DeleteBehavior.Cascade)
            // (DeviceTokenConfiguration), so removing it from this loaded, tracked collection marks
            // it Deleted in the change tracker — AdminAuditInterceptor picks that entry up and
            // writes the Delete audit row on its own; nothing here builds one explicitly.
            traveler.RemoveDeviceToken(cmd.DeviceTokenId);
            await _travelers.SaveChangesAsync(ct);
        }
    }

    public sealed class DeleteDeviceTokenCommandValidator : AbstractValidator<DeleteDeviceTokenCommand>
    {
        public DeleteDeviceTokenCommandValidator()
        {
            RuleFor(x => x.TravelerId).NotEqual(Guid.Empty);
            RuleFor(x => x.DeviceTokenId).NotEqual(Guid.Empty);
        }
    }
}
