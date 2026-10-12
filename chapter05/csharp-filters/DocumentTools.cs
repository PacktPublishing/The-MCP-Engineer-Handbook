using System.Collections.Concurrent;
using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
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
            : throw new McpException($"No document with ID '{id}'.");

    // Replacing an existing document asks the user first, via an MRTR form
    // elicitation (Section 5.3.1). The InputRequiredException has to pass
    // through every filter in Program.cs untouched for the request to reach
    // the client.
    [McpServerTool(Name = "create_document"), Description("Creates a document, or replaces one after the user confirms.")]
    public static string CreateDocument(
        RequestContext<CallToolRequestParams> context,
        [Description("Document ID.")] string id,
        [Description("Document content.")] string content)
    {
        if (Documents.ContainsKey(id))
        {
            if (context.Params?.InputResponses?.TryGetValue("confirm_overwrite", out var response) is not true)
            {
                throw new InputRequiredException(new Dictionary<string, InputRequest>
                {
                    ["confirm_overwrite"] = InputRequest.ForElicitation(new ElicitRequestParams
                    {
                        Message = $"Document '{id}' already exists. Replace it?",
                        RequestedSchema = new ElicitRequestParams.RequestSchema
                        {
                            Properties = new Dictionary<string, ElicitRequestParams.PrimitiveSchemaDefinition>
                            {
                                ["replace"] = new ElicitRequestParams.BooleanSchema { Title = "Replace" },
                            },
                            Required = ["replace"],
                        },
                    }),
                });
            }

            var answer = response.Deserialize(InputResponse.ElicitResultJsonTypeInfo);
            if (answer is not { IsAccepted: true, Content: { } fields }
                || !fields.TryGetValue("replace", out var replace)
                || replace.ValueKind != JsonValueKind.True)
            {
                return $"Kept the existing document '{id}'.";
            }
        }

        Documents[id] = content;
        return $"Stored document '{id}' ({content.Length} chars).";
    }

    // The authorization filter in Program.cs blocks this tool before it runs.
    [McpServerTool(Name = "admin_delete_document"), Description("Deletes a document by ID. Admin only.")]
    public static string AdminDeleteDocument([Description("Document ID.")] string id)
        => Documents.TryRemove(id, out _)
            ? $"Deleted document '{id}'."
            : throw new McpException($"No document with ID '{id}'.");
}
