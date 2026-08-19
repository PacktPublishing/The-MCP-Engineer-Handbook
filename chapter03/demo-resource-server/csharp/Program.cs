// DemoResourceServer — the shared server for the chapter 3 client samples.
//
// It exposes a small catalog of text/markdown resources, a resource template (§3.4.1),
// a tool that returns a resource link instead of content (§3.3.2), and a handful of
// deliberately awkward resources (oversized, binary, broken, chained links) that the
// resource-link-client sample uses to exercise its hardened link resolution (§3.3.4).

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Text;

var builder = Host.CreateApplicationBuilder(args);

// Stdio servers must not write logs to stdout — it would corrupt the JSON-RPC stream.
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithTools<DemoTools>()
    .WithResources<DemoResources>();

await builder.Build().RunAsync();

[McpServerToolType]
public class DemoTools
{
    // Book extract (§3.3.2): a tool that returns a POINTER to a resource, not the payload.
    [McpServerTool]
    ContentBlock GetTipOfTheDay()
    {
        return new ResourceLinkBlock()
        {
            Uri = "file:///helpful_tip.txt",
            Name = "tip_of_the_day"
        };
    }

    // Returns several links at once so the resource-link-client can demonstrate every
    // hardening from §3.3.4 in a single pass: a normal link, an oversized one (size
    // budget), a binary one (MIME filtering), a dangling one (error handling), and the
    // start of a link chain (depth guard).
    [McpServerTool(Name = "get_research_bundle"),
     Description("Returns links to everything relevant for the quarterly research write-up.")]
    IEnumerable<ContentBlock> GetResearchBundle()
    {
        return
        [
            new ResourceLinkBlock
            {
                Uri = "file:///release_notes.md",
                Name = "release_notes",
                MimeType = "text/markdown",
            },
            new ResourceLinkBlock
            {
                Uri = "file:///big_dataset.csv",
                Name = "big_dataset",
                MimeType = "text/csv",
                Size = DemoResources.BigDatasetSize, // declared up front — a budget can reject without reading
            },
            new ResourceLinkBlock
            {
                Uri = "file:///podcast.wav",
                Name = "quarterly_podcast",
                MimeType = "audio/wav",
            },
            new ResourceLinkBlock
            {
                Uri = "file:///does_not_exist.txt",
                Name = "broken_link",
            },
            new ResourceLinkBlock
            {
                Uri = "chain://a",
                Name = "chain_start",
                MimeType = "application/json",
            },
        ];
    }
}

[McpServerResourceType]
public class DemoResources
{
    // --- Ordinary text/markdown resources -------------------------------------------

    [McpServerResource(UriTemplate = "file:///user_guide.md", Name = "user_guide",
        Title = "Nimbus Notes User Guide", MimeType = "text/markdown"),
     Description("Getting-started guide for the Nimbus Notes application.")]
    public static string UserGuide() => """
        # Nimbus Notes — User Guide

        ## Setup
        1. Install the Nimbus Notes desktop client (v3 or later).
        2. Sign in with your workspace account.
        3. Choose a vault location; local-only vaults skip cloud sync entirely.

        ## Daily use
        - `Ctrl+N` creates a note; notes autosave every five seconds.
        - Tag notes with `#project/...` tags to group them in the sidebar.
        - The command palette (`Ctrl+K`) reaches every feature without the mouse.

        ## Troubleshooting
        If sync stalls, check Settings > Sync > Log. A red entry means the vault
        key changed; re-enter it under Settings > Security.
        """;

    [McpServerResource(UriTemplate = "file:///release_notes.md", Name = "release_notes",
        Title = "Release Notes 3.2", MimeType = "text/markdown"),
     Description("What changed in Nimbus Notes 3.2.")]
    public static string ReleaseNotes() => """
        # Nimbus Notes 3.2 — Release Notes

        - New: offline vaults can now be converted to synced vaults in place.
        - Improved: search indexes attachments up to 25 MB.
        - Fixed: the sidebar no longer forgets collapsed tag groups on restart.
        - Deprecated: the legacy `nimbus://` deep-link scheme; use `notes://`.
        """;

    [McpServerResource(UriTemplate = "file:///helpful_tip.txt", Name = "helpful_tip",
        Title = "Tip of the Day", MimeType = "text/plain"),
     Description("A rotating usage tip. Target of the get_tip_of_the_day tool's resource link.")]
    public static string HelpfulTip() =>
        "Tip of the day: pin a note to the sidebar by dragging it onto the star icon — " +
        "pinned notes survive vault switches.";

    // --- Awkward resources for the link-resolution hardening demo -------------------

    public const int BigDatasetSize = 64_000;

