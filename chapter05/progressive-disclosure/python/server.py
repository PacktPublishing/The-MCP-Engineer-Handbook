# Progressive disclosure over a document-management API (Chapter 5, §5.1.2).
# Four static tools — list, search, describe, execute — front an endpoint
# manifest loaded from ../data/endpoints.json. The endpoints are data, not tools.
#
# Python port of the TypeScript canonical (typescript/src/server.ts + api.ts).
# The simulated API lives in this file too, so the sample stays one file.

import json
import math
from datetime import datetime, timezone
from pathlib import Path
from typing import Annotated, Any
from urllib.parse import parse_qsl, unquote

from mcp.server import MCPServer
from mcp_types import CallToolResult, TextContent
from pydantic import Field

# --- Endpoint manifest (generated data — see ../agent-instructions.md) ---

MANIFEST_PATH = Path(__file__).resolve().parent.parent / "data" / "endpoints.json"
manifest: dict[str, Any] = json.loads(MANIFEST_PATH.read_text(encoding="utf-8"))

group_names = [g["name"] for g in manifest["groups"]]


def match_endpoint(method: str, path: str) -> tuple[dict[str, Any], dict[str, str]] | None:
    """Match a path (either the "{id}" template itself or a concrete path like
    "/api/documents/doc-001/permissions") against the manifest. Literal
    segments beat placeholders, so "/api/documents/search" is not swallowed
    by "/api/documents/{id}"."""
    segments = [s for s in path.split("/") if s]
    best: tuple[dict[str, Any], dict[str, str], int] | None = None
    for endpoint in manifest["endpoints"]:
        if endpoint["method"] != method:
            continue
        template_segments = [s for s in endpoint["path"].split("/") if s]
        if len(template_segments) != len(segments):
            continue
        params: dict[str, str] = {}
        literals = 0
        matched = True
        for t, s in zip(template_segments, segments):
            if t.startswith("{") and t.endswith("}"):
                params[t[1:-1]] = unquote(s)
            elif t == s:
                literals += 1
            else:
                matched = False
                break
        if matched and (best is None or literals > best[2]):
            best = (endpoint, params, literals)
    return (best[0], best[1]) if best else None


# --- Simulated document-management API backing execute_endpoint ---
# A small in-memory store stands in for the real service; each endpoint
# handler returns plausible JSON or raises an ApiError whose message is
# written to guide the model toward a correct retry.


class ApiError(Exception):
    pass


# The simulation has no authentication; all calls act as this user.
ACTING_USER = "user-001"

users: list[dict[str, Any]] = [
    {"id": "user-001", "name": "Alice Chen", "email": "alice.chen@example.com"},
    {"id": "user-002", "name": "Bob Martinez", "email": "bob.martinez@example.com"},
    {"id": "user-003", "name": "Carol Okafor", "email": "carol.okafor@example.com"},
    {"id": "user-004", "name": "Dana Kim", "email": "dana.kim@example.com"},
]

groups: list[dict[str, Any]] = [
    {"id": "grp-001", "name": "Leadership", "memberIds": ["user-001", "user-002"]},
    {"id": "grp-002", "name": "Finance", "memberIds": ["user-003", "user-004"]},
]

docs: list[dict[str, Any]] = [
    {
        "id": "doc-001",
        "title": "Quarterly Report Q1 2026",
        "ownerId": "user-001",
        "tags": ["finance", "quarterly"],
        "createdAt": "2026-01-05T09:00:00Z",
        "updatedAt": "2026-04-02T14:30:00Z",
        "currentVersion": "v3",
    },
    {
        "id": "doc-002",
        "title": "Employee Handbook",
        "ownerId": "user-003",
        "tags": ["hr", "policy"],
        "createdAt": "2025-08-12T10:00:00Z",
        "updatedAt": "2025-08-12T10:00:00Z",
        "currentVersion": "v1",
    },
    {
        "id": "doc-003",
        "title": "Product Roadmap 2026",
        "ownerId": "user-002",
        "tags": ["product", "planning"],
        "createdAt": "2025-11-20T08:15:00Z",
        "updatedAt": "2026-02-01T11:00:00Z",
        "currentVersion": "v2",
    },
]

