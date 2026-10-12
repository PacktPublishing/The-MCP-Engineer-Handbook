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
//      get_research_bundle, whose five links exercise those guards
//   3. the same resolver's per-pass limits — byte budget, link limit, deadline — plus
//      cycle-vs-duplicate handling, against tool results shaped the way a careless or
//      hostile server might return them: once a pass limit trips, the rest of the
//      result is summarized in one notice instead of one notice per link
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

// --- 3. Per-pass limits against tool results that misbehave as a whole --------------

// The demo server's tools are well-behaved, so these tool results are built client-side,
// the way a careless or hostile server could return them. Every read still goes to the
// real server.

Console.WriteLine(new string('=', 78));
Console.WriteLine("3a. Per-resource guards and the pass byte budget (repeats / understated size /");
Console.WriteLine("    multi-item read / cycle / pass byte budget)");
Console.WriteLine(new string('=', 78));

List<ContentBlock> misbehaving =
[
    new ResourceLinkBlock { Uri = "file:///release_notes.md", Name = "release_notes" },
    new ResourceLinkBlock { Uri = "file:///release_notes.md", Name = "release_notes" }, // duplicate, not a cycle
    new ResourceLinkBlock { Uri = "file:///release_notes.md", Name = "release_notes" }, // further repeat: counted only
    new ResourceLinkBlock { Uri = "file:///big_dataset.csv", Name = "big_dataset", Size = 2048 }, // understated
    new ResourceLinkBlock { Uri = "file://notes/7/attachments", Name = "note_attachments" },    // 3 items, 18 000 bytes
    new ResourceLinkBlock { Uri = "chain://a", Name = "chain_a" },                       // a -> b -> c -> a
    new ResourceLinkBlock { Uri = "file:///user_guide.md", Name = "user_guide" },
    .. Enumerable.Range(1, 5).Select(id => new ResourceLinkBlock
    {
        Uri = $"file://user/{id}/preferences.md", Name = $"preferences_{id}",
    }),
];
PrintBlocks("tool result as returned", misbehaving);

var strictResolver = new HardenedLinkResolver(new LinkResolutionOptions
{
    MaxResourceBytes = 16 * 1024,             // big_dataset's real 64 015 bytes and the 3 x 6 000-byte
                                              // attachments -> rejected after reading
    MaxTotalBytes = 1024,                     // user_guide (571 bytes) no longer fits: the pass stops
    AllowedMimePrefixes = ["text/", "application/json"],
    MaxDepth = 4,                             // deep enough that the chain's return to a is seen as a cycle
});
PrintBlocks("after hardened resolution", await strictResolver.ResolveAsync(misbehaving, client));

Console.WriteLine(new string('=', 78));
Console.WriteLine("3b. A tool result with 1 000 links (link limit)");
Console.WriteLine(new string('=', 78));

List<ContentBlock> linkFlood = [.. Enumerable.Range(1, 1000).Select(id => new ResourceLinkBlock
{
    Uri = $"file://user/{id}/preferences.md", Name = $"preferences_{id}",
})];
Console.WriteLine($"--- tool result as returned ({linkFlood.Count} block(s): file://user/1/preferences.md … file://user/1000/preferences.md) ---\n");

var floodResolver = new HardenedLinkResolver(new LinkResolutionOptions
{
    MaxLinks = 4,                             // links 5..1000 end up in one summary notice
});
PrintBlocks("after hardened resolution", await floodResolver.ResolveAsync(linkFlood, client));

Console.WriteLine(new string('=', 78));
Console.WriteLine("3c. A slow read and the pass deadline");
Console.WriteLine(new string('=', 78));

List<ContentBlock> slow =
[
    new ResourceLinkBlock { Uri = "file://exports/2026-q3/telemetry.csv", Name = "telemetry_export" }, // takes 3 s
    new ResourceLinkBlock { Uri = "file:///release_notes.md", Name = "release_notes" },
    new ResourceLinkBlock { Uri = "file:///helpful_tip.txt", Name = "helpful_tip" },
];
PrintBlocks("tool result as returned", slow);

