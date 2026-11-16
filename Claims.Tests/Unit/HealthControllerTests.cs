using System.Net;
using Claims.Controllers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Claims.Tests.Unit;

public class HealthControllerTests
{
    [Fact]
    public async Task Get_health_returns_200_anonymously_even_when_fallback_policy_requires_authentication()
    {
        using var host = new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureServices(services =>
                {
                    services.AddControllers().AddApplicationPart(typeof(HealthController).Assembly);
                    services.AddAuthentication();
                    services.AddAuthorization(options => options.FallbackPolicy = new AuthorizationPolicyBuilder()
                        .RequireAuthenticatedUser().Build());
                })
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseAuthentication();
                    app.UseAuthorization();
                    app.UseEndpoints(endpoints => endpoints.MapControllers());
                }))
            .Start();
        using var client = host.GetTestClient();

        using var response = await client.GetAsync("/health", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
