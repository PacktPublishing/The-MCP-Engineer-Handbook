using Microsoft.Extensions.AI;

// Host-side policy for the actions the host lets the model request. There is one,
// share_resource(uri, destination), and the host only shares with destinations on its
// allowlist. Nothing in the context changes that: not the resource's text, not the
// provenance tags, not how confident the model sounds. A real host advertises the
// action through ChatOptions.Tools and executes it only after Authorize allows it.
static class HostPolicy
{
    static readonly HashSet<string> AllowedShareHosts = ["notes.nimbus.example"];

    public static string Authorize(FunctionCallContent call)
    {
        if (call.Name != "share_resource")
            return $"DENIED {Describe(call)}: not an action this host offers.";

        object? destination = null;
        call.Arguments?.TryGetValue("destination", out destination);
        if (!Uri.TryCreate(destination?.ToString(), UriKind.Absolute, out var target) ||
            target.Scheme != Uri.UriSchemeHttps || !AllowedShareHosts.Contains(target.Host))
            return $"DENIED {Describe(call)}: destination is not on the share allowlist.";

        return $"ALLOWED {Describe(call)}";
    }

    public static string Describe(FunctionCallContent call) =>
        $"{call.Name}({string.Join(", ", (call.Arguments ?? new Dictionary<string, object?>()).Select(a => $"{a.Key}: {a.Value}"))})";
}
