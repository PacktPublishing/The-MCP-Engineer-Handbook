// Multi-provider schema mapping (§6.2.2) and output schema lifting (§6.2.3).
// Connects to the demo server over stdio, fetches its tools, and runs them
// through a registry of provider adapters — OpenAI Chat Completions, OpenAI
// Responses, Anthropic, Gemini, and Ollama-style — printing what each adapter
// kept, stripped, lifted, or refused. No model API keys involved: this is the
// mapping layer only. Finishes by reverse-mapping a simulated model tool call
// back to the server and executing it (§6.2.4).
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

// Compact, unescaped JSON — matching what a wire serializer produces.
var jsonOptions = new JsonSerializerOptions(McpJsonUtilities.DefaultOptions)
{
    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
};

// -----------------------------------------------------------------------------
// The minimal implementation printed in §6.2.2, targeting OpenAI's Responses
// API. Reproduced verbatim except the parameter type: the book prints
// `McpTool`, the C# SDK's type is `Tool` (with `InputSchema` as a JsonElement).
// -----------------------------------------------------------------------------
static JsonObject McpToolToOpenAI(Tool tool, string serverPrefix) => new()
{
    ["type"] = "function",
    ["name"] = $"{serverPrefix}__{tool.Name}",
    ["description"] = tool.Description ?? "",
    ["parameters"] = JsonObject.Create(tool.InputSchema),
};

// -----------------------------------------------------------------------------
// Everything below is what that minimal function grows into: a registry of
// provider adapters, each with a per-keyword strategy — strip, lift, or fail
// fast — for the JSON Schema keywords its API doesn't accept, plus a
// per-provider output-schema decision.
// -----------------------------------------------------------------------------

var adapters = new ProviderAdapter[]
{
    new(
        Provider: "openai-chat",
        // Chat Completions passes schemas through fairly permissively, but
        // documents `default` as unsupported.
        KeywordStrategies: new() { ["default"] = KeywordStrategy.Lift },
        InlineLocalRefs: false,
        OutputSchema: OutputSchemaMode.Lift,
        Wrap: (name, description, parameters, _) => new JsonObject
        {
            ["type"] = "function",
            ["function"] = new JsonObject
            {
                ["name"] = name,
                ["description"] = description,
                ["parameters"] = parameters,
            },
        }),
    new(
        Provider: "openai-responses",
        // Strict function calling accepts only a narrow schema subset: lift the
        // validation keywords, fail fast on compositions it can't represent.
        KeywordStrategies: new()
        {
            ["minimum"] = KeywordStrategy.Lift, ["maximum"] = KeywordStrategy.Lift,
            ["minLength"] = KeywordStrategy.Lift, ["maxLength"] = KeywordStrategy.Lift,
            ["pattern"] = KeywordStrategy.Lift, ["format"] = KeywordStrategy.Lift,
            ["default"] = KeywordStrategy.Lift,
            ["oneOf"] = KeywordStrategy.Fail,
        },
        InlineLocalRefs: false,
        OutputSchema: OutputSchemaMode.Native,
        // The flattened shape McpToolToOpenAI produces, grown up.
        Wrap: (name, description, parameters, outputSchema) =>
        {
            var wrapped = new JsonObject
            {
                ["type"] = "function",
                ["name"] = name,
                ["description"] = description,
                ["parameters"] = parameters,
            };
            if (outputSchema is not null)
                wrapped["output_schema"] = outputSchema;
            return wrapped;
        }),
    new(
        Provider: "anthropic",
        // Full JSON Schema 2020-12 support: nothing to strip or lift on input.
        KeywordStrategies: new(),
        InlineLocalRefs: false,
        OutputSchema: OutputSchemaMode.Lift,
        Wrap: (name, description, parameters, _) => new JsonObject
        {
            ["name"] = name,
            ["description"] = description,
            ["input_schema"] = parameters,
        }),
    new(
        Provider: "gemini",
        // Function declarations take an OpenAPI-flavored subset: no $ref, no
        // pattern. oneOf gets a crude lift — structural keywords are the hard
        // case §6.2.2 calls out; the alternatives survive only as prose.
        KeywordStrategies: new()
        {
            ["pattern"] = KeywordStrategy.Lift,
            ["default"] = KeywordStrategy.Lift,
            ["oneOf"] = KeywordStrategy.Lift,
        },
        InlineLocalRefs: true,
        OutputSchema: OutputSchemaMode.Lift,
        // One entry in tools[].functionDeclarations.
        Wrap: (name, description, parameters, _) => new JsonObject
        {
            ["name"] = name,
            ["description"] = description,
            ["parameters"] = parameters,
        }),
    new(
        Provider: "ollama",
        // OpenAI Chat Completions wire format, but small local models handle
        // constraints unevenly — this adapter strips them, trading lost
        // information for a schema the model won't trip over.
        KeywordStrategies: new()
        {
            ["pattern"] = KeywordStrategy.Strip, ["format"] = KeywordStrategy.Strip,
            ["default"] = KeywordStrategy.Strip, ["const"] = KeywordStrategy.Strip,
        },
        InlineLocalRefs: true,
        OutputSchema: OutputSchemaMode.Drop,
        Wrap: (name, description, parameters, _) => new JsonObject
        {
            ["type"] = "function",
            ["function"] = new JsonObject
            {
                ["name"] = name,
                ["description"] = description,
                ["parameters"] = parameters,
            },
        }),
};

