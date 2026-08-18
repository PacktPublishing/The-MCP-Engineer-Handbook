using System.Text.Json;

// The user's side of the demo channel — the stand-in for Telegram, Discord, or iMessage.
// It knows nothing about MCP: it appends your messages to the inbox file and prints
// whatever the agent's reply tool wrote to the outbox file.

var dir = Environment.GetEnvironmentVariable("CHANNEL_DIR")
          ?? Path.Combine(Path.GetTempPath(), "mcp-channel-demo");
Directory.CreateDirectory(dir);
var inbox = Path.Combine(dir, "inbox.jsonl");
var outbox = Path.Combine(dir, "outbox.jsonl");

Console.WriteLine($"Demo channel at {dir}");
Console.WriteLine("Type a message and press Enter. Ctrl+C to quit.");

var printed = File.Exists(outbox) ? File.ReadAllLines(outbox).Length : 0;
_ = Task.Run(async () =>
{
    while (true)
    {
        if (File.Exists(outbox))
        {
            var lines = File.ReadAllLines(outbox);
            for (; printed < lines.Length; printed++)
            {
                var msg = JsonSerializer.Deserialize<JsonElement>(lines[printed]);
                Console.WriteLine($"\nagent> {msg.GetProperty("text").GetString()}");
                Console.Write("you> ");
            }
        }
        await Task.Delay(500);
    }
});

while (true)
{
    Console.Write("you> ");
    var line = Console.ReadLine();
    if (line is null) break;
    if (string.IsNullOrWhiteSpace(line)) continue;
    File.AppendAllText(inbox,
        JsonSerializer.Serialize(new { from = "user", text = line, ts = DateTime.UtcNow }) + Environment.NewLine);
}
