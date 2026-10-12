using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using System.ComponentModel;

/// <summary>
/// Find-and-replace as a logical operation, with preview-before-execute in
/// both of the chapter's variants: a dry_run parameter on the combined tool,
/// and a separate preview tool whose token gates execution. Failures are
/// isError results whose text states what did not happen and what to do
/// instead — positive instructions the model can act on, and a flag a
/// program can check without parsing English.
/// </summary>
[McpServerToolType]
public sealed class FindReplaceTools(DocumentStore documents, PreviewStore previews)
{
    private static CallToolResult Text(string s) =>
        new() { Content = [new TextContentBlock { Text = s }] };

    private static CallToolResult ErrorText(string s) =>
        new() { Content = [new TextContentBlock { Text = s }], IsError = true };

    // --- Variant 1: one tool with a dry_run parameter -----------------------

    [McpServerTool(Name = "find_and_replace"), Description(
        "Replaces every occurrence of 'find' with 'replace' across all documents, applied as one atomic edit batch. " +
        "This is a write operation that modifies documents. " +
        "Set dry_run to true to see which lines would change and which internal operations would run, without changing anything. " +
        "A dry run is advisory: a later call with dry_run false recomputes the changes against the documents as they are then.")]
    public CallToolResult FindAndReplace(
        [Description("Exact text to search for (case-sensitive).")] string find,
        [Description("Text to replace each occurrence with.")] string replace,
        [Description("If true, make no changes; return the would-be changes and the internal operations instead.")] bool dry_run = false)
    {
        if (find.Length == 0)
            return ErrorText("No changes were made: 'find' must be non-empty. Provide the exact text to search for.");

        var plan = FindReplacePlanner.Build(documents, find, replace);
        if (plan.Documents.Count == 0)
            return Text($"No occurrences of \"{find}\" found in any document, so there is nothing to replace. " +
                        "No changes were made. Call list_documents and read_document to inspect the current content.");

        if (dry_run)
            return Text("Dry run — no changes were made.\n\n" + FindReplacePlanner.RenderPreview(plan) +
                        "\nTo apply these changes, call find_and_replace again with the same find and replace and dry_run: false. " +
                        "That call recomputes the changes, so if the documents change in the meantime it applies whatever matches then; " +
                        "use preview_find_and_replace instead to bind execution to this exact plan.");

        var staleDocument = documents.TryApply(plan);
        if (staleDocument is not null)
            return ErrorText($"No changes were made: document \"{staleDocument}\" changed while the operation was being prepared. Retry the call.");

        return Text(FindReplacePlanner.RenderExecuted(plan));
    }

    // --- Variant 2: separate preview and execute tools, linked by a token ---

    [McpServerTool(Name = "preview_find_and_replace"), Description(
        "Previews a find-and-replace across all documents: which lines would change and which internal operations would run. " +
        "Makes no changes. Returns a preview_token — passing it to execute_find_and_replace is the only way to apply the changes.")]
    public CallToolResult PreviewFindAndReplace(
        [Description("Exact text to search for (case-sensitive).")] string find,
        [Description("Text to replace each occurrence with.")] string replace)
    {
        if (find.Length == 0)
            return ErrorText("No preview was created: 'find' must be non-empty. Provide the exact text to search for.");

        var plan = FindReplacePlanner.Build(documents, find, replace);
        if (plan.Documents.Count == 0)
            return Text($"No occurrences of \"{find}\" found in any document, so there is nothing to replace and no preview token was issued. " +
                        "Call list_documents and read_document to inspect the current content.");

        var token = previews.Add(plan);
        return Text($"Preview {token} created — no changes have been made yet.\n\n" + FindReplacePlanner.RenderPreview(plan) +
                    $"\nTo apply these changes, call execute_find_and_replace with preview_token \"{token}\". " +
                    $"The token is single-use and expires in {(int)PreviewStore.TimeToLive.TotalMinutes} minutes.");
    }

    [McpServerTool(Name = "execute_find_and_replace"), Description(
        "Applies the changes described by a preview created with preview_find_and_replace. " +
        "Requires that preview's preview_token; call preview_find_and_replace first to review the changes and obtain the token.")]
    public CallToolResult ExecuteFindAndReplace(
        // Optional in the schema on purpose: a call without the token gets an
        // instructive result steering the model to the preview tool, instead
        // of a generic missing-argument error.
        [Description("Token returned by preview_find_and_replace for the plan to apply.")] string? preview_token = null)
    {
        if (string.IsNullOrWhiteSpace(preview_token))
            return ErrorText("No changes were made. execute_find_and_replace applies a previously previewed plan and needs its preview_token. " +
                             "Call preview_find_and_replace with your find and replace text first — it shows exactly what will change and returns the token to pass here.");

        switch (previews.Redeem(preview_token, out var plan))
        {
            case RedeemResult.NotFound:
                return ErrorText($"No changes were made. Preview token \"{preview_token}\" was not found — tokens are single-use and expire after " +
                                 $"{(int)PreviewStore.TimeToLive.TotalMinutes} minutes. Call preview_find_and_replace to create a fresh preview and token.");
            case RedeemResult.Expired:
                return ErrorText($"No changes were made. Preview \"{preview_token}\" has expired (previews are valid for " +
                                 $"{(int)PreviewStore.TimeToLive.TotalMinutes} minutes). Call preview_find_and_replace again to re-review the changes and get a fresh token.");
        }

        var staleDocument = documents.TryApply(plan!);
        if (staleDocument is not null)
            return ErrorText($"No changes were made. Document \"{staleDocument}\" was modified after preview \"{preview_token}\" was created, " +
                             "so the previewed plan no longer matches the current content. " +
                             "Call preview_find_and_replace again to review the changes against the current documents.");

        return Text($"Preview {preview_token} executed. " + FindReplacePlanner.RenderExecuted(plan!));
    }

    // --- Read-only helpers so the corpus can be inspected -------------------

    [McpServerTool(Name = "list_documents"), Description("Lists all documents in the store. Read-only.")]
    public string ListDocuments()
        => string.Join("\n", documents.All().Select(d => $"{d.Id} ({d.Lines.Length} lines)"));

    [McpServerTool(Name = "read_document"), Description("Returns the full text of one document. Read-only.")]
    public CallToolResult ReadDocument(
        [Description("Document id, e.g. \"onboarding-guide\".")] string document_id)
    {
        var doc = documents.Get(document_id);
        return doc is null
            ? ErrorText($"Document \"{document_id}\" was not found. Call list_documents to see the available ids.")
            : Text(string.Join("\n", doc.Lines));
    }
}
