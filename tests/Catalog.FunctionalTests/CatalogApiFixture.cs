using System.Reflection;
using System.Threading;

using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;

namespace eShop.Catalog.FunctionalTests;

public sealed class CatalogApiFixture : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly IHost _app;

    public IResourceBuilder<SqlServerServerResource> SqlServer { get; private set; }
    public IResourceBuilder<SqlServerDatabaseResource> Sql { get; private set; }
    public IResourceBuilder<AzureServiceBusResource> ServiceBus { get; private set; }
    private string _sqlConnectionString;
    private string _serviceBusConnectionString;

    public CatalogApiFixture()
    {
        var options = new DistributedApplicationOptions { AssemblyName = typeof(CatalogApiFixture).Assembly.FullName, DisableDashboard = true };
        var appBuilder = DistributedApplication.CreateBuilder(options);

        SqlServer = appBuilder.AddSqlServer("sql");
        Sql = SqlServer.AddDatabase("CatalogDB");

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

        var notificationService = _app.Services.GetRequiredService<ResourceNotificationService>();
        await notificationService.WaitForResourceHealthyAsync(SqlServer.Resource.Name, CancellationToken.None);
        await notificationService.WaitForResourceHealthyAsync(ServiceBus.Resource.Name, CancellationToken.None);

        _sqlConnectionString = await Sql.Resource.ConnectionStringExpression.GetValueAsync(CancellationToken.None);
        if (!_sqlConnectionString.Contains("TrustServerCertificate", StringComparison.OrdinalIgnoreCase))
        {
            _sqlConnectionString += ";TrustServerCertificate=true";
        }

        _serviceBusConnectionString = await ServiceBus.Resource.ConnectionStringExpression.GetValueAsync(CancellationToken.None);

        await WaitForSqlServerReadyAsync(_sqlConnectionString);
    }

    private static async Task WaitForSqlServerReadyAsync(string connectionString)
    {
        const int maxAttempts = 30;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                await using var connection = new SqlConnection(connectionString);
                await connection.OpenAsync(CancellationToken.None);
                return;
            }
            catch (SqlException) when (attempt < maxAttempts)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), CancellationToken.None);
            }
        }
    }
}
