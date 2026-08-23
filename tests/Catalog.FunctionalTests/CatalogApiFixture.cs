using System.Reflection;
using System.Threading;

using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure;

using Microsoft.AspNetCore.Mvc.Testing;

namespace eShop.Catalog.FunctionalTests;

public sealed class CatalogApiFixture : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly IHost _app;

    public IResourceBuilder<SqlServerDatabaseResource> Sql { get; private set; }
    public IResourceBuilder<AzureServiceBusResource> ServiceBus { get; private set; }
    private string _sqlConnectionString;
    private string _serviceBusConnectionString;

    public CatalogApiFixture()
    {
        var options = new DistributedApplicationOptions { AssemblyName = typeof(CatalogApiFixture).Assembly.FullName, DisableDashboard = true };
        var appBuilder = DistributedApplication.CreateBuilder(options);

        Sql = appBuilder.AddSqlServer("sql")
            .AddDatabase("CatalogDB");

        var serviceBus = appBuilder.AddAzureServiceBus("eventbus")
            .RunAsEmulator();
        var eventBusTopic = serviceBus.AddServiceBusTopic("eshop-event-bus");
        eventBusTopic.AddServiceBusSubscription("Catalog");
        ServiceBus = serviceBus;

        _app = appBuilder.Build();
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        builder.ConfigureHostConfiguration(config =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string>
            {
                { "ConnectionStrings:CatalogDB", _sqlConnectionString },
                { "ConnectionStrings:EventBus", _serviceBusConnectionString },
                });
        });
        return base.CreateHost(builder);
    }

    public new async Task DisposeAsync()
    {
        await base.DisposeAsync();
        await _app.StopAsync();
        if (_app is IAsyncDisposable asyncDisposable)
        {
            await asyncDisposable.DisposeAsync().ConfigureAwait(false);
        }
        else
        {
            _app.Dispose();
        }
    }

    public async ValueTask InitializeAsync()
    {
        await _app.StartAsync();
        _sqlConnectionString = await Sql.Resource.ConnectionStringExpression.GetValueAsync(CancellationToken.None);
        _serviceBusConnectionString = await ServiceBus.Resource.ConnectionStringExpression.GetValueAsync(CancellationToken.None);
    }
}
