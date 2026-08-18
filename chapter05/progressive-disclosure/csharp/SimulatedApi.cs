// Simulated document-management API backing execute_endpoint.
// A small in-memory store stands in for the real service; each endpoint
// handler returns plausible JSON or throws an ApiError whose message is
// written to guide the model toward a correct retry.

using System.Text.Json.Nodes;

public sealed class ApiError(string message) : Exception(message);

// Parsed query-string pairs; Get returns the first value, like URLSearchParams.
public sealed class ApiQuery
{
    private readonly List<KeyValuePair<string, string>> _pairs = [];

    public void AppendFrom(string queryString)
    {
        foreach (var part in queryString.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=');
            var key = eq < 0 ? part : part[..eq];
            var value = eq < 0 ? "" : part[(eq + 1)..];
            _pairs.Add(new(Decode(key), Decode(value)));
        }
        static string Decode(string s) => Uri.UnescapeDataString(s.Replace('+', ' '));
    }

    public string? Get(string name) =>
        _pairs.Where(p => p.Key == name).Select(p => (string?)p.Value).FirstOrDefault();

    public int GetInt(string name, int fallback) =>
        int.TryParse(Get(name), out var value) ? value : fallback;
}

public static class SimulatedApi
{
    // The simulation has no authentication; all calls act as this user.
    private const string ActingUser = "user-001";

    private sealed class User
    {
        public required string Id { get; set; }
        public required string Name { get; set; }
        public required string Email { get; set; }
    }

    private sealed class Group
    {
        public required string Id { get; set; }
        public required string Name { get; set; }
        public required List<string> MemberIds { get; set; }
    }

    private sealed class Doc
    {
        public required string Id { get; set; }
        public required string Title { get; set; }
        public required string OwnerId { get; set; }
        public required List<string> Tags { get; set; }
        public required string CreatedAt { get; set; }
        public required string UpdatedAt { get; set; }
        public required string CurrentVersion { get; set; }
    }

    private sealed class Version
    {
        public required string DocumentId { get; set; }
        public required string VersionId { get; set; }
        public required string AuthorId { get; set; }
        public required string CreatedAt { get; set; }
        public required string Note { get; set; }
        public required string Content { get; set; }
    }

    private sealed class Grant
    {
        public required string GrantId { get; set; }
        public required string DocumentId { get; set; }
        public required string GranteeType { get; set; }
        public required string GranteeId { get; set; }
        public required string Level { get; set; }
    }

    private static readonly List<User> Users =
    [
        new() { Id = "user-001", Name = "Alice Chen", Email = "alice.chen@example.com" },
        new() { Id = "user-002", Name = "Bob Martinez", Email = "bob.martinez@example.com" },
        new() { Id = "user-003", Name = "Carol Okafor", Email = "carol.okafor@example.com" },
        new() { Id = "user-004", Name = "Dana Kim", Email = "dana.kim@example.com" },
    ];

    private static readonly List<Group> Groups =
    [
        new() { Id = "grp-001", Name = "Leadership", MemberIds = ["user-001", "user-002"] },
        new() { Id = "grp-002", Name = "Finance", MemberIds = ["user-003", "user-004"] },
    ];

    private static readonly List<Doc> Docs =
    [
        new()
        {
            Id = "doc-001",
            Title = "Quarterly Report Q1 2026",
            OwnerId = "user-001",
            Tags = ["finance", "quarterly"],
            CreatedAt = "2026-01-05T09:00:00Z",
            UpdatedAt = "2026-04-02T14:30:00Z",
            CurrentVersion = "v3",
        },
        new()
        {
            Id = "doc-002",
            Title = "Employee Handbook",
            OwnerId = "user-003",
            Tags = ["hr", "policy"],
            CreatedAt = "2025-08-12T10:00:00Z",
            UpdatedAt = "2025-08-12T10:00:00Z",
            CurrentVersion = "v1",
        },
        new()
        {
            Id = "doc-003",
            Title = "Product Roadmap 2026",
            OwnerId = "user-002",
            Tags = ["product", "planning"],
            CreatedAt = "2025-11-20T08:15:00Z",
            UpdatedAt = "2026-02-01T11:00:00Z",
            CurrentVersion = "v2",
        },
    ];

