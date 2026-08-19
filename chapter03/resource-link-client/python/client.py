# ResourceLinkClient — resolving resource links (§3.3.4).
#
# A tool can return a POINTER to a resource instead of its content. Most servers that
# do this expect the client to read the resource and substitute the contents into the
# tool result. This sample shows both versions from the chapter:
#
#   1. the book-page resolve_links — the bare substitution pass, run against the
#      demo server's get_tip_of_the_day
#   2. the production version — size budget, MIME-type filtering, error handling for
#      failed reads, and a depth guard against link chains — run against
#      get_research_bundle, whose five links exercise every guard
#
# Usage: uv run client.py
import asyncio
import json
import os
import sys
from dataclasses import dataclass, field
from pathlib import Path

from mcp.client import Client
from mcp.client.stdio import StdioServerParameters, stdio_client
from mcp.shared.exceptions import MCPError
from mcp_types import (
    AudioContent,
    BlobResourceContents,
    ContentBlock,
    ImageContent,
    ResourceContents,
    ResourceLink,
    TextContent,
    TextResourceContents,
)


async def main() -> None:
    transport = stdio_client(
        StdioServerParameters(command=sys.executable, args=[resolve_server_script()])
    )
    async with Client(transport) as client:
        # --- 1. The book-page version against a single well-behaved link -------------
        print("=" * 78)
        print("1. Bare substitution pass (the §3.3.4 book extract) on get_tip_of_the_day")
        print("=" * 78)

        tip_result = await client.call_tool("get_tip_of_the_day", {})
        print_blocks("tool result as returned", tip_result.content)
        print_blocks("after resolve_links", await resolve_links(tip_result.content, client))

        # --- 2. The hardened version against links that misbehave --------------------
        print("=" * 78)
        print("2. Hardened resolution on get_research_bundle (size / MIME / errors / depth)")
        print("=" * 78)

        bundle_result = await client.call_tool("get_research_bundle", {})
        print_blocks("tool result as returned", bundle_result.content)

        resolver = HardenedLinkResolver(LinkResolutionOptions(
            max_resource_bytes=16 * 1024,             # big_dataset declares 64 000 bytes -> rejected unread
            allowed_mime_prefixes=["text/", "application/json"],  # audio/wav -> filtered out
            max_depth=2,                              # chain a -> b -> c trips the guard at hop 3
        ))
        print_blocks("after hardened resolution", await resolver.resolve(bundle_result.content, client))


# §3.3.4 book extract: the core is a substitution pass over the tool result's content
# blocks. Links must be resolved against the server that returned them — never a
# different one (the origin-binding rule from §3.3.3).
async def resolve_links(
    content: list[ContentBlock], client: Client
) -> list[ContentBlock]:
    resolved: list[ContentBlock] = []
    for block in content:
        if isinstance(block, ResourceLink):
            read = await client.read_resource(block.uri)
            resolved.extend(to_content_block(c) for c in read.contents)
        else:
            resolved.append(block)
    return resolved


# Read contents are text|blob; content blocks are typed. The C# SDK converts via
# ToAIContent().ToContentBlock(); the Python SDK has no equivalent helper, so the
# mapping (text -> text block, blob -> image/audio block by MIME type) is spelled out.
def to_content_block(contents: ResourceContents) -> ContentBlock:
    if isinstance(contents, TextResourceContents):
        return TextContent(type="text", text=contents.text)
    assert isinstance(contents, BlobResourceContents)
    mime_type = contents.mime_type or "application/octet-stream"
    if mime_type.startswith("image/"):
        return ImageContent(type="image", data=contents.blob, mime_type=mime_type)
    if mime_type.startswith("audio/"):
        return AudioContent(type="audio", data=contents.blob, mime_type=mime_type)
    return TextContent(type="text", text=f"[binary content: {mime_type}]")


@dataclass
class LinkResolutionOptions:
    # Budget per linked resource, checked against the link's declared size before
    # reading and against the actual content after.
    max_resource_bytes: int = 16 * 1024

    # MIME-type prefixes the target model can actually consume.
    allowed_mime_prefixes: list[str] = field(default_factory=lambda: ["text/"])

    # Maximum link-chain depth. Links in the tool result resolve at depth 1;
    # a link found inside resolved content resolves one level deeper.
    max_depth: int = 2


