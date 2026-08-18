// The manual gather-and-retry loop from Chapter 6, Section 6.2.5, plus the
// production bounds the chapter calls for (retry budget, timeout, cancel) —
// expressed at the WIRE level, over a raw ITransport.
//
// Why not over McpClient? In preview.1 the MRTR loop is welded into
// McpClientImpl.SendRequestAsync: every request auto-fulfils input_required
// results through the registered handlers, capped at a hard-coded 10 rounds.
// There is no per-call opt-out (nothing like the TS `allowInputRequired`),
// no way to receive the interim InputRequiredResult, and no way to configure
// the budget. So the manual loop — the book's teaching path — has to sit
// directly on IClientTransport/ITransport, which is also an honest picture
// of exactly what the SDK does on your behalf.
//
// The requests are handshake-less 2026-07-28 requests carrying the SEP-2575
// `_meta` envelope (protocol version, client info, client capabilities —
// including `elicitation`, without which a server must not embed elicit
// requests).

using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using System.Text.Json;
using System.Text.Json.Nodes;

public static class ManualLoop
{
    private static long _nextId;

    public static async Task<CallToolResult> CallToolGatheringInputAsync(
        ITransport session,
        string toolName,
        JsonObject arguments,
        int maxRounds,           // retry budget
        TimeSpan timeout,        // deadline for the whole flow, all rounds included
        CancellationToken cancel) // user-facing cancel (Ctrl+C)
    {
        var deadline = DateTime.UtcNow + timeout;
        JsonObject mrtr = [];
        for (var round = 1; ; round++)
        {
            if (round > maxRounds)
            {
                throw new McpException($"input_required retry budget exhausted after {maxRounds} rounds");
            }
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException($"tool call still gathering input after {timeout.TotalMilliseconds} ms");
            }

            // Each iteration is a brand-new request with a new JSON-RPC id,
            // carrying the original params plus the gathered responses.
            var params_ = new JsonObject
            {
                ["name"] = toolName,
                ["arguments"] = arguments.DeepClone(),
                ["_meta"] = Envelope(),
            };
            foreach (var (key, value) in mrtr)
            {
                params_[key] = value?.DeepClone();
            }
            var response = await SendAsync(session, "tools/call", params_, deadline, cancel);

            // On the wire the discriminator is `resultType: "input_required"`.
            if (response is not JsonObject obj
                || obj["resultType"]?.GetValue<string>() != "input_required")
            {
                return JsonSerializer.Deserialize<CallToolResult>(response, McpJsonUtilities.DefaultOptions)
                    ?? throw new McpException("tools/call returned an unparsable result");
            }
            var interim = JsonSerializer.Deserialize<InputRequiredResult>(obj, McpJsonUtilities.DefaultOptions)!;
            Console.WriteLine(
                $"<- input_required (round {round}): keys [{string.Join(", ", interim.InputRequests?.Keys ?? [])}]"
                + (interim.RequestState is not null ? ", requestState present" : ""));

            // Construct every requested input before retrying.
            var inputResponses = new Dictionary<string, InputResponse>();
            foreach (var (key, request) in interim.InputRequests ?? new Dictionary<string, InputRequest>())
            {
                if (request.Method != RequestMethods.ElicitationCreate || request.ElicitationParams is not { } elicit)
                {
                    // This host supports elicitation only (sampling is deprecated; roots n/a).
                    throw new McpException($"Unsupported input request: {request.Method}");
                }
                // Render a form, open a URL, or apply policy — see InputHandler.cs.
                inputResponses[key] = InputResponse.FromElicitResult(
                    await InputHandler.HandleElicitationAsync(elicit));
            }
            mrtr = new JsonObject
            {
                ["inputResponses"] = JsonSerializer.SerializeToNode(inputResponses, McpJsonUtilities.DefaultOptions),
            };
            if (interim.RequestState is { } state)
            {
                // Echo verbatim; never inspect, parse, or modify.
                mrtr["requestState"] = state;
            }
        }
    }

    /// <summary>One JSON-RPC request/response exchange over the raw transport.</summary>
    private static async Task<JsonNode?> SendAsync(
        ITransport session, string method, JsonObject params_, DateTime deadline, CancellationToken cancel)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        cts.CancelAfter(deadline - DateTime.UtcNow);
        var id = new RequestId(Interlocked.Increment(ref _nextId));
        await session.SendMessageAsync(
            new JsonRpcRequest { Id = id, Method = method, Params = params_ }, cts.Token);
        while (true)
        {
            var message = await session.MessageReader.ReadAsync(cts.Token);
            switch (message)
            {
                case JsonRpcResponse response when response.Id == id:
                    return response.Result;
                case JsonRpcError error when error.Id == id:
                    throw new McpException($"{method} failed: {error.Error.Code} {error.Error.Message}");
                default:
                    continue; // unrelated notification/response
            }
        }
    }

    /// <summary>The 2026-07-28 per-request `_meta` envelope (SEP-2575).</summary>
    private static JsonObject Envelope() => new()
    {
        ["io.modelcontextprotocol/protocolVersion"] = "2026-07-28",
        ["io.modelcontextprotocol/clientInfo"] = new JsonObject
        {
            ["name"] = "mrtr-manual-client",
            ["version"] = "0.1.0",
        },
        ["io.modelcontextprotocol/clientCapabilities"] = new JsonObject
        {
            ["elicitation"] = new JsonObject(),
        },
    };
}
