"""Completions client (section 5.3.3).

Connects in-process to the sample server and requests completions for a
partial `path` value, exactly as the chapter extract does.
"""

import asyncio

from mcp.client import Client
from mcp_types import ResourceTemplateReference

from server import server


async def main() -> None:
    async with Client(server) as session:
        # Request completions for the "path" argument
        result = await session.complete(
            ref=ResourceTemplateReference(uri="file:///{path}"),
            argument={"name": "path", "value": "docs/re"},
        )
        completion = result.completion
        print("values:", completion.values)
        print("total:", completion.total, "hasMore:", completion.has_more)


if __name__ == "__main__":
    asyncio.run(main())