versions: list[dict[str, Any]] = [
    {
        "documentId": "doc-001",
        "versionId": "v1",
        "authorId": "user-001",
        "createdAt": "2026-01-05T09:00:00Z",
        "note": "Initial draft",
        "content": "Quarterly Report Q1 2026 — draft outline. Revenue and expense sections pending.",
    },
    {
        "documentId": "doc-001",
        "versionId": "v2",
        "authorId": "user-002",
        "createdAt": "2026-02-10T16:45:00Z",
        "note": "Added revenue figures",
        "content": "Quarterly Report Q1 2026. Revenue grew 12% quarter over quarter, driven by enterprise renewals. Expense section pending.",
    },
    {
        "documentId": "doc-001",
        "versionId": "v3",
        "authorId": "user-001",
        "createdAt": "2026-04-02T14:30:00Z",
        "note": "Final: expenses and outlook",
        "content": "Quarterly Report Q1 2026. Revenue grew 12% quarter over quarter, driven by enterprise renewals. Operating expenses held flat. Outlook for Q2 remains positive.",
    },
    {
        "documentId": "doc-002",
        "versionId": "v1",
        "authorId": "user-003",
        "createdAt": "2025-08-12T10:00:00Z",
        "note": "Initial publication",
        "content": "Employee Handbook. Covers onboarding, leave policy, and code of conduct.",
    },
    {
        "documentId": "doc-003",
        "versionId": "v1",
        "authorId": "user-002",
        "createdAt": "2025-11-20T08:15:00Z",
        "note": "Initial roadmap",
        "content": "Product Roadmap 2026. H1: platform consolidation. H2: TBD.",
    },
    {
        "documentId": "doc-003",
        "versionId": "v2",
        "authorId": "user-004",
        "createdAt": "2026-02-01T11:00:00Z",
        "note": "H2 themes added",
        "content": "Product Roadmap 2026. H1: platform consolidation. H2: analytics and insights suite.",
    },
]

grants: list[dict[str, Any]] = [
    {"grantId": "grant-001", "documentId": "doc-001", "granteeType": "user", "granteeId": "user-001", "level": "admin"},
    {"grantId": "grant-002", "documentId": "doc-001", "granteeType": "group", "granteeId": "grp-002", "level": "read"},
    {"grantId": "grant-003", "documentId": "doc-001", "granteeType": "group", "granteeId": "grp-001", "level": "write"},
    {"grantId": "grant-004", "documentId": "doc-002", "granteeType": "user", "granteeId": "user-003", "level": "admin"},
    {"grantId": "grant-005", "documentId": "doc-002", "granteeType": "group", "granteeId": "grp-001", "level": "read"},
    {"grantId": "grant-006", "documentId": "doc-003", "granteeType": "user", "granteeId": "user-002", "level": "admin"},
]

next_doc_num = 4
next_grant_num = 7


def now_iso() -> str:
    return datetime.now(timezone.utc).isoformat(timespec="milliseconds").replace("+00:00", "Z")


def get_doc(doc_id: str) -> dict[str, Any]:
    doc = next((d for d in docs if d["id"] == doc_id), None)
    if doc is None:
        raise ApiError(
            f'No document with id "{doc_id}". Use GET /api/documents to list documents, '
            "or GET /api/documents/search to find one by title or content."
        )
    return doc


def get_user(user_id: str) -> dict[str, Any]:
    user = next((u for u in users if u["id"] == user_id), None)
    if user is None:
        raise ApiError(f'No user with id "{user_id}". Use GET /api/users to list users.')
    return user