// --- Demo --------------------------------------------------------------------

int ApproxTokens(string json) => (json.Length + 2) / 4;

// Connect to the demo server as a stdio child process. McpClient.CreateAsync
// performs the connection handshake and returns a ready client.
await using var client = await McpClient.CreateAsync(
    new StdioClientTransport(new StdioClientTransportOptions
    {
        Name = "demo-booking-server",
        Command = "dotnet",
        Arguments = [ResolveServerDll()],
    }));
Console.WriteLine($"Negotiated protocol version: {client.NegotiatedProtocolVersion}");

var serverPrefix = "demo"; // Short and meaningful: every token of it rides along on every tool name.
var tools = (await client.ListToolsAsync()).Select(t => t.ProtocolTool).ToList();
Console.WriteLine($"Fetched {tools.Count} tools: {string.Join(", ", tools.Select(t => t.Name))}\n");

Console.WriteLine("--- Minimal mapping (the §6.2.2 snippet, Responses API shape) ---");
Console.WriteLine(McpToolToOpenAI(tools[0], serverPrefix).ToJsonString(jsonOptions));

foreach (var adapter in adapters)
{
    Console.WriteLine($"\n=== {adapter.Provider} ===");
    var strategies = string.Join(", ", adapter.KeywordStrategies
        .Select(e => $"{e.Key}->{e.Value.ToString().ToLowerInvariant()}"));
    Console.WriteLine($"keyword strategies: {(strategies.Length > 0 ? strategies : "(none — full JSON Schema accepted)")}");
    Console.WriteLine($"local $refs: {(adapter.InlineLocalRefs ? "inlined" : "passed through")}; " +
                      $"output schema: {adapter.OutputSchema.ToString().ToLowerInvariant()}");

    foreach (var tool in tools)
    {
        Console.WriteLine($"\n  {tool.Name} (MCP definition ~{ApproxTokens(JsonSerializer.Serialize(tool, jsonOptions))} tokens)");
        try
        {
            var mapped = SchemaMapper.MapTool(adapter, tool, serverPrefix);
            foreach (var a in mapped.Actions)
                Console.WriteLine($"    {a.Action} {a.Keyword} at {a.Path}{(a.Detail is not null ? $" -> {a.Detail}" : "")}");
            if (tool.OutputSchema is not null)
                Console.WriteLine($"    output schema: {mapped.OutputSchemaDecision}");
            var json = mapped.Definition.ToJsonString(jsonOptions);
            Console.WriteLine($"    mapped (~{ApproxTokens(json)} tokens): {json}");
        }
        catch (MappingError error)
        {
            Console.WriteLine($"    FAILED FAST: {error.Message}");
        }
    }
}

