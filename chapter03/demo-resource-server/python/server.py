# DemoResourceServer — the shared server for the chapter 3 client samples.
#
# It exposes a small catalog of text/markdown resources, a resource template (§3.4.1),
# a tool that returns a resource link instead of content (§3.3.2), and a handful of
# deliberately awkward resources (oversized, binary, broken, chained links) that the
# resource-link-client sample uses to exercise its hardened link resolution (§3.3.4).
from mcp.server import MCPServer
from mcp_types import ResourceLink

server = MCPServer(name="demo-resource-server", version="0.1.0")

# --- Tools ---------------------------------------------------------------------------


# Book extract (§3.3.2): a tool that returns a POINTER to a resource, not the payload.
# structured_output=False keeps the SDK from publishing the ResourceLink model as an
# output schema — the link rides in the unstructured content, as in the C# canonical.
@server.tool(structured_output=False)
def get_tip_of_the_day() -> ResourceLink:
    return ResourceLink(
        uri="file:///helpful_tip.txt",
        name="tip_of_the_day",
    )


# Returns several links at once so the resource-link-client can demonstrate every
# hardening from §3.3.4 in a single pass: a normal link, an oversized one (size
# budget), a binary one (MIME filtering), a dangling one (error handling), and the
# start of a link chain (depth guard).
@server.tool(
    name="get_research_bundle",
    description="Returns links to everything relevant for the quarterly research write-up.",
    structured_output=False,
)
def get_research_bundle() -> list[ResourceLink]:
    return [
        ResourceLink(
            uri="file:///release_notes.md",
            name="release_notes",
            mime_type="text/markdown",
        ),
        ResourceLink(
            uri="file:///big_dataset.csv",
            name="big_dataset",
            mime_type="text/csv",
            size=BIG_DATASET_SIZE,  # declared up front — a budget can reject without reading
        ),
        ResourceLink(
            uri="file:///podcast.wav",
            name="quarterly_podcast",
            mime_type="audio/wav",
        ),
        ResourceLink(
            uri="file:///does_not_exist.txt",
            name="broken_link",
        ),
        ResourceLink(
            uri="chain://a",
            name="chain_start",
            mime_type="application/json",
        ),
    ]


# --- Ordinary text/markdown resources --------------------------------------------------


@server.resource(
    "file:///user_guide.md", name="user_guide", title="Nimbus Notes User Guide",
    mime_type="text/markdown",
    description="Getting-started guide for the Nimbus Notes application.",
)
def user_guide() -> str:
    return """\
# Nimbus Notes — User Guide

## Setup
1. Install the Nimbus Notes desktop client (v3 or later).
2. Sign in with your workspace account.
3. Choose a vault location; local-only vaults skip cloud sync entirely.

## Daily use
- `Ctrl+N` creates a note; notes autosave every five seconds.
- Tag notes with `#project/...` tags to group them in the sidebar.
- The command palette (`Ctrl+K`) reaches every feature without the mouse.

## Troubleshooting
If sync stalls, check Settings > Sync > Log. A red entry means the vault
key changed; re-enter it under Settings > Security."""


@server.resource(
    "file:///release_notes.md", name="release_notes", title="Release Notes 3.2",
    mime_type="text/markdown",
    description="What changed in Nimbus Notes 3.2.",
)
def release_notes() -> str:
    return """\
# Nimbus Notes 3.2 — Release Notes

- New: offline vaults can now be converted to synced vaults in place.
- Improved: search indexes attachments up to 25 MB.
- Fixed: the sidebar no longer forgets collapsed tag groups on restart.
- Deprecated: the legacy `nimbus://` deep-link scheme; use `notes://`."""


@server.resource(
    "file:///helpful_tip.txt", name="helpful_tip", title="Tip of the Day",
    mime_type="text/plain",
    description="A rotating usage tip. Target of the get_tip_of_the_day tool's resource link.",
)
def helpful_tip() -> str:
    return (
        "Tip of the day: pin a note to the sidebar by dragging it onto the star icon — "
        "pinned notes survive vault switches."
    )


# --- Awkward resources for the link-resolution hardening demo --------------------------