    private static readonly List<Version> Versions =
    [
        new()
        {
            DocumentId = "doc-001",
            VersionId = "v1",
            AuthorId = "user-001",
            CreatedAt = "2026-01-05T09:00:00Z",
            Note = "Initial draft",
            Content = "Quarterly Report Q1 2026 — draft outline. Revenue and expense sections pending.",
        },
        new()
        {
            DocumentId = "doc-001",
            VersionId = "v2",
            AuthorId = "user-002",
            CreatedAt = "2026-02-10T16:45:00Z",
            Note = "Added revenue figures",
            Content = "Quarterly Report Q1 2026. Revenue grew 12% quarter over quarter, driven by enterprise renewals. Expense section pending.",
        },
        new()
        {
            DocumentId = "doc-001",
            VersionId = "v3",
            AuthorId = "user-001",
            CreatedAt = "2026-04-02T14:30:00Z",
            Note = "Final: expenses and outlook",
            Content = "Quarterly Report Q1 2026. Revenue grew 12% quarter over quarter, driven by enterprise renewals. Operating expenses held flat. Outlook for Q2 remains positive.",
        },
        new()
        {
            DocumentId = "doc-002",
            VersionId = "v1",
            AuthorId = "user-003",
            CreatedAt = "2025-08-12T10:00:00Z",
            Note = "Initial publication",
            Content = "Employee Handbook. Covers onboarding, leave policy, and code of conduct.",
        },
        new()
        {
            DocumentId = "doc-003",
            VersionId = "v1",
            AuthorId = "user-002",
            CreatedAt = "2025-11-20T08:15:00Z",
            Note = "Initial roadmap",
            Content = "Product Roadmap 2026. H1: platform consolidation. H2: TBD.",
        },
        new()
        {
            DocumentId = "doc-003",
            VersionId = "v2",
            AuthorId = "user-004",
            CreatedAt = "2026-02-01T11:00:00Z",
            Note = "H2 themes added",
            Content = "Product Roadmap 2026. H1: platform consolidation. H2: analytics and insights suite.",
        },
    ];

    private static readonly List<Grant> Grants =
    [
        new() { GrantId = "grant-001", DocumentId = "doc-001", GranteeType = "user", GranteeId = "user-001", Level = "admin" },
        new() { GrantId = "grant-002", DocumentId = "doc-001", GranteeType = "group", GranteeId = "grp-002", Level = "read" },
        new() { GrantId = "grant-003", DocumentId = "doc-001", GranteeType = "group", GranteeId = "grp-001", Level = "write" },
        new() { GrantId = "grant-004", DocumentId = "doc-002", GranteeType = "user", GranteeId = "user-003", Level = "admin" },
        new() { GrantId = "grant-005", DocumentId = "doc-002", GranteeType = "group", GranteeId = "grp-001", Level = "read" },
        new() { GrantId = "grant-006", DocumentId = "doc-003", GranteeType = "user", GranteeId = "user-002", Level = "admin" },
    ];

    private static int _nextDocNum = 4;
    private static int _nextGrantNum = 7;

    private static string NowIso() => DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

    private static Doc GetDoc(string id) =>
        Docs.FirstOrDefault(d => d.Id == id)
        ?? throw new ApiError(
            $"No document with id \"{id}\". Use GET /api/documents to list documents, " +
            "or GET /api/documents/search to find one by title or content.");

    private static User GetUser(string id) =>
        Users.FirstOrDefault(u => u.Id == id)
        ?? throw new ApiError($"No user with id \"{id}\". Use GET /api/users to list users.");

    private static Group GetGroup(string id) =>
        Groups.FirstOrDefault(g => g.Id == id)
        ?? throw new ApiError($"No group with id \"{id}\". Use GET /api/groups to list groups.");

    private static JsonObject RequireBody(JsonNode? body, string endpoint) =>
        body as JsonObject
        ?? throw new ApiError(
            $"{endpoint} requires a JSON object request body. " +
            "Use describe_endpoint to see the request schema.");

