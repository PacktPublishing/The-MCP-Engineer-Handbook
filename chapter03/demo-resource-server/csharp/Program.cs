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
using System.Reflection;
using System.Text;
using System.Text.Json;

var builder = Host.CreateApplicationBuilder(args);

// Stdio servers must not write logs to stdout — it would corrupt the JSON-RPC stream.
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithTools<DemoTools>()
    .WithResources<DemoResources>()
    // The SDK keeps registered primitives in a hash-keyed dictionary, so its lists come
    // back in hash order: arbitrary for tools, and different on every run for resources
    // and templates. Restore declaration order (the order the Python and TypeScript
    // ports list them in) so clients see a stable catalog.
    .WithRequestFilters(filters =>
    {
        var resourceOrder = DeclarationOrder<DemoResources, McpServerResourceAttribute>(
            (attribute, _) => attribute.UriTemplate!);
        var toolOrder = DeclarationOrder<DemoTools, McpServerToolAttribute>(
            (attribute, method) => attribute.Name ?? JsonNamingPolicy.SnakeCaseLower.ConvertName(method.Name));

        filters.AddListResourcesFilter(next => async (request, cancellationToken) =>
        {
            var result = await next(request, cancellationToken);
            result.Resources = [.. result.Resources.OrderBy(r => resourceOrder.GetValueOrDefault(r.Uri, int.MaxValue))];
            return result;
        });
        filters.AddListResourceTemplatesFilter(next => async (request, cancellationToken) =>
        {
            var result = await next(request, cancellationToken);
            result.ResourceTemplates = [.. result.ResourceTemplates.OrderBy(t => resourceOrder.GetValueOrDefault(t.UriTemplate, int.MaxValue))];
            return result;
        });
        filters.AddListToolsFilter(next => async (request, cancellationToken) =>
        {
            var result = await next(request, cancellationToken);
            result.Tools = [.. result.Tools.OrderBy(t => toolOrder.GetValueOrDefault(t.Name, int.MaxValue))];
            return result;
        });
    });

await builder.Build().RunAsync();

// Maps each attributed method's key to its position in the source file. Metadata tokens
// follow declaration order, unlike GetMethods(), whose order is unspecified.
static Dictionary<string, int> DeclarationOrder<TType, TAttribute>(Func<TAttribute, MethodInfo, string> key)
    where TAttribute : Attribute =>
    typeof(TType)
        .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
        .Where(method => method.GetCustomAttribute<TAttribute>() is not null)
        .OrderBy(method => method.MetadataToken)
        .Select((method, index) => (Key: key(method.GetCustomAttribute<TAttribute>()!, method), index))
        .ToDictionary(entry => entry.Key, entry => entry.index);

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

    // Two more for the resolver's per-pass limits. They are templates rather than fixed
    // resources so they stay out of resources/list, which the other chapter 3 samples
    // show to the model.

    public const int AttachmentSize = 6_000;

    // ONE read, several content items: each attachment fits a 16 KiB per-resource
    // budget on its own, but the three together don't.
    [McpServerResource(UriTemplate = "file://notes/{id}/attachments", Name = "note_attachments",
        MimeType = "text/markdown"),
     Description("Every attachment of a note, one content item each (3 x 6 000 bytes).")]
    public static IEnumerable<ResourceContents> NoteAttachments(
        RequestContext<ReadResourceRequestParams> requestContext, int id) =>
        Enumerable.Range(1, 3).Select(n => new TextResourceContents
        {
            Uri = requestContext.Params!.Uri!,
            MimeType = "text/markdown",
            Text = ($"# Attachment {n} of note {id}\n\n" +
                    string.Concat(Enumerable.Repeat("Transcript excerpt. ", 300)))[..AttachmentSize],
        });

    // A slow read: the export is "generated on demand" and takes three seconds — longer
    // than the deadline the resource-link-client's last pass allows.
    [McpServerResource(UriTemplate = "file://exports/{quarter}/telemetry.csv", Name = "telemetry_export",
        MimeType = "text/csv"),
     Description("A telemetry export generated on demand. Deliberately slow (3 s) to exercise client deadlines.")]
    public static async Task<string> TelemetryExport(string quarter, CancellationToken cancellationToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
        return $"quarter,active_users\n{quarter},1234\n";
    }

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

    // An RFC 6570 LEVEL-4 template (explode modifier on a query expansion), kept as a
    // record of what the SDK does with it. 2.0.0 accepts and advertises the syntax but
    // matches it as a plain query expansion: the repeated-key form ?tags=a&tags=b is
    // rejected (-32602) and ?tags=a,b binds the scalar string "a,b". Omitted variables
    // bind only because both parameters declare a DEFAULT VALUE — nullability alone
    // is not enough at GA. The result echoes the bindings as JSON so the README's
    // compatibility table can be reproduced; see the README for the observed matrix.
    [McpServerResource(UriTemplate = "docs://search{?tags*,limit}", Name = "doc_search"),
     Description("Searches the documentation catalog by tag.")]
    public static string SearchDocs(string? tags = null, int? limit = null)
    {
        return $"Search results (bound tags={JsonSerializer.Serialize(tags)}, limit={JsonSerializer.Serialize(limit)}): " +
               "user_guide.md, release_notes.md";
    }
}