def get_group(group_id: str) -> dict[str, Any]:
    group = next((g for g in groups if g["id"] == group_id), None)
    if group is None:
        raise ApiError(f'No group with id "{group_id}". Use GET /api/groups to list groups.')
    return group


def require_body(body: Any, endpoint: str) -> dict[str, Any]:
    if not isinstance(body, dict):
        raise ApiError(
            f"{endpoint} requires a JSON object request body. "
            "Use describe_endpoint to see the request schema."
        )
    return body


LEVEL_RANK = {"read": 1, "write": 2, "admin": 3}


def doc_meta(doc: dict[str, Any]) -> dict[str, Any]:
    return {k: doc[k] for k in ("id", "title", "ownerId", "createdAt", "updatedAt")}


def query_get(query: list[tuple[str, str]], name: str) -> str | None:
    return next((v for k, v in query if k == name), None)


def int_param(query: list[tuple[str, str]], name: str, default: int) -> int:
    raw = query_get(query, name)
    if raw is None:
        return default
    try:
        return int(raw)
    except ValueError:
        return default


# Handlers are keyed by "<METHOD> <path template>"; params holds the values
# captured from the {placeholders} in the template.
handlers: dict[str, Any] = {}


def handler(key: str):
    def register(fn):
        handlers[key] = fn
        return fn

    return register


@handler("GET /api/documents")
def list_documents(params, query, body):
    result = sorted(docs, key=lambda d: d["updatedAt"], reverse=True)
    owner_id = query_get(query, "ownerId")
    if owner_id:
        result = [d for d in result if d["ownerId"] == owner_id]
    total = len(result)
    offset = int_param(query, "offset", 0)
    limit = int_param(query, "limit", 20)
    return {"documents": [doc_meta(d) for d in result[offset : offset + limit]], "total": total}


@handler("POST /api/documents")
def create_document(params, query, raw_body):
    global next_doc_num, next_grant_num
    body = require_body(raw_body, "POST /api/documents")
    if not isinstance(body.get("title"), str) or not isinstance(body.get("content"), str):
        raise ApiError(
            'POST /api/documents requires a body with string fields "title" and "content" '
            '(and optionally "tags", an array of strings).'
        )
    doc_id = f"doc-{next_doc_num:03d}"
    next_doc_num += 1
    created_at = now_iso()
    doc = {
        "id": doc_id,
        "title": body["title"],
        "ownerId": ACTING_USER,
        "tags": [str(t) for t in body["tags"]] if isinstance(body.get("tags"), list) else [],
        "createdAt": created_at,
        "updatedAt": created_at,
        "currentVersion": "v1",
    }
    docs.append(doc)
    versions.append(
        {
            "documentId": doc_id,
            "versionId": "v1",
            "authorId": ACTING_USER,
            "createdAt": created_at,
            "note": "Initial version",
            "content": body["content"],
        }
    )
    grants.append(
        {
            "grantId": f"grant-{next_grant_num:03d}",
            "documentId": doc_id,
            "granteeType": "user",
            "granteeId": ACTING_USER,
            "level": "admin",
        }
    )
    next_grant_num += 1
    return {"id": doc_id, "title": doc["title"], "ownerId": doc["ownerId"], "createdAt": created_at, "currentVersion": "v1"}


@handler("GET /api/documents/{id}")
def get_document(params, query, body):
    doc = get_doc(params["id"])
    head = next(
        (v for v in versions if v["documentId"] == doc["id"] and v["versionId"] == doc["currentVersion"]), None
    )
    return {**doc, "content": head["content"] if head else ""}


