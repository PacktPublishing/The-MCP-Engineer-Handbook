"""Completions server (section 5.3.3).

Declares the `completions` capability and answers `completion/complete`
requests for the `path` argument of the `file:///{path}` resource template.
The handler mirrors the chapter extract: prefix-match against a known list,
cap at the spec limit of 100 values.
"""

from mcp.server import MCPServer
from mcp_types import Completion, ResourceTemplateReference

server = MCPServer("completions-demo")

# The catalog the completion handler completes against. A real server would
# consult its actual resource space (and should fuzzy-match, rate-limit, and
# keep sensitive paths out — see the chapter's guidance).
KNOWN_PATHS = [
    "docs/readme.md",
    "docs/reference.md",
    "docs/release-notes.md",
    "docs/setup.md",
    "src/main.py",
    "src/utils.py",
    "tests/test_main.py",
]


@server.resource("file:///{path}")
def read_file(path: str) -> str:
    """Read a project file by path."""
    if path not in KNOWN_PATHS:
        raise ValueError(f"unknown path: {path}")
    return f"Contents of {path}"


@server.completion()
async def handle_completion(ref, argument, context):
    # Only handle our file resource template
    if isinstance(ref, ResourceTemplateReference) and ref.uri == "file:///{path}":
        if argument.name == "path":
            prefix = argument.value
            # Filter paths matching the partial input
            matches = [p for p in KNOWN_PATHS if p.startswith(prefix)]
            # Spec allows max 100 values
            capped = matches[:100]
            return Completion(
                values=capped,
                total=len(matches),
                has_more=len(matches) > len(capped),
            )
    # None -> the SDK responds with an empty completion
    return None


if __name__ == "__main__":
    server.run()
