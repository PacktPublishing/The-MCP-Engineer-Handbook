# A stand-in for any long-running backend: start_report returns immediately with a
# handle, the actual work finishes a few seconds later. With the Tasks extension the
# server would return a CreateTaskResult instead of a handle-in-content, but the
# host-side injection problem this sample demonstrates is identical either way.
import asyncio
import json
import os

from mcp.server import MCPServer

server = MCPServer(name="report-server", version="0.1.0")

# None = still running; str = the finished report.
_results: dict[str, str | None] = {}
_counter = 0
_pending: set[asyncio.Task[None]] = set()

DELAY_SECONDS = float(os.environ.get("REPORT_DELAY_SECONDS", "3"))


@server.tool()
async def start_report(topic: str) -> str:
    """Start generating a report. Returns immediately with an operation handle; the report completes a few seconds later.

    Args:
        topic: What the report should cover.
    """
    global _counter
    _counter += 1
    operation_id = f"op-{_counter:03d}"
    _results[operation_id] = None

    async def finish() -> None:
        await asyncio.sleep(DELAY_SECONDS)
        _results[operation_id] = (
            f"Report on {topic}: revenue up 14% quarter over quarter, "
            "driven by the new subscription tier. Two regions missed target; details in the appendix."
        )

    task = asyncio.create_task(finish())
    _pending.add(task)
    task.add_done_callback(_pending.discard)
    return json.dumps({"operationId": operation_id, "status": "running"})


# The parameter is camelCase so the tool contract on the wire is identical to the
# C# and TypeScript ports.
@server.tool()
def get_report_result(operationId: str) -> str:
    """Fetch the result of a previously started report. Returns status 'running' until it completes.

    Args:
        operationId: Handle returned by start_report.
    """
    if operationId not in _results:
        return json.dumps({"operationId": operationId, "status": "unknown"})
    report = _results[operationId]
    if report is None:
        return json.dumps({"operationId": operationId, "status": "running"})
    return json.dumps({"operationId": operationId, "status": "completed", "report": report})


if __name__ == "__main__":
    server.run()