@handler("PATCH /api/documents/{id}")
def update_document(params, query, raw_body):
    doc = get_doc(params["id"])
    body = require_body(raw_body, "PATCH /api/documents/{id}")
    if "title" not in body and "content" not in body and "tags" not in body:
        raise ApiError(
            'PATCH /api/documents/{id} requires at least one of "title", "content", or "tags" in the body.'
        )
    if "title" in body:
        doc["title"] = str(body["title"])
    if "tags" in body and isinstance(body["tags"], list):
        doc["tags"] = [str(t) for t in body["tags"]]
    if "content" in body:
        version_id = f"v{len([v for v in versions if v['documentId'] == doc['id']]) + 1}"
        versions.append(
            {
                "documentId": doc["id"],
                "versionId": version_id,
                "authorId": ACTING_USER,
                "createdAt": now_iso(),
                "note": "Content update",
                "content": str(body["content"]),
            }
        )
        doc["currentVersion"] = version_id
    doc["updatedAt"] = now_iso()
    return {"id": doc["id"], "title": doc["title"], "updatedAt": doc["updatedAt"], "currentVersion": doc["currentVersion"]}


@handler("DELETE /api/documents/{id}")
def delete_document(params, query, body):
    doc = get_doc(params["id"])
    docs.remove(doc)
    versions[:] = [v for v in versions if v["documentId"] != doc["id"]]
    grants[:] = [g for g in grants if g["documentId"] != doc["id"]]
    return {"deleted": doc["id"]}


@handler("GET /api/documents/search")
def search_documents(params, query, body):
    q = query_get(query, "q")
    if not q:
        raise ApiError(
            'Missing required query parameter "q". '
            'Example: execute_endpoint with path "/api/documents/search" and query "q=quarterly report".'
        )
    limit = int_param(query, "limit", 10)
    terms = q.lower().split()
    if not terms:  # whitespace-only q: no terms can match (TS gets there via NaN relevance)
        return {"results": []}
    results = []
    for doc in docs:
        head = next(
            (v for v in versions if v["documentId"] == doc["id"] and v["versionId"] == doc["currentVersion"]), None
        )
        haystack = f"{doc['title']} {head['content'] if head else ''}".lower()
        matched = len([t for t in terms if t in haystack])
        relevance = math.floor(matched / len(terms) * 100 + 0.5) / 100
        if relevance == int(relevance):
            relevance = int(relevance)  # JSON as "1", not "1.0", to match the TS output
        results.append({"id": doc["id"], "title": doc["title"], "relevance": relevance})
    results = [r for r in results if r["relevance"] >= 0.5]  # documents must match at least half the terms
    results.sort(key=lambda r: r["relevance"], reverse=True)
    return {"results": results[:limit]}


@handler("GET /api/users")
def list_users(params, query, body):
    return {"users": users}


@handler("GET /api/users/{id}")
def get_user_profile(params, query, body):
    return get_user(params["id"])


@handler("GET /api/users/{id}/groups")
def get_user_groups(params, query, body):
    user = get_user(params["id"])
    return {
        "userId": user["id"],
        "groups": [
            {"id": g["id"], "name": g["name"]} for g in groups if user["id"] in g["memberIds"]
        ],
    }


@handler("GET /api/groups")
def list_groups(params, query, body):
    return {"groups": [{"id": g["id"], "name": g["name"], "memberCount": len(g["memberIds"])} for g in groups]}


@handler("GET /api/groups/{id}/members")
def get_group_members(params, query, body):
    group = get_group(params["id"])
    return {"groupId": group["id"], "members": [get_user(uid) for uid in group["memberIds"]]}


@handler("POST /api/groups/{id}/members")
def add_group_member(params, query, raw_body):
    group = get_group(params["id"])
    body = require_body(raw_body, "POST /api/groups/{id}/members")
    if not isinstance(body.get("userId"), str):
        raise ApiError('POST /api/groups/{id}/members requires a body with a string field "userId".')
    user = get_user(body["userId"])
    if user["id"] in group["memberIds"]:
        raise ApiError(f'User "{user["id"]}" is already a member of group "{group["id"]}" ({group["name"]}).')
    group["memberIds"].append(user["id"])
    return {"groupId": group["id"], "userId": user["id"], "memberCount": len(group["memberIds"])}


