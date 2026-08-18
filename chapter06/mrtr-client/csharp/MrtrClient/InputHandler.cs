// HandleElicitationAsync: the client host's side of an elicitation.
//
// Two concerns from Chapter 6, Section 6.2.5:
//   1. Form rendering — a schema-driven console form with labels, help text,
//      defaults, the accept/decline/cancel three-action model, and validation
//      of the user's input against the requested schema before responding.
//   2. Policy hooks — pre-user policies that can auto-answer or deny a
//      request without it ever reaching the user.
//
// Modes:
//   - Interactive (default): renders the form on the terminal.
//   - Scripted: MRTR_ANSWERS holds a JSON array of predetermined answers,
//     consumed in order — one per elicitation. An entry is either a content
//     object (meaning accept) or { "action": "decline" | "cancel" }.
//     Example: MRTR_ANSWERS='[{"title":"Sync","duration":"30"},{"confirm":true}]'
//
// MRTR_POLICY=autoconfirm enables the demo auto-answer policy.
//
// The C# SDK hands the handler a typed ElicitRequestParams (schema properties
// are PrimitiveSchemaDefinition subclasses, not raw JSON), and the SDK itself
// fills schema defaults into accepted results (ElicitResult.WithDefaults) —
// this handler still applies defaults so scripted/interactive content is
// complete before validation.

using ModelContextProtocol.Protocol;
using System.Text.Json;
using System.Text.RegularExpressions;

public static partial class InputHandler
{
    [GeneratedRegex("password|api[ -]?key|token|secret|credential", RegexOptions.IgnoreCase)]
    private static partial Regex Suspicious();

    private static readonly Queue<JsonElement>? ScriptedAnswers =
        Environment.GetEnvironmentVariable("MRTR_ANSWERS") is { } raw
            ? new Queue<JsonElement>(JsonSerializer.Deserialize<JsonElement[]>(raw)!)
            : null;

    public static ValueTask<ElicitResult> HandleElicitationAsync(ElicitRequestParams request)
    {
        if (request.Mode == "url")
        {
            // URL mode: display the full URL, never pre-fetch, never auto-navigate.
            // This console host only reports consent; a real host opens the system
            // browser after explicit user approval.
            Console.WriteLine($"  [url-mode] {request.Message}");
            Console.WriteLine($"  [url-mode] target: {request.Url} — open it in your browser, then continue.");
            return ValueTask.FromResult(new ElicitResult { Action = "accept" });
        }

        Console.WriteLine($"  elicitation: {request.Message}");

        // Policy hooks: run before the user ever sees the request.
        if (DenyCredentialSolicitation(request) is { } denied)
        {
            return ValueTask.FromResult(denied);
        }
        if (AutoConfirm(request) is { } confirmed)
        {
            return ValueTask.FromResult(confirmed);
        }

        if (ScriptedAnswers is not null)
        {
            return ValueTask.FromResult(NextScriptedAnswer(request));
        }
        return ValueTask.FromResult(RenderForm(request));
    }

    // -----------------------------------------------------------------------
    // Policies. A policy returns a full response to short-circuit the form,
    // or null to pass.
    // -----------------------------------------------------------------------

    /// <summary>Form mode must never collect credentials; deny requests that look like it.</summary>
    private static ElicitResult? DenyCredentialSolicitation(ElicitRequestParams request)
    {
        foreach (var (key, prop) in Properties(request))
        {
            var texts = $"{key} {prop.Title} {prop.Description}";
            var format = (prop as ElicitRequestParams.StringSchema)?.Format;
            if (format == "password" || Suspicious().IsMatch(texts))
            {
                Console.WriteLine($"  [policy] declined: field \"{key}\" appears to solicit credentials");
                return new ElicitResult { Action = "decline" };
            }
        }
        return null;
    }

    /// <summary>Demo auto-answer policy: accept pure-confirmation forms (all-boolean) with their defaults.</summary>
    private static ElicitResult? AutoConfirm(ElicitRequestParams request)
    {
        if (Environment.GetEnvironmentVariable("MRTR_POLICY") != "autoconfirm")
        {
            return null;
        }
        var props = Properties(request).ToList();
        if (props.Count == 0 || !props.All(p => p.Value is ElicitRequestParams.BooleanSchema))
        {
            return null;
        }
        var content = props.ToDictionary(
            p => p.Key,
            p => JsonSerializer.SerializeToElement(
                (p.Value as ElicitRequestParams.BooleanSchema)?.Default ?? true));
        Console.WriteLine($"  [policy] auto-answered confirmation form: {JsonSerializer.Serialize(content)}");
        return new ElicitResult { Action = "accept", Content = content };
    }

