using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Text.Json;

var builder = Host.CreateApplicationBuilder(args);

// Stdio servers must not write logs to stdout — it would corrupt the JSON-RPC stream.
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithTools<ChannelTools>();

await builder.Build().RunAsync();

// A channel is an MCP server that carries a conversation. This one bridges a demo chat
// surface (the Chat console app) through two JSONL files; a real channel server bridges
// Telegram, Discord, Slack, or email the same way — the tool surface is the pattern,
// the backend is a detail.
[McpServerToolType]
public class ChannelTools
{
    private static readonly string Dir =
        Environment.GetEnvironmentVariable("CHANNEL_DIR")
        ?? Path.Combine(Path.GetTempPath(), "mcp-channel-demo");
    private static string Inbox => Path.Combine(Dir, "inbox.jsonl");
    private static string Outbox => Path.Combine(Dir, "outbox.jsonl");

    // The read cursor lives in process memory: a channel is a stdio server, and the
    // process is the stream position — Chapter 10's stdio-affinity point in one field.
    private static int _consumed;

    [McpServerTool(Name = "check_messages"), Description(
        "Check the channel for new messages from the user. Returns one message per line " +
        "as 'sender: text', or 'No new messages.' Call this periodically; the channel " +
        "only speaks when polled.")]
    public static string CheckMessages()
    {
        Directory.CreateDirectory(Dir);
        if (!File.Exists(Inbox)) return "No new messages.";

        var lines = File.ReadAllLines(Inbox);
        if (lines.Length <= _consumed) return "No new messages.";

        var fresh = lines[_consumed..];
        _consumed = lines.Length;
        return string.Join('\n', fresh.Select(line =>
        {
            var msg = JsonSerializer.Deserialize<JsonElement>(line);
            return $"{msg.GetProperty("from").GetString()}: {msg.GetProperty("text").GetString()}";
        }));
    }

    [McpServerTool(Name = "reply"), Description(
        "Send a reply to the user on the channel. This is the ONLY way to reach the " +
        "user — plain assistant output goes nowhere.")]
    public static string Reply([Description("The message text to send.")] string text)
    {
        Directory.CreateDirectory(Dir);
        File.AppendAllText(Outbox,
            JsonSerializer.Serialize(new { from = "agent", text, ts = DateTime.UtcNow }) + Environment.NewLine);
        return "Sent.";
    }
}
