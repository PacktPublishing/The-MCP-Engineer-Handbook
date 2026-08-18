using System.Collections.Concurrent;
using System.ComponentModel;
using ModelContextProtocol.Server;

[McpServerToolType]
public class DocumentTools
{
    private static readonly ConcurrentDictionary<string, string> Documents = new()
    {
        ["readme"] = "Welcome to the document store. Start here.",
        ["roadmap"] = "Q3: filters. Q4: everything else.",
        ["postmortem-042"] = "Root cause: a retry loop with no backoff.",
    };

    [McpServerTool(Name = "list_documents"), Description("Lists the IDs of all documents in the store.")]
    public static string ListDocuments()
        => Documents.IsEmpty ? "(no documents)" : string.Join("\n", Documents.Keys.Order());

    [McpServerTool(Name = "get_document"), Description("Returns the content of a document by ID.")]
    public static string GetDocument([Description("Document ID.")] string id)
        => Documents.TryGetValue(id, out var content)
            ? content
            : throw new KeyNotFoundException($"No document with ID '{id}'.");

    [McpServerTool(Name = "create_document"), Description("Creates or replaces a document.")]
    public static string CreateDocument(
        [Description("Document ID.")] string id,
        [Description("Document content.")] string content)
    {
        Documents[id] = content;
        return $"Stored document '{id}' ({content.Length} chars).";
    }

    // The authorization filter in Program.cs blocks this tool before it runs.
    [McpServerTool(Name = "admin_delete_document"), Description("Deletes a document by ID. Admin only.")]
    public static string AdminDeleteDocument([Description("Document ID.")] string id)
        => Documents.TryRemove(id, out _)
            ? $"Deleted document '{id}'."
            : throw new KeyNotFoundException($"No document with ID '{id}'.");
}
