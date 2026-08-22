namespace eShop.EventBusServiceBus;

public class EventBusOptions
{
    /// <summary>
    /// Name of the Service Bus subscription this application consumes from. Under the covers this is the
    /// per-service subscription created on <see cref="TopicName"/>, and it plays the same role the RabbitMQ
    /// queue name used to play.
    /// </summary>
    public string SubscriptionClientName { get; set; }

    /// <summary>
    /// Name of the Service Bus topic all integration events are published to.
    /// </summary>
    public string TopicName { get; set; } = "eshop-event-bus";

    public int RetryCount { get; set; } = 10;

    /// <summary>
    /// Maximum number of messages the processor handles concurrently. RabbitMQ consumed serially, so the
    /// default keeps that behaviour; raise it once handlers are known to be safe to run in parallel.
    /// </summary>
    public int MaxConcurrentCalls { get; set; } = 1;
}
