using System.ComponentModel.DataAnnotations;

namespace FlightAssistant.Infrastructure.ExternalServices.GitHub
{
    /// <summary>
    /// Where the admin CI endpoints read runs and test reports from, bound from the <c>GitHub</c>
    /// section. <see cref="Token"/> is optional so an environment without one still starts: the CI
    /// endpoints answer 503 instead.
    /// </summary>
    public sealed class GitHubOptions
    {
        /// <summary>Fixed rather than configurable: the token is only ever sent to this host.</summary>
        public const string ApiBaseUrl = "https://api.github.com/";

        /// <summary>
        /// A fine-grained, read-only token with Actions: read on <see cref="Repo"/>, from Key Vault as
        /// <c>GitHub--Token</c>. Sent as a bearer token to <see cref="ApiBaseUrl"/> only. Never logged.
        /// </summary>
        public string? Token { get; set; }

        [Required]
        [MinLength(1)]
        public string Owner { get; set; } = "salih1f1";

        [Required]
        [MinLength(1)]
        public string Repo { get; set; } = "flight-assistant-backend";

        /// <summary>The workflow file name (or numeric id) whose runs are listed.</summary>
        [Required]
        [MinLength(1)]
        public string Workflow { get; set; } = "ci.yml";

        public bool IsConfigured => !string.IsNullOrWhiteSpace(Token);
    }
}
