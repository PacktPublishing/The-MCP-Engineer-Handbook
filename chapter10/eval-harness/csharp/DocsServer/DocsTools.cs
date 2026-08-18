using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Text.Json;

// The server under test: a small company-knowledge-base server. The corpus is a fixture —
// the "data seam" that rung 2 of the mocking spectrum (§10.1.4) says to design in early.
// It deliberately contains a trap: a deprecated refund FAQ that keyword search ranks above
// the current policy, so a careless caller produces a dud (§10.1.2) — a technically valid,
// confidently wrong answer that only an outcome check can catch.
[McpServerToolType]
public class DocsTools
{
    public sealed record Doc(string Id, string Title, string[] Tags, string Body);

    public static readonly IReadOnlyList<Doc> Corpus =
    [
        new("legacy-refund-faq", "Refund FAQ (2024)", ["billing", "deprecated"],
            "DEPRECATED — superseded by refund-policy. Refunds are available within 60 days " +
            "of purchase for all plans."),
        new("refund-policy", "Refund Policy", ["billing"],
            "Effective 2025-01-01: monthly plans may be refunded within 14 days of purchase; " +
            "annual plans within 30 days. Refunds are issued to the original payment method."),
        new("invoice-schedule", "Invoice Schedule", ["billing"],
            "Invoices are issued on the 1st of each month and are payable within 30 days. " +
            "Annual plans are invoiced once, at the start of the term."),
        new("shipping-policy", "Shipping Policy", ["fulfillment"],
            "Standard shipping takes 3-5 business days within the EU. Express shipping " +
            "(1-2 business days) is available at checkout for an additional fee."),
        new("api-rate-limits", "API Rate Limits", ["engineering"],
            "The public API allows 10,000 requests per hour per API key. Exceeding the limit " +
            "returns HTTP 429 with a Retry-After header."),
        new("onboarding-checklist", "Onboarding Checklist", ["internal"],
            "New customer onboarding: 1. provision the sandbox tenant, 2. schedule the " +
            "kickoff call, 3. import initial data, 4. enable SSO."),
    ];

    [McpServerTool(Name = "search_documents"),
     Description("Searches the company knowledge base. Returns matching documents as JSON: " +
                 "{\"matches\": [{\"id\", \"title\", \"snippet\"}]}. Use read_document to get full text.")]
    public static string SearchDocuments(
        [Description("Search terms, e.g. 'refund policy'.")] string query)
    {
        var terms = query.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var matches = Corpus
            .Where(d => terms.Any(t =>
                d.Title.Contains(t, StringComparison.OrdinalIgnoreCase) ||
                d.Body.Contains(t, StringComparison.OrdinalIgnoreCase) ||
                d.Tags.Any(tag => tag.Contains(t, StringComparison.OrdinalIgnoreCase))))
            .Select(d => new { id = d.Id, title = d.Title, snippet = Snippet(d.Body) });
        return JsonSerializer.Serialize(new { matches });
    }

    // Returns CallToolResult directly so unknown ids produce an isError tool result with a
    // message written for a model to recover from — recovery is one of the eval's criteria.
    [McpServerTool(Name = "read_document"),
     Description("Reads the full text of a document by id. Ids come from search_documents results.")]
    public static CallToolResult ReadDocument(
        [Description("Document id, e.g. 'refund-policy'.")] string id)
    {
        var doc = Corpus.FirstOrDefault(d => d.Id == id);
        if (doc is null)
        {
            return new CallToolResult
            {
                IsError = true,
                Content = [new TextContentBlock { Text =
                    $"No document with id '{id}'. Ids come from search_documents results — " +
                    "search for the topic first, then read the id it returns." }],
            };
        }
        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = JsonSerializer.Serialize(
                new { id = doc.Id, title = doc.Title, tags = doc.Tags, body = doc.Body }) }],
        };
    }

    [McpServerTool(Name = "count_documents"),
     Description("Counts knowledge-base documents carrying the given tag. Returns JSON: {\"tag\", \"count\"}.")]
    public static string CountDocuments(
        [Description("Tag to count, e.g. 'billing'.")] string tag)
        => JsonSerializer.Serialize(new
        {
            tag,
            count = Corpus.Count(d => d.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase)),
        });

    private static string Snippet(string body)
        => body.Length <= 100 ? body : body[..100] + "…";
}