    private static readonly Dictionary<string, int> LevelRank = new() { ["read"] = 1, ["write"] = 2, ["admin"] = 3 };

    private static bool IsString(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out _);

    // JS String(x) for body fields: strings pass through, other scalars stringify.
    private static string JsToString(JsonNode? node) =>
        node is null ? "null" : IsString(node) ? node.GetValue<string>() : node.ToString();

    private static object DocMeta(Doc doc) =>
        new { doc.Id, doc.Title, doc.OwnerId, doc.CreatedAt, doc.UpdatedAt };

    // Handlers are keyed by "<METHOD> <path template>"; params holds the values
    // captured from the {placeholders} in the template.
    public static readonly Dictionary<string, Func<Dictionary<string, string>, ApiQuery, JsonNode?, object>> Handlers = new()
    {
        ["GET /api/documents"] = (_, query, _) =>
        {
            IEnumerable<Doc> result = Docs.OrderByDescending(d => d.UpdatedAt, StringComparer.Ordinal);
            var ownerId = query.Get("ownerId");
            if (!string.IsNullOrEmpty(ownerId)) result = result.Where(d => d.OwnerId == ownerId);
            var filtered = result.ToList();
            var offset = query.GetInt("offset", 0);
            var limit = query.GetInt("limit", 20);
            return new
            {
                Documents = filtered.Skip(offset).Take(limit).Select(DocMeta).ToList(),
                Total = filtered.Count,
            };
        },

        ["POST /api/documents"] = (_, _, rawBody) =>
        {
            var body = RequireBody(rawBody, "POST /api/documents");
            if (!IsString(body["title"]) || !IsString(body["content"]))
            {
                throw new ApiError(
                    "POST /api/documents requires a body with string fields \"title\" and \"content\" " +
                    "(and optionally \"tags\", an array of strings).");
            }
            var id = $"doc-{_nextDocNum++:000}";
            var createdAt = NowIso();
            var doc = new Doc
            {
                Id = id,
                Title = body["title"]!.GetValue<string>(),
                OwnerId = ActingUser,
                Tags = body["tags"] is JsonArray tags ? tags.Select(JsToString).ToList() : [],
                CreatedAt = createdAt,
                UpdatedAt = createdAt,
                CurrentVersion = "v1",
            };
            Docs.Add(doc);
            Versions.Add(new Version
            {
                DocumentId = id,
                VersionId = "v1",
                AuthorId = ActingUser,
                CreatedAt = createdAt,
                Note = "Initial version",
                Content = body["content"]!.GetValue<string>(),
            });
            Grants.Add(new Grant
            {
                GrantId = $"grant-{_nextGrantNum++:000}",
                DocumentId = id,
                GranteeType = "user",
                GranteeId = ActingUser,
                Level = "admin",
            });
            return new { Id = id, doc.Title, doc.OwnerId, CreatedAt = createdAt, CurrentVersion = "v1" };
        },

        ["GET /api/documents/{id}"] = (parameters, _, _) =>
        {
            var doc = GetDoc(parameters["id"]);
            var head = Versions.FirstOrDefault(v => v.DocumentId == doc.Id && v.VersionId == doc.CurrentVersion);
            return new
            {
                doc.Id, doc.Title, doc.OwnerId, doc.Tags, doc.CreatedAt, doc.UpdatedAt, doc.CurrentVersion,
                Content = head?.Content ?? "",
            };
        },

        ["PATCH /api/documents/{id}"] = (parameters, _, rawBody) =>
        {
            var doc = GetDoc(parameters["id"]);
            var body = RequireBody(rawBody, "PATCH /api/documents/{id}");
            if (!body.ContainsKey("title") && !body.ContainsKey("content") && !body.ContainsKey("tags"))
            {
                throw new ApiError(
                    "PATCH /api/documents/{id} requires at least one of \"title\", \"content\", or \"tags\" in the body.");
            }
            if (body.ContainsKey("title")) doc.Title = JsToString(body["title"]);
            if (body["tags"] is JsonArray tags) doc.Tags = tags.Select(JsToString).ToList();
            if (body.ContainsKey("content"))
            {
                var versionId = $"v{Versions.Count(v => v.DocumentId == doc.Id) + 1}";
                Versions.Add(new Version
                {
                    DocumentId = doc.Id,
                    VersionId = versionId,
                    AuthorId = ActingUser,
                    CreatedAt = NowIso(),
                    Note = "Content update",
                    Content = JsToString(body["content"]),
                });
                doc.CurrentVersion = versionId;
            }
            doc.UpdatedAt = NowIso();
            return new { doc.Id, doc.Title, doc.UpdatedAt, doc.CurrentVersion };
        },

        ["DELETE /api/documents/{id}"] = (parameters, _, _) =>
        {
            var doc = GetDoc(parameters["id"]);
            Docs.Remove(doc);
            Versions.RemoveAll(v => v.DocumentId == doc.Id);
            Grants.RemoveAll(g => g.DocumentId == doc.Id);
            return new { Deleted = doc.Id };
        },

        ["GET /api/documents/search"] = (_, query, _) =>
        {
            var q = query.Get("q");
            if (string.IsNullOrEmpty(q))
            {
                throw new ApiError(
                    "Missing required query parameter \"q\". " +
                    "Example: execute_endpoint with path \"/api/documents/search\" and query \"q=quarterly report\".");
            }
            var limit = query.GetInt("limit", 10);
            var terms = q.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (terms.Length == 0) return new { Results = new List<object>() };
            var results = Docs
                .Select(doc =>
                {
                    var head = Versions.FirstOrDefault(v => v.DocumentId == doc.Id && v.VersionId == doc.CurrentVersion);
                    var haystack = $"{doc.Title} {head?.Content ?? ""}".ToLowerInvariant();
                    var matched = terms.Count(t => haystack.Contains(t));
                    return new
                    {
                        doc.Id, doc.Title,
                        Relevance = Math.Floor(matched / (double)terms.Length * 100 + 0.5) / 100,
                    };
                })
                .Where(r => r.Relevance >= 0.5) // documents must match at least half the terms
                .OrderByDescending(r => r.Relevance)
                .Take(limit)
                .ToList();
            return new { Results = results };
        },

        ["GET /api/users"] = (_, _, _) => new { Users },

        ["GET /api/users/{id}"] = (parameters, _, _) => GetUser(parameters["id"]),

        ["GET /api/users/{id}/groups"] = (parameters, _, _) =>
        {
            var user = GetUser(parameters["id"]);
            return new
            {
                UserId = user.Id,
                Groups = Groups.Where(g => g.MemberIds.Contains(user.Id)).Select(g => new { g.Id, g.Name }).ToList(),
            };
        },

        ["GET /api/groups"] = (_, _, _) => new
        {
            Groups = Groups.Select(g => new { g.Id, g.Name, MemberCount = g.MemberIds.Count }).ToList(),
        },

        ["GET /api/groups/{id}/members"] = (parameters, _, _) =>
        {
            var group = GetGroup(parameters["id"]);
            return new { GroupId = group.Id, Members = group.MemberIds.Select(GetUser).ToList() };
        },

        ["POST /api/groups/{id}/members"] = (parameters, _, rawBody) =>
        {
            var group = GetGroup(parameters["id"]);
            var body = RequireBody(rawBody, "POST /api/groups/{id}/members");
            if (!IsString(body["userId"]))
            {
                throw new ApiError("POST /api/groups/{id}/members requires a body with a string field \"userId\".");
            }
            var user = GetUser(body["userId"]!.GetValue<string>());
            if (group.MemberIds.Contains(user.Id))
            {
                throw new ApiError($"User \"{user.Id}\" is already a member of group \"{group.Id}\" ({group.Name}).");
            }
            group.MemberIds.Add(user.Id);
            return new { GroupId = group.Id, UserId = user.Id, MemberCount = group.MemberIds.Count };
        },

        ["GET /api/documents/{id}/permissions"] = (parameters, _, _) =>
        {
            var doc = GetDoc(parameters["id"]);
            return new
            {
                DocumentId = doc.Id,
                Grants = Grants
                    .Where(g => g.DocumentId == doc.Id)
                    .Select(g => new
                    {
                        g.GrantId,
                        g.GranteeType,
                        g.GranteeId,
                        GranteeName = g.GranteeType == "user" ? GetUser(g.GranteeId).Name : GetGroup(g.GranteeId).Name,
                        g.Level,
                    })
                    .ToList(),
            };
        },

        ["POST /api/documents/{id}/permissions"] = (parameters, _, rawBody) =>
        {
            var doc = GetDoc(parameters["id"]);
            var body = RequireBody(rawBody, "POST /api/documents/{id}/permissions");
            var granteeType = IsString(body["granteeType"]) ? body["granteeType"]!.GetValue<string>() : null;
            var granteeId = IsString(body["granteeId"]) ? body["granteeId"]!.GetValue<string>() : null;
            var level = IsString(body["level"]) ? body["level"]!.GetValue<string>() : null;
            if (granteeType is not ("user" or "group"))
            {
                throw new ApiError("Field \"granteeType\" must be \"user\" or \"group\".");
            }
            if (granteeId is null)
            {
                throw new ApiError("Field \"granteeId\" must be a user or group ID string, e.g. \"user-002\" or \"grp-001\".");
            }
            if (level is not ("read" or "write" or "admin"))
            {
                throw new ApiError("Field \"level\" must be one of \"read\", \"write\", or \"admin\".");
            }
            // Validate the grantee exists.
            if (granteeType == "user") GetUser(granteeId);
            else GetGroup(granteeId);
            var existing = Grants.FirstOrDefault(
                g => g.DocumentId == doc.Id && g.GranteeType == granteeType && g.GranteeId == granteeId);
            if (existing is not null)
            {
                existing.Level = level;
                return new { existing.GrantId, DocumentId = doc.Id, GranteeType = granteeType, GranteeId = granteeId, Level = level };
            }
            var grant = new Grant
            {
                GrantId = $"grant-{_nextGrantNum++:000}",
                DocumentId = doc.Id,
                GranteeType = granteeType,
                GranteeId = granteeId,
                Level = level,
            };
            Grants.Add(grant);
            return new { grant.GrantId, DocumentId = doc.Id, GranteeType = granteeType, GranteeId = granteeId, Level = level };
        },

        ["DELETE /api/documents/{id}/permissions/{grantId}"] = (parameters, _, _) =>
        {
            var doc = GetDoc(parameters["id"]);
            var grant = Grants.FirstOrDefault(g => g.DocumentId == doc.Id && g.GrantId == parameters["grantId"]);
            if (grant is null)
            {
                throw new ApiError(
                    $"No grant \"{parameters["grantId"]}\" on document \"{doc.Id}\". " +
                    "Use GET /api/documents/{id}/permissions to list the grants and their IDs.");
            }
            Grants.Remove(grant);
            return new { DocumentId = doc.Id, Revoked = parameters["grantId"] };
        },

        ["GET /api/documents/{id}/permissions/check"] = (parameters, query, _) =>
        {
            var doc = GetDoc(parameters["id"]);
            var userId = query.Get("userId");
            if (string.IsNullOrEmpty(userId))
            {
                throw new ApiError(
                    "Missing required query parameter \"userId\". " +
                    "Example: path \"/api/documents/doc-001/permissions/check\" with query \"userId=user-002\".");
            }
            var user = GetUser(userId);
            var sources = new List<(string Via, string Level)>();
            foreach (var g in Grants.Where(g => g.DocumentId == doc.Id))
            {
                if (g.GranteeType == "user" && g.GranteeId == user.Id)
                {
                    sources.Add(("direct", g.Level));
                }
                else if (g.GranteeType == "group" && GetGroup(g.GranteeId).MemberIds.Contains(user.Id))
                {
                    sources.Add(($"group:{GetGroup(g.GranteeId).Name}", g.Level));
                }
            }
            var level = "none";
            if (sources.Count > 0)
            {
                level = "read";
                foreach (var s in sources)
                {
                    if (LevelRank[s.Level] > LevelRank[level]) level = s.Level;
                }
            }
            return new
            {
                DocumentId = doc.Id,
                UserId = user.Id,
                Level = level,
                Sources = sources.Select(s => new { s.Via, s.Level }).ToList(),
            };
        },

        ["GET /api/documents/{id}/versions"] = (parameters, _, _) =>
        {
            var doc = GetDoc(parameters["id"]);
            return new
            {
                DocumentId = doc.Id,
                doc.CurrentVersion,
                Versions = Versions
                    .Where(v => v.DocumentId == doc.Id)
                    .Select(v => new
                    {
                        v.VersionId,
                        Author = new { Id = v.AuthorId, GetUser(v.AuthorId).Name },
                        v.CreatedAt,
                        v.Note,
                    })
                    .ToList(),
            };
        },

        ["GET /api/documents/{id}/versions/{versionId}"] = (parameters, _, _) =>
        {
            var doc = GetDoc(parameters["id"]);
            var version = Versions.FirstOrDefault(v => v.DocumentId == doc.Id && v.VersionId == parameters["versionId"]);
            if (version is null)
            {
                throw new ApiError(
                    $"No version \"{parameters["versionId"]}\" of document \"{doc.Id}\". " +
                    "Use GET /api/documents/{id}/versions to list the versions.");
            }
            return new
            {
                DocumentId = doc.Id,
                version.VersionId,
                Author = new { Id = version.AuthorId, GetUser(version.AuthorId).Name },
                version.CreatedAt,
                version.Note,
                version.Content,
            };
        },

        ["GET /api/documents/{id}/versions/compare"] = (parameters, query, _) =>
        {
            var doc = GetDoc(parameters["id"]);
            var fromId = query.Get("from");
            var toId = query.Get("to");
            if (string.IsNullOrEmpty(fromId) || string.IsNullOrEmpty(toId))
            {
                throw new ApiError(
                    "Both query parameters \"from\" and \"to\" are required, e.g. query \"from=v1&to=v3\". " +
                    "Use GET /api/documents/{id}/versions to list the version IDs.");
            }
            var from = Versions.FirstOrDefault(v => v.DocumentId == doc.Id && v.VersionId == fromId);
            var to = Versions.FirstOrDefault(v => v.DocumentId == doc.Id && v.VersionId == toId);
            if (from is null || to is null)
            {
                throw new ApiError(
                    $"Version \"{(from is null ? fromId : toId)}\" does not exist on document \"{doc.Id}\". " +
                    "Use GET /api/documents/{id}/versions to list the versions.");
            }
            return new
            {
                DocumentId = doc.Id,
                From = from.VersionId,
                To = to.VersionId,
                ContentChanged = from.Content != to.Content,
                SizeDelta = to.Content.Length - from.Content.Length,
                Authors = new[] { GetUser(from.AuthorId).Name, GetUser(to.AuthorId).Name },
            };
        },

        ["POST /api/documents/{id}/versions/{versionId}/restore"] = (parameters, _, _) =>
        {
            var doc = GetDoc(parameters["id"]);
            var source = Versions.FirstOrDefault(v => v.DocumentId == doc.Id && v.VersionId == parameters["versionId"]);
            if (source is null)
            {
                throw new ApiError(
                    $"No version \"{parameters["versionId"]}\" of document \"{doc.Id}\" to restore. " +
                    "Use GET /api/documents/{id}/versions to list the versions.");
            }
            var newVersionId = $"v{Versions.Count(v => v.DocumentId == doc.Id) + 1}";
            var createdAt = NowIso();
            Versions.Add(new Version
            {
                DocumentId = doc.Id,
                VersionId = newVersionId,
                AuthorId = ActingUser,
                CreatedAt = createdAt,
                Note = $"Restored from {source.VersionId}",
                Content = source.Content,
            });
            doc.CurrentVersion = newVersionId;
            doc.UpdatedAt = createdAt;
            return new { DocumentId = doc.Id, RestoredFrom = source.VersionId, NewVersion = newVersionId, CreatedAt = createdAt };
        },
    };
}
