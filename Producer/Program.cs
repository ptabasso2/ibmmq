using IBM.WMQ;
using System;
using System.Collections;
using System.Collections.Generic;
using Datadog.Trace;

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

            // Access the queue for output
            queue = queueManagerObject.AccessQueue(queueName, MQC.MQOO_OUTPUT);

            // Start a new Datadog span
            using (var scope = Tracer.Instance.StartActive("producer.send_message"))
            {
                var span = scope.Span;
                span.ResourceName = "SendMessage";
                span.SetTag("mq.queue", queueName);
                
                var traceId = span.Context.TraceId.ToString();
                var spanId = span.Context.SpanId.ToString();

                Console.WriteLine($"Trace ID: {traceId}");
                Console.WriteLine($"Span ID: {spanId}");
                // Create and send a message
                MQMessage message = new MQMessage
                {
                    Format = MQC.MQFMT_STRING
                };
                message.WriteString("Hello from Producer!");

                // Debugging: Create a dictionary to capture headers for logging
                var debugHeaders = new Dictionary<string, string>(); 
                
                // Inject trace context into message properties
                var spanContextInjector = new SpanContextInjector();
                //spanContextInjector.Inject(new Dictionary<string, string>(), (headers, name, value) => message.SetStringProperty(name, value), span.Context);
                spanContextInjector.Inject(debugHeaders, (headers, name, value) =>
                {
                
                var validName = TransformToValidPropertyName(name);

                // Log the transformation for debugging
                Console.WriteLine($"Injecting header: Original Name = {name}, Transformed Name = {validName}, Value = {value}");
    
                // Set the property on the MQMessage
                message.SetStringProperty(validName, value);

                // Add to debug dictionary for further inspection
                headers[name] = value;
                }, scope.Span.Context);


                // Debugging: Display all headers after injection
                Console.WriteLine("Headers injected into the message:");
                foreach (var kvp in debugHeaders)
                {
                  Console.WriteLine($"{kvp.Key}: {kvp.Value}");
                }

                MQPutMessageOptions pmo = new MQPutMessageOptions();
                queue.Put(message, pmo);

                Console.WriteLine("Message sent successfully with trace context!");
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

    private static string TransformToValidPropertyName(string name)
    {
        if (string.IsNullOrEmpty(name))
            throw new ArgumentException("Property name cannot be null or empty.", nameof(name));

        // Replace invalid characters with underscores
        var validName = System.Text.RegularExpressions.Regex.Replace(name, "[^a-zA-Z0-9_]", "_");

        // Ensure the name starts with a letter
        if (!char.IsLetter(validName[0]))
        {
            validName = "P_" + validName; // Prefix with 'P_' if it doesn't start with a letter
        }

        return validName;
    }

}