// ResourceLinkClient — resolving resource links (§3.3.4).
//
// A tool can return a POINTER to a resource instead of its content. Most servers that
// do this expect the client to read the resource and substitute the contents into the
// tool result. This sample shows both versions from the chapter:
//
//   1. the book-page ResolveLinksAsync — the bare substitution pass, run against the
//      demo server's get_tip_of_the_day
//   2. the production version — size budget, MIME-type filtering, error handling for
//      failed reads, and a depth guard against link chains — run against
//      get_research_bundle, whose five links exercise every guard
//
// Usage: dotnet run

using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using System.Text;
using System.Text.Json;

await using var client = await McpClient.CreateAsync(
    new StdioClientTransport(new StdioClientTransportOptions
    {
        Name = "docs",
        Command = "dotnet",
        Arguments = [ResolveServerDll()],
    }));

// --- 1. The book-page version against a single well-behaved link ---------------------

Console.WriteLine(new string('=', 78));
Console.WriteLine("1. Bare substitution pass (the §3.3.4 book extract) on get_tip_of_the_day");
Console.WriteLine(new string('=', 78));

var tipResult = await client.CallToolAsync("get_tip_of_the_day", new Dictionary<string, object?>());
PrintBlocks("tool result as returned", tipResult.Content);
PrintBlocks("after ResolveLinksAsync", await ResolveLinksAsync(tipResult.Content, client));

// --- 2. The hardened version against links that misbehave ----------------------------

Console.WriteLine(new string('=', 78));
Console.WriteLine("2. Hardened resolution on get_research_bundle (size / MIME / errors / depth)");
Console.WriteLine(new string('=', 78));

var bundleResult = await client.CallToolAsync("get_research_bundle", new Dictionary<string, object?>());
PrintBlocks("tool result as returned", bundleResult.Content);

var resolver = new HardenedLinkResolver(new LinkResolutionOptions
{
    MaxResourceBytes = 16 * 1024,             // big_dataset declares 64 000 bytes -> rejected unread
    AllowedMimePrefixes = ["text/", "application/json"], // audio/wav -> filtered out
    MaxDepth = 2,                             // chain a -> b -> c trips the guard at hop 3
});
PrintBlocks("after hardened resolution", await resolver.ResolveAsync(bundleResult.Content, client));
return;

// §3.3.4 book extract: the core is a substitution pass over the tool result's content
// blocks. Links must be resolved against the server that returned them — never a
// different one (the origin-binding rule from §3.3.3).
async Task<IList<ContentBlock>> ResolveLinksAsync(
    IList<ContentBlock> content, McpClient client)
{
    var resolved = new List<ContentBlock>();
    foreach (var block in content)
    {
        if (block is ResourceLinkBlock link)
        {
            var read = await client.ReadResourceAsync(link.Uri);
            resolved.AddRange(read.Contents.Select(c => c.ToAIContent().ToContentBlock()));
        }
        else
        {
            resolved.Add(block);
        }
    }
    return resolved;
}

static void PrintBlocks(string label, IList<ContentBlock> blocks)
{
    Console.WriteLine($"--- {label} ({blocks.Count} block(s)) ---");
    foreach (var block in blocks)
    {
        var line = block switch
        {
            ResourceLinkBlock l =>
                $"resource_link  uri={l.Uri}  name={l.Name}" +
                (l.MimeType is null ? "" : $"  mimeType={l.MimeType}") +
                (l.Size is null ? "" : $"  size={l.Size}"),
            TextContentBlock t => $"text           {Truncate(t.Text, 140)}",
            ImageContentBlock i => $"image          mimeType={i.MimeType}",
            AudioContentBlock a => $"audio          mimeType={a.MimeType}",
            _ => block.Type,
        };
        Console.WriteLine($"  {line.ReplaceLineEndings("\n" + new string(' ', 17))}");
    }
    Console.WriteLine();
}

static string Truncate(string text, int max) =>
    text.Length <= max ? text : $"{text[..max]}… ({text.Length} chars total)";

// Locate the built demo server next to this sample. Build chapter03/demo-resource-server first.
static string ResolveServerDll()
{
    if (Environment.GetEnvironmentVariable("DEMO_RESOURCE_SERVER_DLL") is { Length: > 0 } overridePath)
        return overridePath;

    var candidate = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "..",
        "demo-resource-server", "csharp", "bin", "Debug", "net10.0", "DemoResourceServer.dll"));
    if (!File.Exists(candidate))
        throw new FileNotFoundException(
            $"DemoResourceServer.dll not found at {candidate}. Run 'dotnet build' in " +
            "chapter03/demo-resource-server/csharp first, or point DEMO_RESOURCE_SERVER_DLL at the built server.");
    return candidate;
}

record LinkResolutionOptions
{
    /// <summary>Budget per linked resource, checked against the link's declared
    /// <c>size</c> before reading and against the actual content after.</summary>
    public long MaxResourceBytes { get; init; } = 16 * 1024;

