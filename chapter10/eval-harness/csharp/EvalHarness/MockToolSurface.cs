using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;
using System.Text.Json;

// Rung 1 of the mocking spectrum (§10.1.4): no server in the loop at all. To avoid mock
// drift, the tool surface is not hand-written — it is snapshotted from the real server's
// tools/list, so the names, descriptions, and schemas the model sees are exactly
// production's. Only the RESULTS are fabricated.

sealed record ToolSnapshot(string Name, string Description, JsonElement Schema);

// An AIFunction that carries a real tool's surface but no implementation beyond fabrication.
sealed class MockTool(ToolSnapshot snapshot) : AIFunction
{
    public override string Name => snapshot.Name;
    public override string Description => snapshot.Description;
    public override JsonElement JsonSchema => snapshot.Schema;

    protected override ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments, CancellationToken cancellationToken) =>
        new(MockSurface.Fabricate(Name, arguments).Text);
}

static class MockSurface
{
    public static List<MockTool> FromRealTools(IEnumerable<McpClientTool> tools) =>
        tools.Select(t => new MockTool(new ToolSnapshot(t.Name, t.Description, t.JsonSchema)))
             .ToList();

    // Fabricated results, shaped like the real ones. The recovery path is preserved: an id
    // that didn't come from a (mock) search result still fails, so rung 1 can exercise the
    // agent's error handling.
    public static (string Text, bool IsError) Fabricate(
        string toolName, IReadOnlyDictionary<string, object?> args)
    {
        switch (toolName)
        {
            case "search_documents":
                return (JsonSerializer.Serialize(new
                {
                    matches = new[] { new
                    {
                        id = "mock-doc-1",
                        title = "Mock Document",
                        snippet = "Fabricated snippet for rung-1 evaluation.",
                    } },
                }), false);

            case "read_document":
                var id = args.GetValueOrDefault("id")?.ToString() ?? "";
                if (id != "mock-doc-1")
                    return ($"No document with id '{id}'. Ids come from search_documents " +
                            "results — search for the topic first, then read the id it returns.", true);
                return (JsonSerializer.Serialize(new
                {
                    id,
                    title = "Mock Document",
                    tags = new[] { "mock" },
                    body = "Fabricated document body for rung-1 evaluation.",
                }), false);

            case "count_documents":
                return (JsonSerializer.Serialize(new
                {
                    tag = args.GetValueOrDefault("tag")?.ToString() ?? "",
                    count = 3,
                }), false);

            default:
                return ($"No fabricated result for tool '{toolName}'.", true);
        }
    }
}
