using System.Reflection;
using System.Xml.Linq;
using Claims.Auditing;
using Claims.Controllers;
using Claims.Data;
using Claims.Infrastructure;
using Claims.Repositories;
using Claims.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Claims.Tests.Unit;

public class ArchitectureTests
{
    [Theory]
    [InlineData("Claims.Domain", new string[0])]
    [InlineData("Claims.Application", new[] { "Claims.Domain" })]
    [InlineData("Claims.Infrastructure", new[] { "Claims.Application", "Claims.Domain" })]
    [InlineData("Claims.Api", new[] { "Claims.Application", "Claims.Domain", "Claims.Infrastructure" })]
    public void Projects_have_only_the_allowed_dependencies(string project, string[] expected)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Claims.sln")))
            root = root.Parent;
        Assert.NotNull(root);
        var document = XDocument.Load(Path.Combine(root.FullName, project, project + ".csproj"));
        var references = document.Descendants("ProjectReference")
            .Select(x => Path.GetFileNameWithoutExtension(x.Attribute("Include")!.Value.Replace('\\', '/')))
            .Order().ToArray();
        Assert.Equal(expected.Order(), references);
        Assert.Equal("net10.0", Assert.Single(document.Descendants("TargetFramework")).Value);
        if (project is "Claims.Domain" or "Claims.Application")
        {
            Assert.Empty(document.Descendants("PackageReference"));
            Assert.Empty(document.Descendants("FrameworkReference"));
        }
    }

    [Fact]
    public void Domain_and_application_assemblies_have_no_outer_layer_or_framework_dependencies()
    {
        var domain = typeof(Claim).Assembly;
        var application = typeof(ClaimService).Assembly;
        Assert.Equal("Claims.Domain", domain.GetName().Name);
        Assert.Equal("Claims.Application", application.GetName().Name);
        Assert.All(domain.GetReferencedAssemblies(), x => Assert.True(IsSystem(x), x.FullName));
        Assert.All(application.GetReferencedAssemblies(), x =>
            Assert.True(IsSystem(x) || x.Name == "Claims.Domain", x.FullName));
        Assert.DoesNotContain(typeof(Claim).GetProperties().SelectMany(p => p.CustomAttributes),
            a => !IsSystem(a.AttributeType.Assembly.GetName()));
        Assert.DoesNotContain(typeof(Cover).GetProperties().SelectMany(p => p.CustomAttributes),
            a => !IsSystem(a.AttributeType.Assembly.GetName()));
    }

    [Fact]
    public void Controllers_depend_on_application_interfaces_and_bind_api_contracts()
    {
        Assert.Equal("Claims.Api", typeof(ClaimsController).Assembly.GetName().Name);
        foreach (var controller in new[] { typeof(ClaimsController), typeof(CoversController) })
        {
            var dependency = Assert.Single(Assert.Single(controller.GetConstructors()).GetParameters()).ParameterType;
            Assert.True(dependency.IsInterface);
            Assert.Equal(typeof(ClaimService).Assembly, dependency.Assembly);
            var requestType = Assert.Single(controller.GetMethod("CreateAsync")!.GetParameters()).ParameterType;
            Assert.Equal(controller.Assembly, requestType.Assembly);
            Assert.Equal("Claims.Api.Contracts", requestType.Namespace);
        }
        Assert.Equal(typeof(ClaimService).Assembly, typeof(IClaimRepository).Assembly);
        Assert.Equal(typeof(ClaimService).Assembly, typeof(ICoverRepository).Assembly);
        Assert.Equal(typeof(ClaimService).Assembly, typeof(IAuditer).Assembly);
        Assert.Equal("Claims.Infrastructure", typeof(ClaimRepository).Assembly.GetName().Name);
    }

    [Fact]
    public void Infrastructure_registration_preserves_scoped_resources_and_singleton_audit_queue()
    {
        var services = new ServiceCollection().AddClaimsInfrastructure(
            "Server=unused;Database=audit;Integrated Security=True", "mongodb://localhost:27017", "claims");
        foreach (var type in new[] { typeof(IClaimRepository), typeof(ICoverRepository),
                     typeof(ClaimsContext), typeof(AuditContext), typeof(IAuditWriter) })
            Assert.Equal(ServiceLifetime.Scoped, Assert.Single(services, x => x.ServiceType == type).Lifetime);
        foreach (var type in new[] { typeof(IAuditer), typeof(AuditQueue) })
            Assert.Equal(ServiceLifetime.Singleton, Assert.Single(services, x => x.ServiceType == type).Lifetime);
        var worker = Assert.Single(services, x => x.ServiceType == typeof(IHostedService));
        Assert.Equal(typeof(AuditWorker), worker.ImplementationType);
        Assert.Equal(ServiceLifetime.Singleton, worker.Lifetime);
    }

    private static bool IsSystem(AssemblyName name) => name.Name is "System" or "netstandard"
        || name.Name!.StartsWith("System.", StringComparison.Ordinal);
}