    // -----------------------------------------------------------------------
    // Validation: check content against the requested schema before responding —
    // a correctness concern (the server expects this shape) and a security one.
    // -----------------------------------------------------------------------

    private static string? ValidateField(string key, ElicitRequestParams.PrimitiveSchemaDefinition prop, JsonElement value)
        => prop switch
        {
            ElicitRequestParams.LegacyTitledEnumSchema e =>
                value.ValueKind == JsonValueKind.String && e.Enum.Contains(value.GetString()!)
                    ? null
                    : $"\"{key}\" must be one of: {string.Join(", ", e.Enum)}",
            ElicitRequestParams.TitledSingleSelectEnumSchema e =>
                value.ValueKind == JsonValueKind.String && e.OneOf.Any(o => o.Const == value.GetString())
                    ? null
                    : $"\"{key}\" must be one of: {string.Join(", ", e.OneOf.Select(o => o.Const))}",
            ElicitRequestParams.StringSchema s when value.ValueKind != JsonValueKind.String =>
                $"\"{key}\" must be a string",
            ElicitRequestParams.StringSchema s when s.MinLength is { } min && value.GetString()!.Length < min =>
                $"\"{key}\" must be at least {min} characters",
            ElicitRequestParams.StringSchema s when s.MaxLength is { } max && value.GetString()!.Length > max =>
                $"\"{key}\" must be at most {max} characters",
            ElicitRequestParams.StringSchema => null,
            ElicitRequestParams.NumberSchema n when value.ValueKind != JsonValueKind.Number =>
                $"\"{key}\" must be a number",
            ElicitRequestParams.NumberSchema n when n.Type == "integer" && !value.TryGetInt64(out _) =>
                $"\"{key}\" must be an integer",
            ElicitRequestParams.NumberSchema n when n.Minimum is { } min && value.GetDouble() < min =>
                $"\"{key}\" must be >= {min}",
            ElicitRequestParams.NumberSchema n when n.Maximum is { } max && value.GetDouble() > max =>
                $"\"{key}\" must be <= {max}",
            ElicitRequestParams.NumberSchema => null,
            ElicitRequestParams.BooleanSchema =>
                value.ValueKind is JsonValueKind.True or JsonValueKind.False
                    ? null
                    : $"\"{key}\" must be a boolean",
            _ => $"\"{key}\" has an unsupported schema type: {prop.Type}",
        };

    private static List<string> ValidateContent(ElicitRequestParams request, Dictionary<string, JsonElement> content)
    {
        var errors = new List<string>();
        var properties = request.RequestedSchema?.Properties
            ?? new Dictionary<string, ElicitRequestParams.PrimitiveSchemaDefinition>();
        foreach (var key in request.RequestedSchema?.Required ?? [])
        {
            if (!content.ContainsKey(key))
            {
                errors.Add($"\"{key}\" is required");
            }
        }
        foreach (var (key, value) in content)
        {
            if (!properties.TryGetValue(key, out var prop))
            {
                errors.Add($"\"{key}\" is not in the requested schema");
                continue;
            }
            if (ValidateField(key, prop, value) is { } error)
            {
                errors.Add(error);
            }
        }
        return errors;
    }

    /// <summary>The schema default of one property, if any (the SDK's own accessor is internal).</summary>
    private static JsonElement? DefaultOf(ElicitRequestParams.PrimitiveSchemaDefinition prop)
        => prop switch
        {
            ElicitRequestParams.StringSchema { Default: { } d } => JsonSerializer.SerializeToElement(d),
            ElicitRequestParams.NumberSchema { Default: { } d } => JsonSerializer.SerializeToElement(d),
            ElicitRequestParams.BooleanSchema { Default: { } d } => JsonSerializer.SerializeToElement(d),
            ElicitRequestParams.LegacyTitledEnumSchema { Default: { } d } => JsonSerializer.SerializeToElement(d),
            ElicitRequestParams.TitledSingleSelectEnumSchema { Default: { } d } => JsonSerializer.SerializeToElement(d),
            _ => null,
        };

    /// <summary>Pre-populate defaults, per the schema-driven rendering rules.</summary>
    private static Dictionary<string, JsonElement> DefaultsOf(ElicitRequestParams request)
    {
        var content = new Dictionary<string, JsonElement>();
        foreach (var (key, prop) in Properties(request))
        {
            if (DefaultOf(prop) is { } def)
            {
                content[key] = def;
            }
        }
        return content;
    }