var hurriedResolver = new HardenedLinkResolver(new LinkResolutionOptions
{
    Timeout = TimeSpan.FromSeconds(1),        // the export read is cancelled; the rest is summarized
});
PrintBlocks("after hardened resolution", await hurriedResolver.ResolveAsync(slow, client));
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
    /// <c>size</c> before reading and against the actual bytes of every content item
    /// the read returned after.</summary>
    public long MaxResourceBytes { get; init; } = 16 * 1024;

    /// <summary>Budget for the whole pass: bytes accepted from all reads made while
    /// resolving one tool result.</summary>
    public long MaxTotalBytes { get; init; } = 64 * 1024;

    /// <summary>Maximum links handled per pass (read, or dropped by a guard), however
    /// many the tool result (or the chains it starts) contains. Repeats of a link
    /// already handled don't count.</summary>
    public int MaxLinks { get; init; } = 16;

    /// <summary>Deadline for the whole pass. A read still in flight when it passes is
    /// cancelled.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>MIME-type prefixes the target model can actually consume.</summary>
    public string[] AllowedMimePrefixes { get; init; } = ["text/"];

    /// <summary>Maximum link-chain depth. Links in the tool result resolve at depth 1;
    /// a link found inside resolved content resolves one level deeper.</summary>
    public int MaxDepth { get; init; } = 2;
}

// What the book page leaves out (§3.3.4): a resource link can point at ANYTHING, so
// production resolution needs size budgets, MIME filtering for model compatibility,
// error handling for failed reads, and guards that make link chains and link-heavy
// results terminate. Where a guard drops a link, an explanatory text block takes its
// place so the model knows what happened instead of silently losing context. Once a
// pass limit trips, the links still left get ONE summary notice instead of one each,
// so the notices stay bounded too.
sealed class HardenedLinkResolver(LinkResolutionOptions options)
{
    public async Task<IList<ContentBlock>> ResolveAsync(IList<ContentBlock> content, McpClient client)
    {
        using var deadline = new CancellationTokenSource(options.Timeout);
        var pass = new ResolutionPass(deadline.Token);
        var resolved = await ResolveAtDepthAsync(content, client, depth: 1, pass);
        resolved.AddRange(Summary(pass));
        return resolved;
    }

    private async Task<List<ContentBlock>> ResolveAtDepthAsync(
        IList<ContentBlock> content, McpClient client, int depth, ResolutionPass pass)
    {
        var resolved = new List<ContentBlock>();
        foreach (var block in content)
        {
            if (block is ResourceLinkBlock link)
                resolved.AddRange(await ResolveOneLinkAsync(link, client, depth, pass));
            else
                resolved.Add(block);
        }
        return resolved;
    }

    private async Task<List<ContentBlock>> ResolveOneLinkAsync(
        ResourceLinkBlock link, McpClient client, int depth, ResolutionPass pass)
    {
        // A pass limit has tripped: the rest of the result goes into the summary.
        if (pass.StopReason is not null)
            return Skip(link, pass);

        Console.WriteLine($"  [resolve] {link.Uri} (depth {depth})");

        // Cycle vs. duplicate: only a URI on the chain currently being followed is a
        // cycle. Two links to the same URI side by side, or two chains converging on one
        // resource, are ordinary repetition: point at the earlier result, don't re-read.
        if (pass.Seen.Contains(link.Uri) && !pass.ActivePath.Contains(link.Uri))
            return Duplicate(link, pass);

        // Link limit: bound the pass as a whole, however many links it holds.
        if (pass.Links >= options.MaxLinks)
            return Stop(link, pass, $"link limit of {options.MaxLinks} per pass reached");
        pass.Links++;

        // Depth guard: link chains must terminate.
        if (depth > options.MaxDepth)
            return Drop(link, $"chain depth {depth} exceeds the maximum of {options.MaxDepth}");
        if (pass.ActivePath.Contains(link.Uri))
            return Drop(link, "link cycle detected — this URI is already being resolved further up the chain");

        pass.Seen.Add(link.Uri);
        pass.ActivePath.Add(link.Uri);
        try
        {
            return await ReadAndSubstituteAsync(link, client, depth, pass);
        }
        finally
        {
            pass.ActivePath.Remove(link.Uri);
        }
    }