Console.WriteLine("\n--- Reverse mapping: routing a model tool call (§6.2.4) ---");
var dispatch = new Dictionary<string, McpClient> { [serverPrefix] = client };
var modelCall = new ModelToolCall("demo__get_booking", """{"booking_ref":"BK-7Q2M4X"}""");
Console.WriteLine($"model emitted: {modelCall.Name}({modelCall.Arguments})");
var result = await SchemaMapper.RouteToolCallAsync(modelCall, dispatch);
Console.WriteLine($"routed to server \"{serverPrefix}\", tool \"get_booking\"");
Console.WriteLine($"result: {JsonSerializer.Serialize(result.StructuredContent, jsonOptions)}");
return;

// Locate the built server binary next to this project. Build csharp/DemoServer first.
static string ResolveServerDll()
{
    if (Environment.GetEnvironmentVariable("DEMO_SERVER_DLL") is { Length: > 0 } overridePath)
        return overridePath;

    var candidate = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..",
        "DemoServer", "bin", "Debug", "net10.0", "DemoServer.dll"));
    if (!File.Exists(candidate))
        throw new FileNotFoundException(
            $"DemoServer.dll not found at {candidate}. Run 'dotnet build' in csharp/DemoServer first, " +
            "or point DEMO_SERVER_DLL at the built server.");
    return candidate;
}

/// <summary>What to do with a JSON Schema keyword the provider's API doesn't accept.</summary>
enum KeywordStrategy { Strip, Lift, Fail }

/// <summary>
/// §6.2.3: pass the output schema through natively, lift it into the
/// description, or drop it (when the model doesn't need it, lifting only adds
/// token cost). As of this writing no major API takes the field on tool
/// definitions — Native here illustrates the pass-through path for when
/// support lands.
/// </summary>
enum OutputSchemaMode { Native, Lift, Drop }

record MappingAction(string Action, string Keyword, string Path, string? Detail = null);

class MappingError(string message) : Exception(message);

/// <param name="KeywordStrategies">
/// Per-keyword strategy for keywords this provider's API rejects or ignores.
/// Unlisted keywords pass through untouched. This table is data, not code,
/// because provider support matrices shift release to release — the
/// assignments above are illustrative; verify against current provider docs
/// before relying on them.
/// </param>
/// <param name="InlineLocalRefs">Inline local $refs for APIs that don't understand references.</param>
/// <param name="Wrap">The provider's wrapper format around name/description/schema.</param>
record ProviderAdapter(
    string Provider,
    OrderedDictionary<string, KeywordStrategy> KeywordStrategies,
    bool InlineLocalRefs,
    OutputSchemaMode OutputSchema,
    Func<string, string, JsonObject, JsonObject?, JsonObject> Wrap);

record MappedTool(JsonObject Definition, List<MappingAction> Actions, string OutputSchemaDecision);

/// <summary>The shape a model API hands back: prefixed name, arguments as JSON text.</summary>
record ModelToolCall(string Name, string Arguments);

static class SchemaMapper
{
    const int MaxDepth = 32; // Bound the walk: hostile schemas are a DoS vector.

    // Compact, unescaped JSON for printed details and lifted phrases.
    static readonly JsonSerializerOptions Compact = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    // minimum+maximum and minLength+maxLength lift as one sentence; when the
    // first of a pair is lifted, the second is silently removed with it.
    static readonly Dictionary<string, string> LiftPairs = new()
    {
        ["minimum"] = "maximum",
        ["minLength"] = "maxLength",
    };

    // --- Namespace prefixing (§6.2.2) ---------------------------------------
    // Server prefix + double underscore, so the separator can't be confused
    // with the word-divider underscores inside tool names.

    public static string NamespacedName(string serverPrefix, string toolName) =>
        $"{serverPrefix}__{toolName}";

    /// <summary>Reverse mapping: prefixed model tool call -> originating server + tool.</summary>
    public static (string ServerPrefix, string ToolName) SplitNamespacedName(string name)
    {
        var sep = name.IndexOf("__", StringComparison.Ordinal);
        if (sep < 1)
            throw new MappingError($"Tool name has no server prefix: {name}");
        return (name[..sep], name[(sep + 2)..]);
    }

