// Companion demo server for the MRTR client sample (Chapter 6, Section 6.2.5).
//
// `book_meeting` answers its first call with `input_required`, asking for
// meeting details (a form exercising titles, descriptions, and defaults), and
// answers the retry with a SECOND elicitation (a final confirmation) before
// completing — so the client's gather-and-retry loop genuinely iterates.
//
// `never_satisfied` asks forever; it exists so the client can demonstrate its
// retry budget tripping against a misbehaving server.
//
// C# SDK notes (ModelContextProtocol 2.0.0-preview.1):
//   - A tool handler produces an input_required result by THROWING
//     `InputRequiredException(inputRequests, requestState)`; there is no
//     result-value path like the TS `inputRequired({...})` helper. The
//     tools/call pipeline explicitly rethrows this exception past the
//     usual exception-to-isError conversion.
//   - Retried answers arrive on `context.Params.InputResponses`
//     (IDictionary<string, InputResponse>) and `context.Params.RequestState`.
//   - Against a client that did NOT negotiate 2026-07-28, the server does not
//     fail: on a stateful transport (stdio included) it resolves the embedded
//     requests itself via legacy server->client `elicitation/create` calls and
//     re-invokes this handler with the responses patched in — MRTR-native
//     tools work transparently on legacy sessions. `McpServer.IsMrtrSupported`
//     reports which path the connected client takes.
//   - `requestState` goes out exactly as the handler minted it — plain JSON
//     here so transcripts stay readable. It round-trips through the client as
//     attacker-controlled input: a production server must integrity-protect
//     it (the C# SDK has no built-in sealing; contrast the Python SDK's
//     default RequestStateBoundary).

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Text.Json;

var builder = Host.CreateApplicationBuilder(args);

// Stdio servers must not write logs to stdout — it would corrupt the JSON-RPC stream.
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithTools<BookingTools>();

await builder.Build().RunAsync();

[McpServerToolType]
public class BookingTools
{
    // The demo requestState, serialized as plain JSON.
    private record BookingState(string Step, string Room, Details? Details = null);

    private record Details(string Title, string Duration, bool? Notify);

    [McpServerTool(Name = "book_meeting"),
     Description("Books a meeting room. Elicits meeting details, then a final confirmation, before booking.")]
    public static string BookMeeting(
        McpServer server,
        RequestContext<CallToolRequestParams> context,
        [Description("Room to book.")] string room)
    {
        var state = ReadState(context.Params?.RequestState);
        var responses = context.Params?.InputResponses;

        // Round 1: no state yet — ask for the meeting details.
        if (state is null)
        {
            throw new InputRequiredException(
                inputRequests: new Dictionary<string, InputRequest>
                {
                    ["details"] = InputRequest.ForElicitation(new ElicitRequestParams
                    {
                        Message = $"Booking room {room}. What should the meeting look like?",
                        RequestedSchema = new ElicitRequestParams.RequestSchema
                        {
                            Properties = new Dictionary<string, ElicitRequestParams.PrimitiveSchemaDefinition>
                            {
                                ["title"] = new ElicitRequestParams.StringSchema
                                {
                                    Title = "Meeting title",
                                    Description = "Shown on the room display.",
                                    MinLength = 1,
                                    MaxLength = 80,
                                },
                                // The TS/Python demos emit the plain-`enum` form; the C# SDK
                                // deprecates that (SEP-1330), so this port uses the
                                // oneOf/const/title single-select form instead.
                                ["duration"] = new ElicitRequestParams.TitledSingleSelectEnumSchema
                                {
                                    Title = "Duration (minutes)",
                                    Description = "How long to hold the room.",
                                    OneOf =
                                    [
                                        new ElicitRequestParams.EnumSchemaOption { Const = "15", Title = "15 minutes" },
                                        new ElicitRequestParams.EnumSchemaOption { Const = "30", Title = "30 minutes" },
                                        new ElicitRequestParams.EnumSchemaOption { Const = "60", Title = "60 minutes" },
                                    ],
                                    Default = "30",
                                },
                                ["notify"] = new ElicitRequestParams.BooleanSchema
                                {
                                    Title = "Notify attendees",
                                    Description = "Send a calendar notification when booked.",
                                    Default = true,
                                },
                            },
                            Required = ["title", "duration"],
                        },
                    }),
                },
                requestState: JsonSerializer.Serialize(new BookingState("awaiting-details", room)));
        }

        // Round 2: the retry carrying the details form's answers.
        if (state.Step == "awaiting-details")
        {
            // Schema-aware read: validates the untrusted content before use.
            var details = AcceptedDetails(responses, "details");
            if (details is null)
            {
                return $"Room {state.Room} not booked (declined, missing, or invalid). Ask me again anytime.";
            }
            // Ask once more — a retry is allowed to come back input_required.
            throw new InputRequiredException(
                inputRequests: new Dictionary<string, InputRequest>
                {
                    ["confirm"] = InputRequest.ForElicitation(new ElicitRequestParams
                    {
                        Message = $"Book {state.Room} for \"{details.Title}\" ({details.Duration} min)?",
                        RequestedSchema = new ElicitRequestParams.RequestSchema
                        {
                            Properties = new Dictionary<string, ElicitRequestParams.PrimitiveSchemaDefinition>
                            {
                                ["confirm"] = new ElicitRequestParams.BooleanSchema
                                {
                                    Title = "Confirm booking",
                                    Description = "The room is charged to your team once booked.",
                                    Default = true,
                                },
                            },
                            Required = ["confirm"],
                        },
                    }),
                },
                requestState: JsonSerializer.Serialize(new BookingState("awaiting-confirm", state.Room, details)));
        }

        // Round 3: the retry carrying the confirmation.
        var confirmation = AcceptedContent(responses, "confirm");
        if (confirmation is null
            || !confirmation.TryGetValue("confirm", out var c)
            || c.ValueKind != JsonValueKind.True
            || state.Details is null)
        {
            return $"Room {state.Room} not booked: confirmation was withheld.";
        }
        var notify = state.Details.Notify ?? true;
        return $"Booked {state.Room} for \"{state.Details.Title}\" ({state.Details.Duration} min). "
            + (notify ? "Attendees notified." : "No notification sent.");
    }

