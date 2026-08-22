using System.Diagnostics;
using OpenTelemetry.Context.Propagation;

namespace eShop.EventBusServiceBus;

public class ServiceBusTelemetry
{
    public static string ActivitySourceName = "EventBusServiceBus";

    public ActivitySource ActivitySource { get; } = new(ActivitySourceName);
    public TextMapPropagator Propagator { get; } = Propagators.DefaultTextMapPropagator;
}
