# The two wrapper tools from §3.3.3: MCP resources have no model-native integration
# point, so the host creates one — list_resources and read_resource, aggregating across
# every connected server. The methods are the book extract; the class, the metadata
# shape, and the two format helpers are the plumbing the extract elides.
from __future__ import annotations

from contextlib import AsyncExitStack
from typing import Any

from mcp.client import Client
from mcp.client.stdio import StdioServerParameters, stdio_client
from mcp_types import BlobResourceContents, ReadResourceResult, Resource, TextResourceContents


class ResourceTools:
    def __init__(self, client_manager: "ClientManager") -> None:
        self._client_manager = client_manager

    async def list_resources(self) -> list[dict[str, Any]]:
        """Lists available resources from connected servers."""
        resource_metadata: list[dict[str, Any]] = []
        for server_name in self._client_manager.get_server_names():
            client = self._client_manager.get_client(server_name)
            resources = (await client.list_resources()).resources
            resource_metadata.extend(_format_resource_metadata(resources, server_name))
        return resource_metadata

    async def read_resource(self, server_name: str, uri: str) -> str:
        """Reads a resource by server name and URI."""
        result = await self._client_manager.get_client(server_name).read_resource(uri)
        return _format_resource_content(result)


# Tag every entry with the HOST-ASSIGNED server name so the model can route reads —
# and so two servers with colliding URIs (like the two spawns in this demo) stay apart.
def _format_resource_metadata(
    resources: list[Resource], server_name: str
) -> list[dict[str, Any]]:
    return [
        {
            "serverName": server_name,
            "uri": r.uri,
            "name": r.name,
            "title": r.title,
            "description": r.description,
            "mimeType": r.mime_type,
        }
        for r in resources
    ]


# Models (and many chat APIs) only take text from tools; flatten accordingly.
def _format_resource_content(result: ReadResourceResult) -> str:
    lines: list[str] = []
    for contents in result.contents:
        if isinstance(contents, TextResourceContents):
            lines.append(contents.text)
        elif isinstance(contents, BlobResourceContents):
            lines.append(f"[binary content: {contents.mime_type or 'unknown type'}]")
        else:
            lines.append(f"[unsupported content type: {type(contents).__name__}]")
    return "\n".join(lines)


# Owns one Client per connected server, keyed by a label the HOST assigns. Never key
# on the server's self-reported name — identity claims from the server are not trusted
# input for routing decisions (§3.3.3).
class ClientManager:
    def __init__(self) -> None:
        self._clients: dict[str, Client] = {}
        self._stack = AsyncExitStack()

    async def connect(self, server_name: str, params: StdioServerParameters) -> None:
        self._clients[server_name] = await self._stack.enter_async_context(
            Client(stdio_client(params))
        )

    def get_server_names(self) -> list[str]:
        return list(self._clients)

    def get_client(self, server_name: str) -> Client:
        if server_name not in self._clients:
            raise ValueError(f"No connected server is labeled '{server_name}'.")
        return self._clients[server_name]

    async def aclose(self) -> None:
        await self._stack.aclose()
        self._clients.clear()