    [McpServerTool(Name = "never_satisfied"),
     Description("Misbehaving tool: keeps requesting input forever. For retry-budget demos.")]
    public static string NeverSatisfied(RequestContext<CallToolRequestParams> context)
    {
        var round = (int.TryParse(context.Params?.RequestState, out var r) ? r : 0) + 1;
        throw new InputRequiredException(
            inputRequests: new Dictionary<string, InputRequest>
            {
                ["again"] = InputRequest.ForElicitation(new ElicitRequestParams
                {
                    Message = $"Still not satisfied (round {round}). Once more?",
                    RequestedSchema = new ElicitRequestParams.RequestSchema
                    {
                        Properties = new Dictionary<string, ElicitRequestParams.PrimitiveSchemaDefinition>
                        {
                            ["again"] = new ElicitRequestParams.BooleanSchema { Title = "Go again", Default = true },
                        },
                        Required = ["again"],
                    },
                }),
            },
            requestState: round.ToString());
    }

    private static BookingState? ReadState(string? raw)
    {
        if (raw is null)
        {
            return null;
        }
        try
        {
            return JsonSerializer.Deserialize<BookingState>(raw);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Accept-action content of one retried answer, or null.</summary>
    private static Dictionary<string, JsonElement>? AcceptedContent(
        IDictionary<string, InputResponse>? responses, string key)
    {
        if (responses is null || !responses.TryGetValue(key, out var response))
        {
            return null;
        }
        var elicit = response.Deserialize(InputResponse.ElicitResultJsonTypeInfo);
        if (elicit is null || !elicit.IsAccepted || elicit.Content is null)
        {
            return null;
        }
        return new Dictionary<string, JsonElement>(elicit.Content);
    }

    /// <summary>Validates the untrusted details form content before use.</summary>
    private static Details? AcceptedDetails(IDictionary<string, InputResponse>? responses, string key)
    {
        var content = AcceptedContent(responses, key);
        if (content is null
            || !content.TryGetValue("title", out var title) || title.ValueKind != JsonValueKind.String
            || title.GetString() is not { Length: >= 1 and <= 80 } titleText
            || !content.TryGetValue("duration", out var duration) || duration.ValueKind != JsonValueKind.String
            || duration.GetString() is not ("15" or "30" or "60"))
        {
            return null;
        }
        bool? notify = content.TryGetValue("notify", out var n)
            ? n.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => null,
            }
            : null;
        return new Details(titleText, duration.GetString()!, notify);
    }
}
