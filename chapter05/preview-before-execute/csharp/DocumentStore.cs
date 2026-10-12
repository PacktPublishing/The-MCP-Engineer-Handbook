using System.Collections.Immutable;

/// <summary>
/// An immutable snapshot of one document version. A write never changes a
/// Document; it replaces it in the store with a new one whose Version is one
/// higher. Lines and Version therefore always describe the same content, and
/// a preview records the Version it was computed against so a stale preview
/// is detectable.
/// </summary>
public sealed record Document(string Id, int Version, ImmutableArray<string> Lines);

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

    /// <summary>
    /// A consistent snapshot of every document, taken under the lock. The
    /// snapshot stays valid after the lock is released because documents are
    /// immutable — later writes replace them rather than editing them.
    /// </summary>
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
                var lines = doc.Lines.ToBuilder();
                foreach (var line in edit.Lines)
                    lines[line.LineNumber - 1] = line.After;
                _documents[edit.DocumentId] = doc with { Version = doc.Version + 1, Lines = lines.ToImmutable() };
            }

            return null;
        }
    }

    private static IEnumerable<Document> Seed() =>
    [
        new("onboarding-guide", 1,
        [
            "Welcome to the team! This guide covers your first week.",
            "Your first task is to install the Aurora CLI and sign in.",
            "Aurora sign-in issues go to the platform team.",
            "All services deploy through the standard pipeline to staging first.",
            "Your onboarding buddy will schedule a walkthrough on day two.",
        ]),
        new("release-checklist", 1,
        [
            "1. Confirm the changelog is complete and reviewed.",
            "2. Run the full Aurora test suite against staging.",
            "3. Tag the release and update the Aurora version manifest.",
            "4. Announce the release in #announcements.",
        ]),
        new("support-runbook", 1,
        [
            "Check the status page before triaging any report.",
            "Auth incidents: restart the token service first.",
            "Aurora ingestion lag above 5 minutes pages the on-call engineer.",
            "Escalate unresolved incidents after 30 minutes.",
        ]),
        new("style-guide", 1,
        [
            "Write short sentences in the active voice.",
            "Prefer numbered steps for procedures.",
            "Screenshots must include alt text.",
        ]),
        new("team-charter", 1,
        [
            "We ship small changes frequently.",
            "Every change is reviewed by at least one other engineer.",
            "Documentation updates ship with the change, not after.",
        ]),
    ];
}