    // --- Schema transformation -----------------------------------------------

    /// <summary>
    /// 2026-07-28 obligation: never dereference $refs that resolve to a network
    /// URI — a schema is data from a partially trusted source. This sample
    /// supports local (#/...) references only and rejects everything else.
    /// </summary>
    static void AssertLocalRef(string reference, string path)
    {
        if (!reference.StartsWith('#'))
            throw new MappingError($"Refusing non-local $ref \"{reference}\" at {path}");
    }

    /// <summary>Inline #/$defs/... references so the schema is self-contained.</summary>
    public static JsonNode? InlineLocalRefs(JsonNode? node, JsonObject root, List<MappingAction> actions, string path = "#", int depth = 0)
    {
        if (depth > MaxDepth)
            throw new MappingError($"Schema exceeds depth {MaxDepth} at {path}");
        if (node is JsonArray array)
        {
            var items = new JsonArray();
            for (var i = 0; i < array.Count; i++)
                items.Add(InlineLocalRefs(array[i], root, actions, $"{path}/{i}", depth + 1));
            return items;
        }
        if (node is not JsonObject obj)
            return node?.DeepClone();
        if (obj["$ref"] is JsonValue refValue && refValue.TryGetValue<string>(out var reference))
        {
            AssertLocalRef(reference, path);
            JsonNode? target = root;
            foreach (var segment in reference[2..].Split('/'))
                target = (target as JsonObject)?[segment];
            if (target is null)
                throw new MappingError($"Unresolvable $ref \"{reference}\" at {path}");
            actions.Add(new MappingAction("inlined-ref", "$ref", path, reference));
            // 2020-12 allows keywords alongside $ref; merge them over the target.
            var merged = new JsonObject();
            foreach (var (key, value) in (JsonObject)target)
                merged[key] = value?.DeepClone();
            foreach (var (key, value) in obj)
                if (key != "$ref")
                    merged[key] = value?.DeepClone();
            return InlineLocalRefs(merged, root, actions, path, depth + 1);
        }
        var result = new JsonObject();
        foreach (var (key, value) in obj)
        {
            if (key == "$defs") continue; // Consumed by inlining.
            result[key] = InlineLocalRefs(value, root, actions, $"{path}/{key}", depth + 1);
        }
        return result;
    }

    /// <summary>Human phrasing for a lifted keyword — what the model sees instead.</summary>
    static string LiftPhrase(string keyword, JsonObject node) => keyword switch
    {
        "minimum" => node.ContainsKey("maximum")
            ? $"Must be between {node["minimum"]} and {node["maximum"]}."
            : $"Must be at least {node["minimum"]}.",
        "maximum" => $"Must be at most {node["maximum"]}.",
        "minLength" => node.ContainsKey("maxLength")
            ? $"Must be {node["minLength"]} to {node["maxLength"]} characters."
            : $"Must be at least {node["minLength"]} characters.",
        "maxLength" => $"Must be at most {node["maxLength"]} characters.",
        "pattern" => $"Must match the regular expression {node["pattern"]!.GetValue<string>()}.",
        "format" => $"Format: {node["format"]!.GetValue<string>()}.",
        "default" => $"Defaults to {node["default"]!.ToJsonString(Compact)}.",
        // The crude lift for a structural keyword: alternatives as prose.
        "oneOf" => $"Exactly one of these shapes: {node["oneOf"]!.ToJsonString(Compact)}.",
        _ => $"{keyword}: {node[keyword]!.ToJsonString(Compact)}.",
    };

    static void AppendToDescription(JsonObject node, string text) =>
        node["description"] = node["description"] is JsonValue existing
            ? $"{existing.GetValue<string>()} {text}"
            : text;