    private static IEnumerable<KeyValuePair<string, ElicitRequestParams.PrimitiveSchemaDefinition>> Properties(
        ElicitRequestParams request)
        => request.RequestedSchema?.Properties
            ?? Enumerable.Empty<KeyValuePair<string, ElicitRequestParams.PrimitiveSchemaDefinition>>();

    // -----------------------------------------------------------------------
    // Scripted answers (for smoke tests / headless runs).
    // -----------------------------------------------------------------------

    private static ElicitResult NextScriptedAnswer(ElicitRequestParams request)
    {
        if (ScriptedAnswers!.Count == 0)
        {
            throw new InvalidOperationException("MRTR_ANSWERS ran out of scripted answers");
        }
        var entry = ScriptedAnswers.Dequeue();
        if (entry.TryGetProperty("action", out var action)
            && action.GetString() is ("decline" or "cancel") and { } a)
        {
            Console.WriteLine($"  [scripted] {a}");
            return new ElicitResult { Action = a };
        }
        var content = DefaultsOf(request);
        foreach (var field in entry.EnumerateObject())
        {
            content[field.Name] = field.Value;
        }
        var errors = ValidateContent(request, content);
        if (errors.Count > 0)
        {
            throw new InvalidOperationException($"Scripted answer failed validation: {string.Join("; ", errors)}");
        }
        Console.WriteLine($"  [scripted] accept: {JsonSerializer.Serialize(content)}");
        return new ElicitResult { Action = "accept", Content = content };
    }

    // -----------------------------------------------------------------------
    // Interactive form rendering.
    // -----------------------------------------------------------------------

    private static JsonElement? ParseInput(ElicitRequestParams.PrimitiveSchemaDefinition prop, string raw)
    {
        switch (prop)
        {
            case ElicitRequestParams.LegacyTitledEnumSchema:
            case ElicitRequestParams.TitledSingleSelectEnumSchema:
            case ElicitRequestParams.StringSchema:
                return JsonSerializer.SerializeToElement(raw);
            case ElicitRequestParams.NumberSchema:
                return double.TryParse(raw, out var n) ? JsonSerializer.SerializeToElement(n) : null;
            case ElicitRequestParams.BooleanSchema:
                if (raw is "y" or "yes" or "true" or "Y") return JsonSerializer.SerializeToElement(true);
                if (raw is "n" or "no" or "false" or "N") return JsonSerializer.SerializeToElement(false);
                return null;
            default:
                return null;
        }
    }

    private static ElicitResult RenderForm(ElicitRequestParams request)
    {
        Console.WriteLine($"\n{request.Message}");
        Console.WriteLine("(enter a value, or !decline / !cancel; blank accepts the default)");
        var content = new Dictionary<string, JsonElement>();
        var required = request.RequestedSchema?.Required ?? [];
        foreach (var (key, prop) in Properties(request))
        {
            var label = prop.Title ?? key; // title is the display label
            if (prop.Description is { } help)
            {
                Console.WriteLine($"  {help}"); // description is help text
            }
            var options = prop switch
            {
                ElicitRequestParams.LegacyTitledEnumSchema e => "options: " + string.Join("/", e.Enum),
                ElicitRequestParams.TitledSingleSelectEnumSchema e => "options: " + string.Join("/", e.OneOf.Select(o => o.Const)),
                _ => prop.Type,
            };
            var def = DefaultOf(prop);
            var hints = def is { } d ? $"{options}, default: {d}" : options;
            for (; ; )
            {
                Console.Write($"  {label} ({hints}): ");
                var raw = (Console.ReadLine() ?? "!cancel").Trim();
                if (raw == "!decline") return new ElicitResult { Action = "decline" };
                if (raw == "!cancel") return new ElicitResult { Action = "cancel" };
                if (raw.Length == 0)
                {
                    if (def is { } dv)
                    {
                        content[key] = dv;
                    }
                    else if (required.Contains(key))
                    {
                        Console.WriteLine($"  \"{label}\" is required.");
                        continue;
                    }
                    break;
                }
                var parsed = ParseInput(prop, raw);
                var error = parsed is { } p ? ValidateField(key, prop, p) : $"\"{label}\" is not a valid {prop.Type}";
                if (error is not null)
                {
                    Console.WriteLine($"  {error}");
                    continue;
                }
                content[key] = parsed!.Value;
                break;
            }
        }
        var errors = ValidateContent(request, content);
        if (errors.Count > 0)
        {
            // Should not happen after per-field checks, but never send invalid data.
            Console.WriteLine($"  Input failed validation: {string.Join("; ", errors)}");
            return new ElicitResult { Action = "cancel" };
        }
        return new ElicitResult { Action = "accept", Content = content };
    }
}