@handler("GET /api/documents/{id}/permissions")
def list_permissions(params, query, body):
    doc = get_doc(params["id"])
    return {
        "documentId": doc["id"],
        "grants": [
            {
                "grantId": g["grantId"],
                "granteeType": g["granteeType"],
                "granteeId": g["granteeId"],
                "granteeName": get_user(g["granteeId"])["name"]
                if g["granteeType"] == "user"
                else get_group(g["granteeId"])["name"],
                "level": g["level"],
            }
            for g in grants
            if g["documentId"] == doc["id"]
        ],
    }


@handler("POST /api/documents/{id}/permissions")
def grant_permission(params, query, raw_body):
    global next_grant_num
    doc = get_doc(params["id"])
    body = require_body(raw_body, "POST /api/documents/{id}/permissions")
    grantee_type = body.get("granteeType")
    grantee_id = body.get("granteeId")
    level = body.get("level")
    if grantee_type not in ("user", "group"):
        raise ApiError('Field "granteeType" must be "user" or "group".')
    if not isinstance(grantee_id, str):
        raise ApiError('Field "granteeId" must be a user or group ID string, e.g. "user-002" or "grp-001".')
    if level not in ("read", "write", "admin"):
        raise ApiError('Field "level" must be one of "read", "write", or "admin".')
    # Validate the grantee exists.
    if grantee_type == "user":
        get_user(grantee_id)
    else:
        get_group(grantee_id)
    existing = next(
        (
            g
            for g in grants
            if g["documentId"] == doc["id"] and g["granteeType"] == grantee_type and g["granteeId"] == grantee_id
        ),
        None,
    )
    if existing:
        existing["level"] = level
        return {
            "grantId": existing["grantId"],
            "documentId": doc["id"],
            "granteeType": grantee_type,
            "granteeId": grantee_id,
            "level": level,
        }
    grant = {
        "grantId": f"grant-{next_grant_num:03d}",
        "documentId": doc["id"],
        "granteeType": grantee_type,
        "granteeId": grantee_id,
        "level": level,
    }
    next_grant_num += 1
    grants.append(grant)
    return {
        "grantId": grant["grantId"],
        "documentId": doc["id"],
        "granteeType": grantee_type,
        "granteeId": grantee_id,
        "level": level,
    }


@handler("DELETE /api/documents/{id}/permissions/{grantId}")
def revoke_permission(params, query, body):
    doc = get_doc(params["id"])
    grant = next(
        (g for g in grants if g["documentId"] == doc["id"] and g["grantId"] == params["grantId"]), None
    )
    if grant is None:
        raise ApiError(
            f'No grant "{params["grantId"]}" on document "{doc["id"]}". '
            "Use GET /api/documents/{id}/permissions to list the grants and their IDs."
        )
    grants.remove(grant)
    return {"documentId": doc["id"], "revoked": params["grantId"]}


@handler("GET /api/documents/{id}/permissions/check")
def check_permission(params, query, body):
    doc = get_doc(params["id"])
    user_id = query_get(query, "userId")
    if not user_id:
        raise ApiError(
            'Missing required query parameter "userId". '
            'Example: path "/api/documents/doc-001/permissions/check" with query "userId=user-002".'
        )
    user = get_user(user_id)
    sources = []
    for g in (g for g in grants if g["documentId"] == doc["id"]):
        if g["granteeType"] == "user" and g["granteeId"] == user["id"]:
            sources.append({"via": "direct", "level": g["level"]})
        elif g["granteeType"] == "group" and user["id"] in get_group(g["granteeId"])["memberIds"]:
            sources.append({"via": f"group:{get_group(g['granteeId'])['name']}", "level": g["level"]})
    if not sources:
        level = "none"
    else:
        level = "read"
        for s in sources:
            if LEVEL_RANK[s["level"]] > LEVEL_RANK[level]:
                level = s["level"]
    return {"documentId": doc["id"], "userId": user["id"], "level": level, "sources": sources}