    /// <summary>
    /// Walk the schema applying the adapter's per-keyword strategy. Mutates the
    /// (already cloned) schema in place and records every action taken.
    /// </summary>
    public static void ApplyKeywordStrategies(JsonNode? node, OrderedDictionary<string, KeywordStrategy> strategies, List<MappingAction> actions, string path = "#", int depth = 0)
    {
        if (depth > MaxDepth)
            throw new MappingError($"Schema exceeds depth {MaxDepth} at {path}");
        if (node is JsonArray array)
        {
            for (var i = 0; i < array.Count; i++)
                ApplyKeywordStrategies(array[i], strategies, actions, $"{path}/{i}", depth + 1);
            return;
        }
        if (node is not JsonObject obj)
            return;
        if (obj["$ref"] is JsonValue refValue && refValue.TryGetValue<string>(out var reference))
            AssertLocalRef(reference, path); // Even when passed through.

        foreach (var (keyword, strategy) in strategies)
        {
            if (!obj.ContainsKey(keyword)) continue;
            switch (strategy)
            {
                case KeywordStrategy.Fail:
                    throw new MappingError($"Schema uses \"{keyword}\" at {path}, which this provider cannot represent");
                case KeywordStrategy.Strip:
                    actions.Add(new MappingAction("stripped", keyword, path, obj[keyword]?.ToJsonString(Compact) ?? "null"));
                    obj.Remove(keyword);
                    break;
                case KeywordStrategy.Lift:
                    var phrase = LiftPhrase(keyword, obj);
                    AppendToDescription(obj, phrase);
                    actions.Add(new MappingAction("lifted", keyword, path, phrase));
                    obj.Remove(keyword);
                    if (LiftPairs.TryGetValue(keyword, out var partner) &&
                        obj.ContainsKey(partner) &&
                        strategies.TryGetValue(partner, out var partnerStrategy) &&
                        partnerStrategy == KeywordStrategy.Lift)
                        obj.Remove(partner);
                    if (keyword == "oneOf" && !obj.ContainsKey("type"))
                        obj["type"] = "object";
                    break;
            }
        }

        foreach (var (key, value) in obj.ToList())
        {
            if (key is "description" or "enum" or "required") continue;
            ApplyKeywordStrategies(value, strategies, actions, $"{path}/{key}", depth + 1);
        }
    }

    // --- Forward mapping: one MCP tool through one adapter --------------------

    public static MappedTool MapTool(ProviderAdapter adapter, Tool tool, string serverPrefix)
    {
        var actions = new List<MappingAction>();
        var schema = JsonObject.Create(tool.InputSchema)!;
        if (adapter.InlineLocalRefs)
            schema = (JsonObject)InlineLocalRefs(schema, schema, actions)!;
        ApplyKeywordStrategies(schema, adapter.KeywordStrategies, actions);

        var description = tool.Description ?? "";
        var outputSchemaDecision = "none declared";
        JsonObject? nativeOutput = null;
        if (tool.OutputSchema is { } outputSchema)
        {
            switch (adapter.OutputSchema)
            {
                case OutputSchemaMode.Native:
                    nativeOutput = JsonObject.Create(outputSchema);
                    outputSchemaDecision = "passed through natively";
                    break;
                case OutputSchemaMode.Lift:
                    description += $" Returns JSON matching this schema: {JsonSerializer.Serialize(outputSchema, Compact)}";
                    outputSchemaDecision = "lifted into description";
                    break;
                case OutputSchemaMode.Drop:
                    outputSchemaDecision = "dropped (model does not need it; lifting would only add token cost)";
                    break;
            }
        }

        var definition = adapter.Wrap(NamespacedName(serverPrefix, tool.Name), description, schema, nativeOutput);
        return new MappedTool(definition, actions, outputSchemaDecision);
    }

    // --- Reverse mapping: model tool call -> MCP tools/call --------------------

    public static async Task<CallToolResult> RouteToolCallAsync(ModelToolCall call, Dictionary<string, McpClient> clients)
    {
        var (serverPrefix, toolName) = SplitNamespacedName(call.Name);
        if (!clients.TryGetValue(serverPrefix, out var client))
            throw new MappingError($"No connected server for prefix \"{serverPrefix}\"");
        var arguments = JsonSerializer.Deserialize<Dictionary<string, object?>>(call.Arguments)!;
        return await client.CallToolAsync(toolName, arguments);
    }
}
