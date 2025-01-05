using IBM.WMQ;
using System;
using System.Collections.Generic;
using Datadog.Trace;
using System.Collections;

class Program
{
    static void Main()
    {
        string queueManager = "QM1";
        string queueName = "DEV.QUEUE.1";
        string host = (Environment.GetEnvironmentVariable("MQ_HOST") ?? "localhost")!;
        int port = 1414;
        string channel = "DEV.APP.SVRCONN";
        string user = "app";
        string password = "passw0rd";

        MQQueueManager queueManagerObject = null;
        MQQueue queue = null;

        try
        {
            // Define connection properties
            var connectionProperties = new Hashtable
            {
                { MQC.HOST_NAME_PROPERTY, host },
                { MQC.PORT_PROPERTY, port },
                { MQC.CHANNEL_PROPERTY, channel },
                { MQC.USER_ID_PROPERTY, user },
                { MQC.PASSWORD_PROPERTY, password }
            };

            // Connect to the queue manager
            queueManagerObject = new MQQueueManager(queueManager, connectionProperties);

            // Access the queue for input
            queue = queueManagerObject.AccessQueue(queueName, MQC.MQOO_INPUT_AS_Q_DEF);

            // Retrieve the message
            MQMessage message = new MQMessage();
            MQGetMessageOptions gmo = new MQGetMessageOptions();
            queue.Get(message, gmo);

            // Reverse transform the headers
            var restoredHeaders = ReverseTransformHeaders(message);

            // Use Datadog's SpanContextExtractor to extract the span context
            var spanContextExtractor = new SpanContextExtractor();
            var parentContext = spanContextExtractor.Extract(restoredHeaders, (headers, name) =>
            {
                if (headers.TryGetValue(name, out var value))
                {
                    return new[] { value }; // Wrap value in an IEnumerable<string?>
                }
                return Array.Empty<string?>(); // Return empty if no value exists
            });

            // Start a new span with the extracted context as the parent
            using (var scope = Tracer.Instance.StartActive("consumer.process_message", new SpanCreationSettings { Parent = parentContext }))
            {
                var span = scope.Span;
                span.ResourceName = "ProcessMessage";
                span.SetTag("mq.queue", queueName);

                // Read message content
                string content = message.ReadString(message.MessageLength);
                Console.WriteLine($"Received message: {content}");
            }
        }
        catch (MQException ex)
        {
            Console.WriteLine($"MQ Exception: {ex.Message}");
        }
        finally
        {
            queue?.Close();
            queueManagerObject?.Disconnect();
        }
    }

    private static Dictionary<string, string> ReverseTransformHeaders(MQMessage message)
    {
        // Map transformed names back to original names
        var headerMappings = new Dictionary<string, string>
        {
            { "x_datadog_trace_id", "x-datadog-trace-id" },
            { "x_datadog_parent_id", "x-datadog-parent-id" },
            { "x_datadog_sampling_priority", "x-datadog-sampling-priority" },
            { "traceparent", "traceparent" },
            { "tracestate", "tracestate" }
        };

        var restoredHeaders = new Dictionary<string, string>();

        foreach (var mapping in headerMappings)
        {
            try
            {
                // Get the transformed property value
                string value = message.GetStringProperty(mapping.Key);
                restoredHeaders[mapping.Value] = value; // Use the original header name as the key
            }
            catch (MQException ex) when (ex.ReasonCode == MQC.MQRC_PROPERTY_NOT_AVAILABLE)
            {
                Console.WriteLine($"Property {mapping.Key} not available.");
            }
        }

        return restoredHeaders;
    }
}