    private async Task<List<ContentBlock>> ReadAndSubstituteAsync(
        ResourceLinkBlock link, McpClient client, int depth, ResolutionPass pass)
    {
        // Size budget, part 1: a declared size lets us reject without reading at all.
        if (link.Size is { } declaredSize && declaredSize > options.MaxResourceBytes)
            return Drop(link, $"declared size {declaredSize} exceeds the budget of {options.MaxResourceBytes} bytes");

        // MIME filter, part 1: a declared type lets us skip content the model can't take.
        if (link.MimeType is { } declaredMime && !MimeAllowed(declaredMime))
            return Drop(link, $"declared MIME type '{declaredMime}' is not model-compatible");

        // Deadline: bound the pass in time as well.
        if (pass.Deadline.IsCancellationRequested)
            return Stop(link, pass, $"pass deadline of {options.Timeout.TotalSeconds}s has passed");

        // Error handling: a link is a promise the server doesn't have to keep.
        ReadResourceResult read;
        try
        {
            read = await client.ReadResourceAsync(link.Uri, cancellationToken: pass.Deadline);
        }
        catch (McpException ex)
        {
            return Drop(link, $"read failed: {ex.Message}");
        }
        catch (OperationCanceledException) when (pass.Deadline.IsCancellationRequested)
        {
            pass.StopReason = $"pass deadline of {options.Timeout.TotalSeconds}s has passed";
            return Drop(link, $"read cancelled: {pass.StopReason}");
        }

        // Size budget, part 2: links may omit or understate their size, so count what the
        // read actually returned — across ALL its content items, since one resource can
        // return many. The response is already in memory by now: this bounds what
        // reaches the model, not what the transport buffers.
        var readBytes = read.Contents.Sum(ContentBytes);
        if (readBytes > options.MaxResourceBytes)
            return Drop(link, $"content size {readBytes}{Declared(link)} exceeds the budget of {options.MaxResourceBytes} bytes");
        if (pass.Bytes + readBytes > options.MaxTotalBytes)
        {
            pass.StopReason = $"pass budget of {options.MaxTotalBytes} bytes reached";
            return Drop(link, $"content size {readBytes} would exceed the pass budget of {options.MaxTotalBytes} bytes ({pass.Bytes} already used)");
        }
        pass.Bytes += readBytes;

        var resolved = new List<ContentBlock>();
        foreach (var contents in read.Contents)
        {
            // MIME filter, part 2: links may omit metadata, so re-check every item.
            var actualMime = contents.MimeType ?? link.MimeType;
            if (actualMime is not null && !MimeAllowed(actualMime))
            {
                resolved.AddRange(Drop(link, $"content MIME type '{actualMime}' is not model-compatible"));
                continue;
            }
            if (actualMime is null && contents is BlobResourceContents)
            {
                resolved.AddRange(Drop(link, "binary content without a MIME type"));
                continue;
            }

            // Chain convention: a read result cannot carry a resource link natively
            // (contents are text|blob only), but some servers tunnel an onward link as
            // JSON content. Follow it — that is exactly what the depth guard is for.
            if (contents is TextResourceContents { Text: { } json } &&
                actualMime == "application/json" && json.Contains("\"resource_link\"") &&
                TryParseLink(json) is { } onwardLink)
            {
                resolved.AddRange(await ResolveOneLinkAsync(onwardLink, client, depth + 1, pass));
                continue;
            }

            resolved.Add(contents.ToAIContent().ToContentBlock());
        }
        return resolved;
    }

    private bool MimeAllowed(string mimeType) =>
        options.AllowedMimePrefixes.Any(mimeType.StartsWith);

