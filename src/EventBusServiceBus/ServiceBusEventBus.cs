namespace eShop.EventBusServiceBus;

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using OpenTelemetry;
using OpenTelemetry.Context.Propagation;
using Polly.Retry;

public sealed class ServiceBusEventBus(
    ILogger<ServiceBusEventBus> logger,
    IServiceProvider serviceProvider,
    ServiceBusClient serviceBusClient,
    IOptions<EventBusOptions> options,
    IOptions<EventBusSubscriptionInfo> subscriptionOptions,
    ServiceBusTelemetry serviceBusTelemetry) : IEventBus, IAsyncDisposable, IHostedService
{
    private readonly ResiliencePipeline _pipeline = CreateResiliencePipeline(options.Value.RetryCount);
    private readonly TextMapPropagator _propagator = serviceBusTelemetry.Propagator;
    private readonly ActivitySource _activitySource = serviceBusTelemetry.ActivitySource;
    private readonly string _topicName = options.Value.TopicName;
    private readonly string _subscriptionName = options.Value.SubscriptionClientName;
    private readonly EventBusSubscriptionInfo _subscriptionInfo = subscriptionOptions.Value;
    private ServiceBusSender _sender;
    private ServiceBusProcessor _processor;

    public async Task PublishAsync(IntegrationEvent @event)
    {
        var eventName = @event.GetType().Name;

        if (logger.IsEnabled(LogLevel.Trace))
        {
            logger.LogTrace("Creating Service Bus message to publish event: {EventId} ({EventName})", @event.Id, eventName);
        }

        var body = SerializeMessage(@event);

        // Start an activity with a name following the semantic convention of the OpenTelemetry messaging specification.
        // https://github.com/open-telemetry/semantic-conventions/blob/main/docs/messaging/messaging-spans.md
        var activityName = $"{eventName} publish";

        await _pipeline.Execute(async () =>
        {
            using var activity = _activitySource.StartActivity(activityName, ActivityKind.Client);

            // Depending on Sampling (and whether a listener is registered or not), the activity above may not be created.
            // If it is created, then propagate its context. If it is not created, the propagate the Current context, if any.

            ActivityContext contextToInject = default;

            if (activity != null)
            {
                contextToInject = activity.Context;
            }
            else if (Activity.Current != null)
            {
                contextToInject = Activity.Current.Context;
            }

            var message = new ServiceBusMessage(body)
            {
                Subject = eventName,
                ContentType = "application/json",
                MessageId = @event.Id.ToString(),
            };

            static void InjectTraceContextIntoMessage(ServiceBusMessage msg, string key, string value)
            {
                msg.ApplicationProperties[key] = value;
            }

            _propagator.Inject(new PropagationContext(contextToInject, Baggage.Current), message, InjectTraceContextIntoMessage);

            SetActivityContext(activity, eventName, "publish");

            if (logger.IsEnabled(LogLevel.Trace))
            {
                logger.LogTrace("Publishing event to Service Bus: {EventId}", @event.Id);
            }

            try
            {
                await _sender.SendMessageAsync(message);
            }
            catch (Exception ex)
            {
                activity.SetExceptionTags(ex);

                throw;
            }
        });
    }

    private static void SetActivityContext(Activity activity, string eventName, string operation)
    {
        if (activity is not null)
        {
            // These tags are added demonstrating the semantic conventions of the OpenTelemetry messaging specification
            // https://github.com/open-telemetry/semantic-conventions/blob/main/docs/messaging/messaging-spans.md
            activity.SetTag("messaging.system", "servicebus");
            activity.SetTag("messaging.destination_kind", "topic");
            activity.SetTag("messaging.operation", operation);
            activity.SetTag("messaging.destination.name", eventName);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_processor is not null)
        {
            await _processor.DisposeAsync();
        }

        if (_sender is not null)
        {
            await _sender.DisposeAsync();
        }
    }

    private async Task OnMessageReceived(ProcessMessageEventArgs args)
    {
        static IEnumerable<string> ExtractTraceContextFromMessage(ServiceBusReceivedMessage msg, string key)
        {
            if (msg.ApplicationProperties.TryGetValue(key, out var value) && value is string str)
            {
                return [str];
            }
            return [];
        }

        // Extract the PropagationContext of the upstream parent from the message properties.
        var parentContext = _propagator.Extract(default, args.Message, ExtractTraceContextFromMessage);
        Baggage.Current = parentContext.Baggage;

        var eventName = args.Message.Subject;

        // Start an activity with a name following the semantic convention of the OpenTelemetry messaging specification.
        // https://github.com/open-telemetry/semantic-conventions/blob/main/docs/messaging/messaging-spans.md
        var activityName = $"{eventName} receive";

        using var activity = _activitySource.StartActivity(activityName, ActivityKind.Client, parentContext.ActivityContext);

        SetActivityContext(activity, eventName, "receive");

        var message = Encoding.UTF8.GetString(args.Message.Body);

        try
        {
            activity?.SetTag("message", message);

            if (message.Contains("throw-fake-exception", StringComparison.InvariantCultureIgnoreCase))
            {
                throw new InvalidOperationException($"Fake exception requested: \"{message}\"");
            }

            await ProcessEvent(eventName, message);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Error Processing message \"{Message}\"", message);

            activity.SetExceptionTags(ex);
        }

        // Even on exception we take the message off the queue.
        // in a REAL WORLD app this should be handled with a dead-letter queue (Service Bus has one built in).
        // For more information see: https://learn.microsoft.com/azure/service-bus-messaging/service-bus-dead-letter-queues
        await args.CompleteMessageAsync(args.Message);
    }

    private Task OnProcessError(ProcessErrorEventArgs args)
    {
        logger.LogWarning(args.Exception, "Error with Service Bus processor: {ErrorSource}", args.ErrorSource);
        return Task.CompletedTask;
    }

    private async Task ProcessEvent(string eventName, string message)
    {
        if (logger.IsEnabled(LogLevel.Trace))
        {
            logger.LogTrace("Processing Service Bus event: {EventName}", eventName);
        }

        await using var scope = serviceProvider.CreateAsyncScope();

        if (!_subscriptionInfo.EventTypes.TryGetValue(eventName, out var eventType))
        {
            logger.LogWarning("Unable to resolve event type for event name {EventName}", eventName);
            return;
        }

        // Deserialize the event
        var integrationEvent = DeserializeMessage(message, eventType);

        // REVIEW: This could be done in parallel

        // Get all the handlers using the event type as the key
        foreach (var handler in scope.ServiceProvider.GetKeyedServices<IIntegrationEventHandler>(eventType))
        {
            await handler.Handle(integrationEvent);
        }
    }

    [UnconditionalSuppressMessage("Trimming", "IL2026:RequiresUnreferencedCode",
        Justification = "The 'JsonSerializer.IsReflectionEnabledByDefault' feature switch, which is set to false by default for trimmed .NET apps, ensures the JsonSerializer doesn't use Reflection.")]
    [UnconditionalSuppressMessage("AOT", "IL3050:RequiresDynamicCode", Justification = "See above.")]
    private IntegrationEvent DeserializeMessage(string message, Type eventType)
    {
        return JsonSerializer.Deserialize(message, eventType, _subscriptionInfo.JsonSerializerOptions) as IntegrationEvent;
    }

    [UnconditionalSuppressMessage("Trimming", "IL2026:RequiresUnreferencedCode",
        Justification = "The 'JsonSerializer.IsReflectionEnabledByDefault' feature switch, which is set to false by default for trimmed .NET apps, ensures the JsonSerializer doesn't use Reflection.")]
    [UnconditionalSuppressMessage("AOT", "IL3050:RequiresDynamicCode", Justification = "See above.")]
    private byte[] SerializeMessage(IntegrationEvent @event)
    {
        return JsonSerializer.SerializeToUtf8Bytes(@event, @event.GetType(), _subscriptionInfo.JsonSerializerOptions);
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            logger.LogInformation("Starting Service Bus connection");

            _sender = serviceBusClient.CreateSender(_topicName);

            _processor = serviceBusClient.CreateProcessor(_topicName, _subscriptionName, new ServiceBusProcessorOptions
            {
                MaxConcurrentCalls = options.Value.MaxConcurrentCalls,
                AutoCompleteMessages = false,
            });

            _processor.ProcessMessageAsync += OnMessageReceived;
            _processor.ProcessErrorAsync += OnProcessError;

            await _processor.StartProcessingAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error starting Service Bus connection");
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_processor is not null)
        {
            await _processor.StopProcessingAsync(cancellationToken);
        }
    }

    private static ResiliencePipeline CreateResiliencePipeline(int retryCount)
    {
        // See https://www.pollydocs.org/strategies/retry.html
        var retryOptions = new RetryStrategyOptions
        {
            ShouldHandle = new PredicateBuilder().Handle<ServiceBusException>(ex => ex.IsTransient),
            MaxRetryAttempts = retryCount,
            DelayGenerator = (context) => ValueTask.FromResult(GenerateDelay(context.AttemptNumber))
        };

        return new ResiliencePipelineBuilder()
            .AddRetry(retryOptions)
            .Build();

        static TimeSpan? GenerateDelay(int attempt)
        {
            return TimeSpan.FromSeconds(Math.Pow(2, attempt));
        }
    }
}