BIG_DATASET_SIZE = 64_000


@server.resource(
    "file:///big_dataset.csv", name="big_dataset", title="Quarterly Telemetry Export",
    mime_type="text/csv",
    description="A large CSV export. Deliberately oversized to exercise client size budgets.",
)
def big_dataset() -> str:
    lines = ["day,active_users,notes_created,sync_errors"]
    length = len(lines[0]) + 1
    day = 1
    while length < BIG_DATASET_SIZE:
        line = f"{day},{1000 + day * 7 % 500},{300 + day * 13 % 900},{day * 3 % 17}"
        lines.append(line)
        length += len(line) + 1
        day += 1
    return "\n".join(lines) + "\n"


@server.resource(
    "file:///podcast.wav", name="quarterly_podcast", title="Quarterly Podcast",
    mime_type="audio/wav",
    description="A binary audio resource. Exercises client MIME-type filtering.",
)
def podcast() -> bytes:
    # A 44-byte silent WAV header — real enough to be honest about the MIME type.
    # Returning bytes makes the SDK serve blob contents (base64 on the wire).
    return bytes([
        0x52, 0x49, 0x46, 0x46, 0x24, 0, 0, 0, 0x57, 0x41, 0x56, 0x45,
        0x66, 0x6D, 0x74, 0x20, 0x10, 0, 0, 0, 1, 0, 1, 0,
        0x44, 0xAC, 0, 0, 0x88, 0x58, 0x01, 0, 2, 0, 0x10, 0,
        0x64, 0x61, 0x74, 0x61, 0, 0, 0, 0,
    ])


# A link CHAIN: each resource's content is itself a serialized resource_link content
# block pointing at the next hop — and the last hop points back to the first, forming
# a cycle. The protocol cannot express links in a read result natively (contents are
# text|blob only), so onward links like these exist purely by client/server convention;
# the resource-link-client follows the convention and shows its depth guard stopping
# the cycle.
@server.resource(
    "chain://a", name="chain_a", mime_type="application/json",
    description="First hop of a deliberate link chain (a -> b -> c -> a).",
)
def chain_a() -> str:
    return '{"type":"resource_link","uri":"chain://b","name":"chain_b"}'


@server.resource(
    "chain://b", name="chain_b", mime_type="application/json",
    description="Second hop of the link chain.",
)
def chain_b() -> str:
    return '{"type":"resource_link","uri":"chain://c","name":"chain_c"}'


@server.resource(
    "chain://c", name="chain_c", mime_type="application/json",
    description="Third hop of the link chain — points back to chain://a.",
)
def chain_c() -> str:
    return '{"type":"resource_link","uri":"chain://a","name":"chain_a"}'


# --- Resource templates (§3.4.1) --------------------------------------------------------


# Book extract (§3.4.1): the decorator declares the template; the SDK routes any
# matching read to the function with {id} bound to the typed parameter.
@server.resource("file://user/{id}/preferences.md", name="User Preferences",
                 mime_type="text/markdown")
def user_preferences(id: int) -> str:
    # retrieve user preferences from your data source of choice
    theme = "dark" if id % 2 == 0 else "light"
    return f"# Preferences for user {id}\n- theme: {theme}\n- sync: enabled\n"


# The C# canonical registers an RFC 6570 LEVEL-4 template here
# (docs://search{?tags*,limit}). The Python 2.0.0b1 SDK REJECTS the explode
# modifier at registration time (InvalidUriTemplate: "Explode modifier on
# {?tags*} is not supported for matching"), so this port registers the level-3
# form without the modifier — single-valued ?tags=...&limit=... reads route and
# bind fine. See the sample README for the full verdict per SDK.
@server.resource("docs://search{?tags,limit}", name="doc_search",
                 description="Searches the documentation catalog by tag.")
def search_docs(tags: str | None = None, limit: int | None = None) -> str:
    return (
        f"Search results for tags [{tags or '(none)'}] "
        f"(limit {limit if limit is not None else 'default'}): "
        "user_guide.md, release_notes.md"
    )


if __name__ == "__main__":
    server.run()