@handler("GET /api/documents/{id}/versions")
def list_versions(params, query, body):
    doc = get_doc(params["id"])
    return {
        "documentId": doc["id"],
        "currentVersion": doc["currentVersion"],
        "versions": [
            {
                "versionId": v["versionId"],
                "author": {"id": v["authorId"], "name": get_user(v["authorId"])["name"]},
                "createdAt": v["createdAt"],
                "note": v["note"],
            }
            for v in versions
            if v["documentId"] == doc["id"]
        ],
    }


@handler("GET /api/documents/{id}/versions/{versionId}")
def get_version(params, query, body):
    doc = get_doc(params["id"])
    version = next(
        (v for v in versions if v["documentId"] == doc["id"] and v["versionId"] == params["versionId"]), None
    )
    if version is None:
        raise ApiError(
            f'No version "{params["versionId"]}" of document "{doc["id"]}". '
            "Use GET /api/documents/{id}/versions to list the versions."
        )
    return {
        "documentId": doc["id"],
        "versionId": version["versionId"],
        "author": {"id": version["authorId"], "name": get_user(version["authorId"])["name"]},
        "createdAt": version["createdAt"],
        "note": version["note"],
        "content": version["content"],
    }


@handler("GET /api/documents/{id}/versions/compare")
def compare_versions(params, query, body):
    doc = get_doc(params["id"])
    from_id = query_get(query, "from")
    to_id = query_get(query, "to")
    if not from_id or not to_id:
        raise ApiError(
            'Both query parameters "from" and "to" are required, e.g. query "from=v1&to=v3". '
            "Use GET /api/documents/{id}/versions to list the version IDs."
        )
    v_from = next((v for v in versions if v["documentId"] == doc["id"] and v["versionId"] == from_id), None)
    v_to = next((v for v in versions if v["documentId"] == doc["id"] and v["versionId"] == to_id), None)
    if v_from is None or v_to is None:
        raise ApiError(
            f'Version "{from_id if v_from is None else to_id}" does not exist on document "{doc["id"]}". '
            "Use GET /api/documents/{id}/versions to list the versions."
        )
    return {
        "documentId": doc["id"],
        "from": v_from["versionId"],
        "to": v_to["versionId"],
        "contentChanged": v_from["content"] != v_to["content"],
        "sizeDelta": len(v_to["content"]) - len(v_from["content"]),
        "authors": [get_user(v_from["authorId"])["name"], get_user(v_to["authorId"])["name"]],
    }


@handler("POST /api/documents/{id}/versions/{versionId}/restore")
def restore_version(params, query, body):
    doc = get_doc(params["id"])
    source = next(
        (v for v in versions if v["documentId"] == doc["id"] and v["versionId"] == params["versionId"]), None
    )
    if source is None:
        raise ApiError(
            f'No version "{params["versionId"]}" of document "{doc["id"]}" to restore. '
            "Use GET /api/documents/{id}/versions to list the versions."
        )
    new_version_id = f"v{len([v for v in versions if v['documentId'] == doc['id']]) + 1}"
    created_at = now_iso()
    versions.append(
        {
            "documentId": doc["id"],
            "versionId": new_version_id,
            "authorId": ACTING_USER,
            "createdAt": created_at,
            "note": f"Restored from {source['versionId']}",
            "content": source["content"],
        }
    )
    doc["currentVersion"] = new_version_id
    doc["updatedAt"] = created_at
    return {"documentId": doc["id"], "restoredFrom": source["versionId"], "newVersion": new_version_id, "createdAt": created_at}


# --- Server (matches the construction printed in Chapter 5) ---

