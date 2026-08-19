// Completions server (section 5.3.3).
//
// In the C# SDK, completions are a server-level handler: WithCompleteHandler
// declares the `completions` capability and answers `completion/complete`.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

var builder = Host.CreateApplicationBuilder(args);

// Stdio servers must not write logs to stdout — route console logging to stderr.
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithResources<ProjectFileResources>()
    .WithCompleteHandler(async (request, cancellationToken) =>
    {
        // Only handle our file resource template
        if (request.Params?.Ref is ResourceTemplateReference { Uri: "file:///{path}" } &&
            request.Params.Argument is { Name: "path" } argument)
        {
            var prefix = argument.Value;
            // Filter paths matching the partial input
            var matches = ProjectFileResources.KnownPaths
                .Where(p => p.StartsWith(prefix, StringComparison.Ordinal))
                .ToList();
            // Spec allows max 100 values
            var capped = matches.Take(100).ToList();
            return new CompleteResult
            {
                Completion = new Completion
                {
                    Values = capped,
                    Total = matches.Count,
                    HasMore = matches.Count > capped.Count,
                },
            };
        }

        // Anything else -> empty completion
        return new CompleteResult();
    });

await builder.Build().RunAsync();

[McpServerResourceType]
public class ProjectFileResources
{
    // The catalog the completion handler completes against. A real server would
    // consult its actual resource space (and should fuzzy-match, rate-limit, and
    // keep sensitive paths out — see the chapter's guidance).
    public static readonly string[] KnownPaths =
    [
        "docs/readme.md",
        "docs/reference.md",
        "docs/release-notes.md",
        "docs/setup.md",
        "src/main.py",
        "src/utils.py",
        "tests/test_main.py",
    ];

    [McpServerResource(UriTemplate = "file:///{path}", Name = "project-file")]
    public static string ReadFile(string path) =>
        KnownPaths.Contains(path)
            ? $"Contents of {path}"
            : throw new ArgumentException($"unknown path: {path}");
}