# What the book page leaves out (§3.3.4): a resource link can point at ANYTHING, so
# production resolution needs a size budget, MIME filtering for model compatibility,
# error handling for failed reads, and a depth guard against link chains. Where a
# guard drops a link, an explanatory text block takes its place so the model knows
# what happened instead of silently losing context.
class HardenedLinkResolver:
    def __init__(self, options: LinkResolutionOptions) -> None:
        self._options = options

    async def resolve(
        self, content: list[ContentBlock], client: Client
    ) -> list[ContentBlock]:
        visited: set[str] = set()  # cycle detection across the whole pass
        return await self._resolve_at_depth(content, client, depth=1, visited=visited)

    async def _resolve_at_depth(
        self, content: list[ContentBlock], client: Client, depth: int, visited: set[str]
    ) -> list[ContentBlock]:
        resolved: list[ContentBlock] = []
        for block in content:
            if isinstance(block, ResourceLink):
                resolved.extend(await self._resolve_one_link(block, client, depth, visited))
            else:
                resolved.append(block)
        return resolved

    async def _resolve_one_link(
        self, link: ResourceLink, client: Client, depth: int, visited: set[str]
    ) -> list[ContentBlock]:
        options = self._options
        print(f"  [resolve] {link.uri} (depth {depth})")

        # Depth guard: link chains (and cycles) must terminate.
        if depth > options.max_depth:
            return _drop(link, f"chain depth {depth} exceeds the maximum of {options.max_depth}")
        if link.uri in visited:
            return _drop(link, "link cycle detected — this URI was already resolved in this pass")
        visited.add(link.uri)

        # Size budget, part 1: a declared size lets us reject without reading at all.
        if link.size is not None and link.size > options.max_resource_bytes:
            return _drop(
                link,
                f"declared size {link.size} exceeds the budget of {options.max_resource_bytes} bytes",
            )

        # MIME filter, part 1: a declared type lets us skip content the model can't take.
        if link.mime_type is not None and not self._mime_allowed(link.mime_type):
            return _drop(link, f"declared MIME type '{link.mime_type}' is not model-compatible")

        # Error handling: a link is a promise the server doesn't have to keep.
        try:
            read = await client.read_resource(link.uri)
        except MCPError as error:
            return _drop(link, f"read failed: {error}")

        resolved: list[ContentBlock] = []
        for contents in read.contents:
            # MIME filter and size budget, part 2: links may omit metadata, so re-check
            # what the read actually returned.
            actual_mime = contents.mime_type or link.mime_type
            if actual_mime is not None and not self._mime_allowed(actual_mime):
                resolved.extend(
                    _drop(link, f"content MIME type '{actual_mime}' is not model-compatible")
                )
                continue
            if isinstance(contents, TextResourceContents):
                actual_size = len((contents.text or "").encode("utf-8"))
            else:
                # non-text got past the filter without a type: don't inject it
                actual_size = options.max_resource_bytes + 1
            if actual_size > options.max_resource_bytes:
                resolved.extend(
                    _drop(
                        link,
                        f"content size {actual_size} exceeds the budget of "
                        f"{options.max_resource_bytes} bytes",
                    )
                )
                continue

            # Chain convention: a read result cannot carry a resource link natively
            # (contents are text|blob only), but some servers tunnel an onward link as
            # JSON content. Follow it — that is exactly what the depth guard is for.
            if (
                isinstance(contents, TextResourceContents)
                and actual_mime == "application/json"
                and '"resource_link"' in contents.text
                and (onward_link := _try_parse_link(contents.text)) is not None
            ):
                resolved.extend(
                    await self._resolve_one_link(onward_link, client, depth + 1, visited)
                )
                continue

            resolved.append(to_content_block(contents))
        return resolved

    def _mime_allowed(self, mime_type: str) -> bool:
        return any(mime_type.startswith(prefix) for prefix in self._options.allowed_mime_prefixes)


def _try_parse_link(json_text: str) -> ResourceLink | None:
    try:
        parsed = json.loads(json_text)
        if isinstance(parsed, dict) and parsed.get("type") == "resource_link":
            return ResourceLink.model_validate(parsed)
        return None
    except (ValueError, TypeError):
        return None


# Replace a dropped link with an explanation the model can see and act on.
def _drop(link: ResourceLink, reason: str) -> list[ContentBlock]:
    print(f"  [guard]   {link.uri}: {reason}")
    return [
        TextContent(
            type="text",
            text=f"[resource link '{link.name}' ({link.uri}) was not resolved: {reason}]",
        )
    ]


def print_blocks(label: str, blocks: list[ContentBlock]) -> None:
    print(f"--- {label} ({len(blocks)} block(s)) ---")
    continuation = "\n" + " " * 17
    for block in blocks:
        if isinstance(block, ResourceLink):
            line = f"resource_link  uri={block.uri}  name={block.name}"
            if block.mime_type is not None:
                line += f"  mimeType={block.mime_type}"
            if block.size is not None:
                line += f"  size={block.size}"
        elif isinstance(block, TextContent):
            line = f"text           {truncate(block.text, 140)}"
        elif isinstance(block, ImageContent):
            line = f"image          mimeType={block.mime_type}"
        elif isinstance(block, AudioContent):
            line = f"audio          mimeType={block.mime_type}"
        else:
            line = block.type
        print(f"  {line.replace(chr(10), continuation)}")
    print()


def truncate(text: str, max_length: int) -> str:
    return text if len(text) <= max_length else f"{text[:max_length]}… ({len(text)} chars total)"


# Locate the demo server next to this sample, or wherever DEMO_RESOURCE_SERVER_PY points.
def resolve_server_script() -> str:
    if override := os.environ.get("DEMO_RESOURCE_SERVER_PY"):
        return override
    candidate = (
        Path(__file__).resolve().parent.parent.parent
        / "demo-resource-server" / "python" / "server.py"
    )
    if not candidate.exists():
        raise FileNotFoundError(
            f"server.py not found at {candidate}. Point DEMO_RESOURCE_SERVER_PY at "
            "chapter03/demo-resource-server/python/server.py."
        )
    return str(candidate)


if __name__ == "__main__":
    asyncio.run(main())
