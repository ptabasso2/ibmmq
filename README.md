
# IBM MQ Producer-Consumer with Datadog Tracing

## 1. Prerequisites

Ensure the following prerequisites are met:

- **.NET SDK**: Install the .NET SDK (version 6.0 or later). [Download here](https://dotnet.microsoft.com/download).
- **Docker**: Install Docker to run the IBM MQ broker and Datadog Agent. [Download Docker](https://www.docker.com/products/docker-desktop/).
- **IBM MQ Docker Image**: Ensure access to the `icr.io/ibm-messaging/mq:latest` image.
- **Datadog API Key**: Obtain your Datadog API Key from the [Datadog dashboard](https://app.datadoghq.com/).

## 2. Directory Structure

Here is the directory structure for the project:

```plaintext
ibmmq/
├── Consumer
│   ├── Consumer.csproj
│   ├── Program.cs
│   ├── bin/
│   ├── instrument.env
│   └── obj/
├── MQExample.sln
└── Producer
    ├── Producer.csproj
    ├── Program.cs
    ├── bin/
    ├── instrument.env
    └── obj/
```

- **`MQExample.sln`**: Solution file to manage both projects.
- **`Producer`**: Producer project that sends messages to IBM MQ.
- **`Consumer`**: Consumer project that receives messages from IBM MQ.
- **`instrument.env`**: File containing environment variables for Datadog instrumentation.

## 3. Adding Dependencies

Add the necessary NuGet packages to both projects.

### Add `Datadog.Trace` and `IBMMQDotnetClient` Dependencies

Run these commands in the terminal from the respective project directories (`Producer` and `Consumer`):

```bash
dotnet add package Datadog.Trace
dotnet add package IBMMQDotnetClient
```

Verify that the `Datadog.Trace` and `IBMMQDotnetClient` packages are listed in each project’s `csproj` file.

## 4. Final Code

### Producer Code

The producer sends a message to the IBM MQ queue and injects trace context for Datadog.

```csharp
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
```

### Consumer Code

The consumer retrieves a message from the IBM MQ queue, extracts trace context, and links the trace to Datadog.

```csharp
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
```


## 5. Start the Datadog Agent

Run the following command to start the Datadog Agent with the necessary configurations for tracing:

```bash
docker run -d --name dd-agent-dogfood-jmx \
  -v /var/run/docker.sock:/var/run/docker.sock:ro \
  -v /proc/:/host/proc/:ro \
  -v /sys/fs/cgroup/:/host/sys/fs/cgroup:ro \
  -p 8126:8126 \
  -p 8125:8125/udp \
  -e DD_API_KEY=<YOUR_DATADOG_API_KEY> \
  -e DD_APM_ENABLED=true \
  -e DD_APM_NON_LOCAL_TRAFFIC=true \
  -e DD_PROCESS_AGENT_ENABLED=true \
  -e DD_DOGSTATSD_NON_LOCAL_TRAFFIC="true" \
  -e DD_LOG_LEVEL=debug \
  -e DD_LOGS_ENABLED=true \
  -e DD_LOGS_CONFIG_CONTAINER_COLLECT_ALL=true \
  -e DD_CONTAINER_EXCLUDE_LOGS="name:datadog-agent" \
  gcr.io/datadoghq/agent:latest-jmx
```

Replace `<YOUR_DATADOG_API_KEY>` with your actual Datadog API Key.

## 6. Start the IBM MQ Broker

Run the following command to start the IBM MQ broker container:

```bash
docker run --rm -d --name ibmmq \
  --env LICENSE=accept \
  --env MQ_QMGR_NAME=QM1 \
  -e MQ_APP_PASSWORD=passw0rd \
  -p 1414:1414 -p 9443:9443 \
  icr.io/ibm-messaging/mq:latest
```

## 7. Build Instructions

To build the projects and prepare them for execution:

1. **Build the Solution**:
   From the root directory (`ibmmq`), run:
   ```bash
   dotnet build MQExample.sln
   ```

2. **Instrument the Producer**:
   In a first terminal, from the root directory run the following command:
   ```bash
   for i in $(cat Producer/instrument.env) 
   do 
    export $i 
   done
   ```

3. **Instrument the Consumer**:
   In another terminal, from the root directory run the following command:
   ```bash
   for i in $(cat Consumer/instrument.env)
   do 
     export $i
   done
   ```
   

4. **Run the Producer**:
   In the first terminal, navigate to the `Producer` directory and run:
   ```bash
   dotnet run
   ```

5. **Run the Consumer**:
   In the second terminal, navigate to the `Consumer` directory and run:
   ```bash
   dotnet run
   ```


## 8. Verify Traces in Datadog

1. Log in to the [Datadog APM dashboard](https://app.datadoghq.com/apm).
2. Look for the `producer.send_message` and `consumer.process_message` spans.
3. Confirm the parent-child relationship between the spans and verify trace propagation.
