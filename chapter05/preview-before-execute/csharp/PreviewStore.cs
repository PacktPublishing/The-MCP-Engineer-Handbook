using System.Collections.Concurrent;
using System.Security.Cryptography;

public enum RedeemResult
{
    NotFound,
    Expired,
    Valid,
}

/// <summary>
/// Stores previewed plans keyed by token — the explicit handle pattern from
/// Chapter 4 applied to preview-before-execute. This sample keeps the
/// dictionary in process memory; in production it belongs in the durable
/// storage the server already uses (a database, Redis, ...) so that
/// execute_find_and_replace can land on any server instance. The token, not
/// the connection, carries the state. A production token would also bind the
/// authenticated user, so a preview cannot be redeemed by someone else.
/// </summary>
public sealed class PreviewStore
{
    public static readonly TimeSpan TimeToLive = TimeSpan.FromMinutes(5);

    private sealed record StoredPreview(FindReplacePlan Plan, DateTimeOffset ExpiresAt);

    private readonly ConcurrentDictionary<string, StoredPreview> _previews = new();

    public string Add(FindReplacePlan plan)
    {
        var token = RandomNumberGenerator.GetHexString(12, lowercase: true);
        _previews[token] = new StoredPreview(plan, DateTimeOffset.UtcNow + TimeToLive);
        return token;
    }

    /// <summary>
    /// Tokens are single-use: redeeming removes the entry whether or not the
    /// subsequent execution succeeds. A failed execution means the state
    /// changed, so a fresh preview is needed anyway.
    /// </summary>
    public RedeemResult Redeem(string token, out FindReplacePlan? plan)
    {
        plan = null;
        if (!_previews.TryRemove(token, out var stored))
            return RedeemResult.NotFound;
        if (stored.ExpiresAt < DateTimeOffset.UtcNow)
            return RedeemResult.Expired;
        plan = stored.Plan;
        return RedeemResult.Valid;
    }
}
