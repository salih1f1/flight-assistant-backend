namespace FlightAssistant.Domain.Administration.Enums
{
    /// <summary>
    /// The kind of write an <see cref="Entities.AdminAuditLog"/> row records.
    ///
    /// <see cref="Reveal"/> is included now, before anything writes it, even though it is not
    /// consumed until A5 (the audited reveal of a masked sensitive value) — adding an enum
    /// member later means another migration for a value the table shape already had room for.
    /// </summary>
    public enum AdminAuditAction
    {
        Create,
        Update,
        Delete,
        Verify,
        Import,
        Reveal,

        /// <summary>
        /// An operator pressing "Check now" on a data source (#460). The automatic observations
        /// behind that page are not audited — one row per outbound call would drown the log — so
        /// this records the deliberate act only.
        /// </summary>
        Probe
    }
}
