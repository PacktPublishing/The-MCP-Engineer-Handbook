using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Reflection;
using System.Text;
using System.Text.Json;

var builder = Host.CreateApplicationBuilder(args);

// Stdio servers must not write logs to stdout — it would corrupt the JSON-RPC stream.
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithTools<ChannelTools>()
    // The SDK keeps registered tools in a hash-keyed dictionary, so tools/list comes back
    // in hash order: stable, but not the order the tools are declared in. Restore
    // declaration order, so check_messages is listed before reply.
    .WithRequestFilters(filters =>
    {
        var toolOrder = typeof(ChannelTools).GetMethods()
            .Where(method => method.GetCustomAttribute<McpServerToolAttribute>() is not null)
            .OrderBy(method => method.MetadataToken) // metadata tokens follow declaration order
            .Select((method, index) => (Name: method.GetCustomAttribute<McpServerToolAttribute>()!.Name!, index))
            .ToDictionary(entry => entry.Name, entry => entry.index);

        filters.AddListToolsFilter(next => async (request, cancellationToken) =>
        {
            var result = await next(request, cancellationToken);
            result.Tools = [.. result.Tools.OrderBy(t => toolOrder.GetValueOrDefault(t.Name, int.MaxValue))];
            return result;
        });
    });

// The JSONL backend is one trusted local conversation, so it needs a private directory:
// there is no shared default. ChannelHost creates one (and passes it here) when unset.
if (string.IsNullOrEmpty(ChannelTools.Dir))
{
    Console.Error.WriteLine("CHANNEL_DIR is not set: point it at a private directory (mktemp -d).");
    return 1;
}

await builder.Build().RunAsync();
return 0;

// A channel is an MCP server that carries a conversation. This one bridges a demo chat
// surface (the Chat console app) through two JSONL files; a real channel server bridges
// Telegram, Discord, Slack, or email the same way — the tool surface is the pattern,
// the backend is a detail. The demo trusts the inbox's "from" field: it is one local
// user's conversation, not authenticated sender identity.
[McpServerToolType]
public class ChannelTools
{
    public static readonly string? Dir = Environment.GetEnvironmentVariable("CHANNEL_DIR");
    private static string Inbox => Path.Combine(Dir!, "inbox.jsonl");
    private static string Outbox => Path.Combine(Dir!, "outbox.jsonl");

    // The read cursor lives in process memory: a channel is a stdio server, and the
    // process is the stream position — Chapter 10's stdio-affinity point in two fields.
    // It moves when a batch is returned, not when the agent has replied: delivery is
    // at-most-once per server process, and a restarted server replays from line 1.
    private static long _offset;  // bytes of the inbox already read
    private static int _line;     // inbox lines already read; line numbers are message IDs
    private static readonly Lock Gate = new();

    [McpServerTool(Name = "check_messages", UseStructuredContent = true), Description(
        "Check the channel for new messages from the user. Returns {messages: [{id, from, " +
        "text}], rejected: [line numbers of malformed inbox records]}; an empty messages " +
        "array means nothing new. The channel only speaks when polled.")]
    public static MessageBatch CheckMessages()
    {
        lock (Gate)
        {
            if (!File.Exists(Inbox)) return new([], []);

            byte[] fresh;
            using (var stream = new FileStream(Inbox, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                if (stream.Length <= _offset) return new([], []);
                stream.Seek(_offset, SeekOrigin.Begin);
                fresh = new byte[stream.Length - _offset];
                stream.ReadExactly(fresh);
            }

            // Only complete lines are records: a last line without its newline may still be
            // mid-append, so it stays unread until the writer finishes it.
            var complete = Array.LastIndexOf(fresh, (byte)'\n') + 1;
            List<ChannelMessage> messages = [];
            List<int> rejected = [];
            foreach (var line in Encoding.UTF8.GetString(fresh, 0, complete).Split('\n')[..^1])
            {
                var id = ++_line;
                if (string.IsNullOrWhiteSpace(line)) continue;
                if (Parse(line, id) is { } message) messages.Add(message);
                else rejected.Add(id); // reported, never retried: a complete bad line stays bad
            }

            // Every complete line is now either delivered or reported, so the cursor can pass it.
            _offset += complete;
            return new(messages, rejected);
        }
    }

    [McpServerTool(Name = "reply"), Description(
        "Send a reply to the user on the channel. This is the ONLY way to reach the " +
        "user — plain assistant output goes nowhere.")]
    public static string Reply([Description("The message text to send.")] string text)
    {
        Directory.CreateDirectory(Dir!);
        File.AppendAllText(Outbox,
            JsonSerializer.Serialize(new { from = "agent", text, ts = DateTime.UtcNow }) + Environment.NewLine);
        return "Sent.";
    }

    private static ChannelMessage? Parse(string line, int id)
    {
        try
        {
            var record = JsonSerializer.Deserialize<JsonElement>(line);
            return record.ValueKind == JsonValueKind.Object
                && record.TryGetProperty("from", out var from) && from.ValueKind == JsonValueKind.String
                && record.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String
                ? new(id, from.GetString()!, text.GetString()!)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

public record ChannelMessage(int Id, string From, string Text);
public record MessageBatch(IReadOnlyList<ChannelMessage> Messages, IReadOnlyList<int> Rejected);