server = MCPServer(
    name="document-management-api",
    version="1.0.0",
    instructions=(
        "This server provides access to a document management API with features for "
        "managing documents, users, groups, permissions, and document versioning. "
        "Use list_endpoints to browse available API groups, search_endpoints to find "
        "specific functionality, describe_endpoint to get full details before calling, "
        "and execute_endpoint to invoke API operations. "
        "Common workflows: querying document permissions, checking user access levels, "
        "comparing document versions, and managing document lifecycle."
    ),
)


def text(s: str) -> CallToolResult:
    return CallToolResult(content=[TextContent(type="text", text=s)])


def error_text(s: str) -> CallToolResult:
    return CallToolResult(content=[TextContent(type="text", text=s)], is_error=True)


def dump_json(value: Any) -> str:
    return json.dumps(value, indent=2, ensure_ascii=False)


# --- Tool 1: list_endpoints (navigation) ---


@server.tool(
    description=(
        "List the available endpoint groups of the document management API, or list the "
        "endpoints within a specific group. Call without arguments to see all groups. "
        "Provide a group name to see its endpoints."
    )
)
def list_endpoints(
    group: Annotated[
        str | None, Field(description="Group name to list endpoints for. Omit to see all groups.")
    ] = None,
):
    if group is None:
        lines = []
        for g in manifest["groups"]:
            count = len([e for e in manifest["endpoints"] if e["group"] == g["name"]])
            lines.append(f"- {g['name']} ({count} endpoint{'' if count == 1 else 's'})")
        return text(
            "Available API groups:\n\n" + "\n".join(lines) + "\n\n"
            "Use list_endpoints with a group name to see the endpoints in that group."
        )
    match = next((n for n in group_names if n.lower() == group.lower()), None)
    if match is None:
        return error_text(
            f'Unknown group "{group}". Available groups: {", ".join(group_names)}. '
            "Call list_endpoints without arguments to see all groups."
        )
    lines = [
        f"- {e['method']} {e['path']} — {e['summary']}"
        for e in manifest["endpoints"]
        if e["group"] == match
    ]
    return text(
        f'Endpoints in "{match}":\n\n' + "\n".join(lines) + "\n\n"
        "Use describe_endpoint to get full details for a specific endpoint."
    )


# --- Tool 2: search_endpoints (search) ---


@server.tool(
    description=(
        "Search the document management API endpoints with a free-text query. Matches "
        "endpoint paths, summaries, and descriptions — API metadata, not document content. "
        "Returns concise results; use describe_endpoint for full details."
    )
)
def search_endpoints(
    query: Annotated[
        str, Field(description='Free-text search terms, e.g. "write permission" or "compare versions".')
    ],
):
    terms = query.lower().split()
    if not terms:
        return error_text('Provide one or more search terms, e.g. "write permission".')
    matches = [
        e
        for e in manifest["endpoints"]
        if all(
            t in f"{e['method']} {e['path']} {e['group']} {e['summary']} {e['description']}".lower()
            for t in terms
        )
    ]
    if not matches:
        return text(
            f'No endpoints match "{query}". Try fewer or more general keywords, '
            "or use list_endpoints to browse the API groups."
        )
    lines = [f"- [{e['group']}] {e['method']} {e['path']} — {e['summary']}" for e in matches]
    return text(
        f'Found {len(matches)} endpoint(s) matching "{query}":\n\n' + "\n".join(lines) + "\n\n"
        "Use describe_endpoint to get full details before executing."
    )


# --- Tool 3: describe_endpoint (full metadata) ---