    private static long ContentBytes(ResourceContents contents) => contents switch
    {
        TextResourceContents text => Encoding.UTF8.GetByteCount(text.Text ?? ""),
        BlobResourceContents blob => blob.DecodedData.Length,
        _ => 0,
    };

    private static string Declared(ResourceLinkBlock link) =>
        link.Size is { } size ? $" (the link declared {size})" : "";

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

    // A repeated link is not an error: refer the model to the earlier occurrence — once
    // per URI. Further repeats are only counted, for the summary.
    private static List<ContentBlock> Duplicate(ResourceLinkBlock link, ResolutionPass pass)
    {
        if (!pass.Noted.Add(link.Uri))
        {
            Console.WriteLine($"  [dedupe]  {link.Uri}: repeat omitted (counted for the summary)");
            pass.RepeatsOmitted++;
            return [];
        }
        Console.WriteLine($"  [dedupe]  {link.Uri}: already handled earlier in this pass");
        return [new TextContentBlock
        {
            Text = $"[resource link '{link.Name}' ({link.Uri}) repeats an earlier link in this result; " +
                   "see its content, or why it was not resolved, above]",
        }];
    }

    // A pass limit trips: no more reads this pass, and this link is the first one left.
    private static List<ContentBlock> Stop(ResourceLinkBlock link, ResolutionPass pass, string reason)
    {
        Console.WriteLine($"  [limit]   {reason}: the remaining links are summarized");
        pass.StopReason = reason;
        return Skip(link, pass);
    }

    private static List<ContentBlock> Skip(ResourceLinkBlock link, ResolutionPass pass)
    {
        pass.Skipped++;
        if (pass.SkippedUris.Count < 3 && !pass.SkippedUris.Contains(link.Uri))
            pass.SkippedUris.Add(link.Uri);
        return [];
    }

    // One notice for everything a tripped limit left unresolved, and one for omitted
    // repeats — however many links that is.
    private static List<ContentBlock> Summary(ResolutionPass pass)
    {
        List<ContentBlock> notices = [];
        if (pass.Skipped > 0)
        {
            var more = pass.Skipped - pass.SkippedUris.Count;
            var uris = string.Join(", ", pass.SkippedUris) + (more > 0 ? $" and {more} more" : "");
            Console.WriteLine($"  [summary] {pass.Skipped} further link(s) not resolved: {pass.StopReason}");
            notices.Add(new TextContentBlock
            {
                Text = $"[{pass.Skipped} further resource link(s) in this result were not resolved: " +
                       $"{pass.StopReason}. Not resolved: {uris}]",
            });
        }
        if (pass.RepeatsOmitted > 0)
        {
            Console.WriteLine($"  [summary] {pass.RepeatsOmitted} further repeat(s) omitted");
            notices.Add(new TextContentBlock
            {
                Text = $"[{pass.RepeatsOmitted} further repeat(s) of resource links already handled above were omitted]",
            });
        }
        return notices;
    }
}

// State for ONE resolution pass (one tool result). The budgets live here rather than
// per link, so a result carrying thousands of small links is bounded as a whole.
sealed class ResolutionPass(CancellationToken deadline)
{
    public CancellationToken Deadline { get; } = deadline;
    public HashSet<string> ActivePath { get; } = []; // URIs on the chain being followed now
    public HashSet<string> Seen { get; } = [];       // every URI this pass has started on
    public HashSet<string> Noted { get; } = [];      // URIs that already have a repeat notice
    public long Bytes { get; set; }                  // bytes accepted from reads so far
    public int Links { get; set; }                   // links handled so far (see MaxLinks)
    public string? StopReason { get; set; }          // the pass limit that tripped, if any
    public int Skipped { get; set; }                 // links left unresolved after it tripped
    public List<string> SkippedUris { get; } = [];   // the first few of them, for the summary
    public int RepeatsOmitted { get; set; }          // repeats beyond the first, per URI
}
