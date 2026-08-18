/// <summary>
/// A document in the store. Version is bumped on every write; previews record
/// the version they were computed against so a stale preview is detectable.
/// </summary>
public sealed class Document
{
    public required string Id { get; init; }
    public required string[] Lines { get; init; }
    public int Version { get; set; } = 1;
}

/// <summary>
/// In-memory stand-in for the document service this MCP server wraps.
/// </summary>
public sealed class DocumentStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Document> _documents;

    public DocumentStore()
    {
        _documents = Seed().ToDictionary(d => d.Id);
    }

    public IReadOnlyList<Document> All()
    {
        lock (_gate)
        {
            return _documents.Values.OrderBy(d => d.Id, StringComparer.Ordinal).ToList();
        }
    }

    public Document? Get(string id)
    {
        lock (_gate)
        {
            return _documents.GetValueOrDefault(id);
        }
    }

    /// <summary>
    /// Applies a plan atomically. Returns null on success, or the id of the
    /// first document that changed since the plan was computed (stale plan —
    /// nothing is applied in that case).
    /// </summary>
    public string? TryApply(FindReplacePlan plan)
    {
        lock (_gate)
        {
            foreach (var edit in plan.Documents)
            {
                var doc = _documents.GetValueOrDefault(edit.DocumentId);
                if (doc is null || doc.Version != edit.DocumentVersion)
                    return edit.DocumentId;
            }

            foreach (var edit in plan.Documents)
            {
                var doc = _documents[edit.DocumentId];
                foreach (var line in edit.Lines)
                    doc.Lines[line.LineNumber - 1] = line.After;
                doc.Version++;
            }

            return null;
        }
    }

    private static IEnumerable<Document> Seed() =>
    [
        new()
        {
            Id = "onboarding-guide",
            Lines =
            [
                "Welcome to the team! This guide covers your first week.",
                "Your first task is to install the Aurora CLI and sign in.",
                "Aurora sign-in issues go to the platform team.",
                "All services deploy through the standard pipeline to staging first.",
                "Your onboarding buddy will schedule a walkthrough on day two.",
            ],
        },
        new()
        {
            Id = "release-checklist",
            Lines =
            [
                "1. Confirm the changelog is complete and reviewed.",
                "2. Run the full Aurora test suite against staging.",
                "3. Tag the release and update the Aurora version manifest.",
                "4. Announce the release in #announcements.",
            ],
        },
        new()
        {
            Id = "support-runbook",
            Lines =
            [
                "Check the status page before triaging any report.",
                "Auth incidents: restart the token service first.",
                "Aurora ingestion lag above 5 minutes pages the on-call engineer.",
                "Escalate unresolved incidents after 30 minutes.",
            ],
        },
        new()
        {
            Id = "style-guide",
            Lines =
            [
                "Write short sentences in the active voice.",
                "Prefer numbered steps for procedures.",
                "Screenshots must include alt text.",
            ],
        },
        new()
        {
            Id = "team-charter",
            Lines =
            [
                "We ship small changes frequently.",
                "Every change is reviewed by at least one other engineer.",
                "Documentation updates ship with the change, not after.",
            ],
        },
    ];
}
