using FlightAssistant.Infrastructure.Persistance;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace FlightAssistant.API.Tests.Integration;

/// <summary>
/// <see cref="FlightAssistantWebApplicationFactory"/> strips every EF Core registration and
/// re-adds <c>AddDbContext</c> with the InMemory provider and no interceptors — deliberately, to
/// give the rest of this suite a fast, isolated database. The consequence is that no other test in
/// this project ever exercises the real <c>Program.cs</c> line that attaches
/// <see cref="AdminAuditInterceptor"/> to the <c>DbContext</c> options (see <c>Program.cs</c>,
/// the "Database" section, <c>opt.AddInterceptors(sp.GetRequiredService&lt;AdminAuditInterceptor&gt;())</c>).
///
/// <para>
/// This test builds a second, standalone <see cref="WebApplicationFactory{Program}"/> that leaves
/// that registration untouched instead of layering onto (or restructuring)
/// <see cref="FlightAssistantWebApplicationFactory"/> — the cheapest way to prove the actual
/// composition wires the interceptor in, not a copy of it (#313 Task 2 review, F6/M6). It inspects
/// the resolved <c>DbContextOptions</c> rather than performing a save, so it needs no database
/// connection at all.
/// </para>
///
/// <para>
/// Hosted services are the one thing stripped here too: starting the host for real starts
/// <c>FlightStatusSyncBackgroundService</c>, which would otherwise immediately attempt a
/// connection using <c>appsettings.Testing.json</c>'s placeholder Postgres connection string. That
/// failure is caught and logged by the service itself, so it wouldn't fail this test — it's just
/// unrelated noise this test has no reason to invite.
/// </para>
/// </summary>
public class DbContextInterceptorWiringTests
{
    [Fact]
    public void RealComposition_AttachesAdminAuditInterceptor_ToTheDbContextOptions()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");

            builder.ConfigureServices(services =>
            {
                var hosted = services.Where(d => d.ServiceType == typeof(IHostedService)).ToList();
                foreach (var d in hosted) services.Remove(d);
            });
        });

        using var scope = factory.Services.CreateScope();
        var options = scope.ServiceProvider.GetRequiredService<DbContextOptions<FlightAssistantDbContext>>();

        var coreExtension = options.FindExtension<CoreOptionsExtension>();
        Assert.NotNull(coreExtension);
        Assert.NotNull(coreExtension.Interceptors);
        Assert.Contains(coreExtension.Interceptors!, i => i is AdminAuditInterceptor);
    }
}