    /// <summary>MIME-type prefixes the target model can actually consume.</summary>
    public string[] AllowedMimePrefixes { get; init; } = ["text/"];

    /// <summary>Maximum link-chain depth. Links in the tool result resolve at depth 1;
    /// a link found inside resolved content resolves one level deeper.</summary>
    public int MaxDepth { get; init; } = 2;
}

// What the book page leaves out (§3.3.4): a resource link can point at ANYTHING, so
// production resolution needs a size budget, MIME filtering for model compatibility,
// error handling for failed reads, and a depth guard against link chains. Where a
// guard drops a link, an explanatory text block takes its place so the model knows
// what happened instead of silently losing context.
sealed class HardenedLinkResolver(LinkResolutionOptions options)
{
    public async Task<IList<ContentBlock>> ResolveAsync(IList<ContentBlock> content, McpClient client)
    {
        var visited = new HashSet<string>(); // cycle detection across the whole pass
        return await ResolveAtDepthAsync(content, client, depth: 1, visited);
    }

    private async Task<List<ContentBlock>> ResolveAtDepthAsync(
        IList<ContentBlock> content, McpClient client, int depth, HashSet<string> visited)
    {
        var resolved = new List<ContentBlock>();
        foreach (var block in content)
        {
            if (block is ResourceLinkBlock link)
                resolved.AddRange(await ResolveOneLinkAsync(link, client, depth, visited));
            else
                resolved.Add(block);
        }
        return resolved;
    }

    private async Task<List<ContentBlock>> ResolveOneLinkAsync(
        ResourceLinkBlock link, McpClient client, int depth, HashSet<string> visited)
    {
        Console.WriteLine($"  [resolve] {link.Uri} (depth {depth})");

        // Depth guard: link chains (and cycles) must terminate.
        if (depth > options.MaxDepth)
            return Drop(link, $"chain depth {depth} exceeds the maximum of {options.MaxDepth}");
        if (!visited.Add(link.Uri))
            return Drop(link, "link cycle detected — this URI was already resolved in this pass");

        // Size budget, part 1: a declared size lets us reject without reading at all.
        if (link.Size is { } declaredSize && declaredSize > options.MaxResourceBytes)
            return Drop(link, $"declared size {declaredSize} exceeds the budget of {options.MaxResourceBytes} bytes");

        // MIME filter, part 1: a declared type lets us skip content the model can't take.
        if (link.MimeType is { } declaredMime && !MimeAllowed(declaredMime))
            return Drop(link, $"declared MIME type '{declaredMime}' is not model-compatible");

        // Error handling: a link is a promise the server doesn't have to keep.
        ReadResourceResult read;
        try
        {
            read = await client.ReadResourceAsync(link.Uri);
        }
        catch (McpException ex)
        {
            return Drop(link, $"read failed: {ex.Message}");
        }

        var resolved = new List<ContentBlock>();
        foreach (var contents in read.Contents)
        {
            // MIME filter and size budget, part 2: links may omit metadata, so re-check
            // what the read actually returned.
            var actualMime = contents.MimeType ?? link.MimeType;
            if (actualMime is not null && !MimeAllowed(actualMime))
            {
                resolved.AddRange(Drop(link, $"content MIME type '{actualMime}' is not model-compatible"));
                continue;
            }
            var actualSize = contents is TextResourceContents text
                ? Encoding.UTF8.GetByteCount(text.Text ?? "")
                : options.MaxResourceBytes + 1; // non-text got past the filter without a type: don't inject it
            if (actualSize > options.MaxResourceBytes)
            {
                resolved.AddRange(Drop(link, $"content size {actualSize} exceeds the budget of {options.MaxResourceBytes} bytes"));
                continue;
            }

            // Chain convention: a read result cannot carry a resource link natively
            // (contents are text|blob only), but some servers tunnel an onward link as
            // JSON content. Follow it — that is exactly what the depth guard is for.
            if (contents is TextResourceContents { Text: { } json } &&
                actualMime == "application/json" && json.Contains("\"resource_link\"") &&
                TryParseLink(json) is { } onwardLink)
            {
                resolved.AddRange(await ResolveOneLinkAsync(onwardLink, client, depth + 1, visited));
                continue;
            }

            resolved.Add(contents.ToAIContent().ToContentBlock());
        }
        return resolved;
    }

    private bool MimeAllowed(string mimeType) =>
        options.AllowedMimePrefixes.Any(mimeType.StartsWith);

    private static ResourceLinkBlock? TryParseLink(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<ContentBlock>(json, McpJsonUtilities.DefaultOptions)
                as ResourceLinkBlock;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // Replace a dropped link with an explanation the model can see and act on.
    private static List<ContentBlock> Drop(ResourceLinkBlock link, string reason)
    {
        Console.WriteLine($"  [guard]   {link.Uri}: {reason}");
        return [new TextContentBlock
        {
            Text = $"[resource link '{link.Name}' ({link.Uri}) was not resolved: {reason}]",
        }];
    }
}