@server.tool(
    description=(
        "Get the full details of a single API endpoint: description, parameters, request "
        "body schema, and response schema. Call this before using execute_endpoint."
    )
)
def describe_endpoint(
    method: Annotated[str, Field(description='HTTP method, e.g. "GET".')],
    path: Annotated[
        str,
        Field(
            description='Endpoint path as shown by list_endpoints or search_endpoints, '
            'e.g. "/api/documents/{id}/permissions".'
        ),
    ],
):
    m = method.upper()
    found = next((e for e in manifest["endpoints"] if e["method"] == m and e["path"] == path), None)
    if found is None:
        matched = match_endpoint(m, path)
        found = matched[0] if matched else None
    if found is None:
        return error_text(
            f"No endpoint matches {m} {path}. Use list_endpoints to browse groups "
            "or search_endpoints to find functionality."
        )
    parts = [
        f"{found['method']} {found['path']} — {found['summary']}",
        f"Group: {found['group']}",
        found["description"],
    ]
    if found["parameters"]:
        lines = [
            f"- {p['name']} ({p['in']}, {p['type']}, {'required' if p['required'] else 'optional'}) — {p['description']}"
            for p in found["parameters"]
        ]
        parts.append("Parameters:\n" + "\n".join(lines))
    else:
        parts.append("Parameters: none")
    if found["requestBody"]:
        parts.append(
            f"Request body ({found['requestBody']['contentType']}):\n"
            + dump_json(found["requestBody"]["schema"])
        )
    else:
        parts.append("Request body: none")
    parts.append(f"Response: {found['response']['description']}\n" + dump_json(found["response"]["schema"]))
    return text("\n\n".join(parts))


# --- Tool 4: execute_endpoint (invocation) ---


@server.tool(
    description=(
        "Execute a document management API endpoint. Provide the HTTP method and the path "
        'with path parameters filled in (e.g. "/api/documents/doc-001/permissions"). Query '
        "strings can be embedded in the path or passed separately; POST and PATCH bodies "
        "are passed as a JSON string. Use describe_endpoint first to see the exact schema."
    )
)
def execute_endpoint(
    method: Annotated[str, Field(description='HTTP method of the endpoint, e.g. "GET" or "POST".')],
    path: Annotated[
        str,
        Field(
            description='Endpoint path with path parameters filled in, e.g. "/api/documents/doc-001/permissions". '
            "May include a query string."
        ),
    ],
    # The optional parameters take their defaults from Field(default=None) so the
    # annotations stay exactly `str`: with `str | None`, the SDK's pre-parsing turns
    # a JSON-string body into a dict before validation, and the call fails.
    query: Annotated[
        str,
        Field(
            default=None,
            description='Query string without the leading "?", e.g. "q=quarterly report". '
            "Alternative to embedding it in the path.",
        ),
    ],
    body: Annotated[
        str, Field(default=None, description="JSON request body, for POST and PATCH endpoints.")
    ],
):
    m = method.upper()
    pieces = path.split("?")
    pure_path, embedded_query = pieces[0], pieces[1] if len(pieces) > 1 else ""
    params = parse_qsl(embedded_query, keep_blank_values=True)
    params += parse_qsl(query or "", keep_blank_values=True)

    match = match_endpoint(m, pure_path)
    if match is None:
        other_methods = [
            e
            for e in manifest["endpoints"]
            if e["method"] != m
            and (matched := match_endpoint(e["method"], pure_path)) is not None
            and matched[0] is e
        ]
        if other_methods:
            hint = "The path exists with other methods: " + ", ".join(
                f"{e['method']} {e['path']}" for e in other_methods
            ) + "."
        else:
            hint = (
                "Use list_endpoints to browse groups or search_endpoints to find functionality, "
                "then describe_endpoint to confirm the exact method and path."
            )
        return error_text(f"No endpoint matches {m} {pure_path}. {hint}")
    endpoint, path_params = match

    parsed_body: Any = None
    if body is not None:
        try:
            parsed_body = json.loads(body)
        except ValueError as err:
            return error_text(
                f"The body argument is not valid JSON ({err}). "
                "Pass the request body as a JSON string; use describe_endpoint with "
                f"{m} {endpoint['path']} to see the request schema."
            )

    fn = handlers[f"{endpoint['method']} {endpoint['path']}"]
    try:
        result = fn(path_params, params, parsed_body)
        return text(dump_json(result))
    except ApiError as err:
        return error_text(str(err))


if __name__ == "__main__":
    server.run()