    [McpServerResource(UriTemplate = "file:///big_dataset.csv", Name = "big_dataset",
        Title = "Quarterly Telemetry Export", MimeType = "text/csv"),
     Description("A large CSV export. Deliberately oversized to exercise client size budgets.")]
    public static string BigDataset()
    {
        var sb = new StringBuilder(BigDatasetSize + 64);
        sb.AppendLine("day,active_users,notes_created,sync_errors");
        for (int day = 1; sb.Length < BigDatasetSize; day++)
            sb.AppendLine($"{day},{1000 + day * 7 % 500},{300 + day * 13 % 900},{day * 3 % 17}");
        return sb.ToString();
    }

    [McpServerResource(UriTemplate = "file:///podcast.wav", Name = "quarterly_podcast",
        Title = "Quarterly Podcast", MimeType = "audio/wav"),
     Description("A binary audio resource. Exercises client MIME-type filtering.")]
    public static ResourceContents Podcast()
    {
        // A 44-byte silent WAV header — real enough to be honest about the MIME type.
        byte[] wav = [0x52, 0x49, 0x46, 0x46, 0x24, 0, 0, 0, 0x57, 0x41, 0x56, 0x45,
                      0x66, 0x6D, 0x74, 0x20, 0x10, 0, 0, 0, 1, 0, 1, 0,
                      0x44, 0xAC, 0, 0, 0x88, 0x58, 0x01, 0, 2, 0, 0x10, 0,
                      0x64, 0x61, 0x74, 0x61, 0, 0, 0, 0];
        return BlobResourceContents.FromBytes(wav, "file:///podcast.wav", "audio/wav");
    }

    // A link CHAIN: each resource's content is itself a serialized resource_link content
    // block pointing at the next hop — and the last hop points back to the first, forming
    // a cycle. The protocol cannot express links in a read result natively (contents are
    // text|blob only), so onward links like these exist purely by client/server convention;
    // the resource-link-client follows the convention and shows its depth guard stopping
    // the cycle.
    [McpServerResource(UriTemplate = "chain://a", Name = "chain_a", MimeType = "application/json"),
     Description("First hop of a deliberate link chain (a -> b -> c -> a).")]
    public static string ChainA() => """{"type":"resource_link","uri":"chain://b","name":"chain_b"}""";

    [McpServerResource(UriTemplate = "chain://b", Name = "chain_b", MimeType = "application/json"),
     Description("Second hop of the link chain.")]
    public static string ChainB() => """{"type":"resource_link","uri":"chain://c","name":"chain_c"}""";

    [McpServerResource(UriTemplate = "chain://c", Name = "chain_c", MimeType = "application/json"),
     Description("Third hop of the link chain — points back to chain://a.")]
    public static string ChainC() => """{"type":"resource_link","uri":"chain://a","name":"chain_a"}""";

    // --- Resource templates (§3.4.1) -------------------------------------------------

    // Book extract (§3.4.1): the attribute declares the template; the SDK routes any
    // matching read to the method with {id} bound to the typed parameter.
    [McpServerResource(UriTemplate = "file://user/{id}/preferences.md",
        Name = "User Preferences")]
    public static ResourceContents UserPreferences(
        RequestContext<ReadResourceRequestParams> requestContext, int id)
    {
        // retrieve user preferences from your data source of choice
        return new TextResourceContents
        {
            Uri = requestContext.Params!.Uri!,
            MimeType = "text/markdown",
            Text = $"# Preferences for user {id}\n- theme: {(id % 2 == 0 ? "dark" : "light")}\n- sync: enabled\n",
        };
    }

    // An RFC 6570 LEVEL-4 template (explode modifier on a query expansion). 2.0.0
    // accepts and advertises the syntax, but routing rejects the repeated-key
    // expansion (?tags=a&tags=b -> -32602) and binds the value as a scalar
    // (?tags=a,b binds the string "a,b") — an array parameter here throws at bind
    // time. Partial expansions (?tags=a with limit omitted) route, but at GA an
    // omitted variable binds only if the parameter declares a DEFAULT VALUE —
    // nullability alone made it optional in preview.1; without `= null` GA throws
    // "Missing a value for the required parameter" (-32603). See the sample
    // READMEs for the full verdict on the SDK's URI-template support.
    [McpServerResource(UriTemplate = "docs://search{?tags*,limit}", Name = "doc_search"),
     Description("Searches the documentation catalog by tag.")]
    public static string SearchDocs(string? tags = null, int? limit = null)
    {
        return $"Search results for tags [{tags ?? "(none)"}] (limit {limit?.ToString() ?? "default"}): " +
               "user_guide.md, release_notes.md";
    }
}
