// The client-side task methods from Chapter 7, §7.3: tasks/get, tasks/cancel, and
// the transparent auto-polling path. tasks/update (mid-flight input) is not shown —
// it needs an input_required task, which needs an elicitation-capable host.
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using System.Text.Json;

var serverProject = args.Length > 0 ? args[0] : "csharp";

var transport = new StdioClientTransport(new StdioClientTransportOptions
{
    Name = "tasks-polling",
    Command = "dotnet",
    Arguments = ["run", "--project", serverProject],
});

await using var client = await McpClient.CreateAsync(transport);

static Dictionary<string, JsonElement> ReportArgs(string topic, int seconds) => new()
{
    ["topic"] = JsonSerializer.SerializeToElement(topic),
    ["seconds"] = JsonSerializer.SerializeToElement(seconds),
};

// --- 1. Task creation is server-directed --------------------------------------
// CallToolRawAsync declares the tasks extension on the request and returns the raw
// response: either an immediate result or a task handle. The client never asks for
// a task; the server decides.
ResultOrCreatedTask<CallToolResult> response = await client.CallToolRawAsync(
    new CallToolRequestParams { Name = "generate_report", Arguments = ReportArgs("quarterly sales", 4) });

CreateTaskResult created = response.TaskCreated!;
Console.WriteLine($"tools/call   -> resultType: task, taskId: {created.TaskId}");
Console.WriteLine($"                status: {created.Status}, pollIntervalMs: {created.PollIntervalMs}");

// --- 2. tasks/get: poll until terminal; the last poll delivers the result -----
GetTaskResult snapshot = await client.GetTaskAsync(created.TaskId);
while (snapshot.Status is McpTaskStatus.Working or McpTaskStatus.InputRequired)
{
    Console.WriteLine($"tasks/get    -> {snapshot.Status}");
    await Task.Delay((int)(snapshot.PollIntervalMs ?? 1000));
    snapshot = await client.GetTaskAsync(created.TaskId);
}
Console.WriteLine($"tasks/get    -> {snapshot.Status}");

if (snapshot is CompletedTaskResult completed)
{
    // No tasks/result method exists: the completed snapshot inlines the CallToolResult.
    var result = JsonSerializer.Deserialize<CallToolResult>(completed.Result, McpJsonUtilities.DefaultOptions)!;
    Console.WriteLine($"result       -> {result.Content.OfType<TextContentBlock>().First().Text}");
}

// --- 3. tasks/cancel: cooperative, eventually consistent ----------------------
var slow = await client.CallToolRawAsync(
    new CallToolRequestParams { Name = "generate_report", Arguments = ReportArgs("all of history", 600) });

var slowTask = slow.TaskCreated!;
Console.WriteLine($"tools/call   -> resultType: task, taskId: {slowTask.TaskId}");

await client.CancelTaskAsync(slowTask.TaskId);
Console.WriteLine("tasks/cancel -> acknowledged (intent only; the server decides)");

var after = await client.GetTaskAsync(slowTask.TaskId);
Console.WriteLine($"tasks/get    -> {after.Status}");

// --- 4. The transparent path ---------------------------------------------------
// Plain CallToolAsync also declares the extension, then polls any task handle to
// completion internally — the whole lifecycle above, hidden behind one call.
var direct = await client.CallToolAsync(
    new CallToolRequestParams { Name = "generate_report", Arguments = ReportArgs("one more thing", 2) });
Console.WriteLine($"CallToolAsync -> {direct.Content.OfType<TextContentBlock>().First().Text}");
