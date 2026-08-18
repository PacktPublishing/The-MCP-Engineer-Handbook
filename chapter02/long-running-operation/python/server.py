# LongRunningOperation — splitting a slow domain operation across multiple tools
# (Chapter 2, §6.4). start_data_processing and start_search return immediately with
# an operation ID; the model threads that ID through check_operation_status and can
# recover lost IDs with list_all_operations.
import os
import random
import time
from dataclasses import dataclass

from mcp.server import MCPServer
from pydantic import BaseModel

server = MCPServer(name="long-running-operation", version="0.1.0")

# How long a started operation takes before it is "done".
# Scaled down for the demo; the tool descriptions below are scaled to match.
COMPLETION_DELAY_SECONDS = float(os.environ.get("OPERATION_DELAY_SECONDS", "4"))


# A long-running operation. Completion is derived from the clock — no background workers.
@dataclass
class Operation:
    operation_id: str
    kind: str
    input: str
    started_at: float
    completes_at: float
    result: str

    @property
    def is_completed(self) -> bool:
        return time.time() >= self.completes_at


# Status report returned by check_operation_status (also published as the tool's
# output schema). Field names are camelCase so the wire contract matches the C# port.
class OperationStatus(BaseModel):
    operationId: str
    kind: str
    status: str
    result: str | None
    guidance: str | None


# In-memory store keyed by the operation handle. In production this would be
# durable storage (a database or task queue): the stateless design rules from
# Chapter 5 mean any replica may receive the status poll, so the state must
# live somewhere every replica can reach — not in process memory.
_operations: dict[str, Operation] = {}


def _start_operation(kind: str, input: str, result_template: str) -> Operation:
    # Short, distinctive handles: the model has to reproduce them verbatim,
    # so op_3f9c beats a 36-character UUID (Chapter 2, Section 6.3).
    while True:
        op = Operation(
            operation_id=f"op_{random.randrange(0x10000):04x}",
            kind=kind,
            input=input,
            started_at=time.time(),
            completes_at=time.time() + COMPLETION_DELAY_SECONDS,
            result=result_template,
        )
        if op.operation_id not in _operations:
            _operations[op.operation_id] = op
            return op


@server.tool(
    name="start_data_processing",
    description="Start an asynchronous data-processing job on the named dataset. "
    "Returns immediately with an operation ID — it does NOT wait for the job to finish. "
    "Processing typically takes 3-6 seconds. Poll check_operation_status with the "
    "returned operation ID; do not poll more than once every 2 seconds.",
    structured_output=False,
)
def start_data_processing(dataset: str) -> str:
    op = _start_operation(
        "data_processing",
        dataset,
        result_template=f"Dataset '{dataset}' processed: 1204 rows read, "
        "1187 rows transformed, 17 rows rejected (schema mismatch).",
    )
    return (
        f"Started data processing for dataset '{dataset}'. Operation ID: {op.operation_id}. "
        "Typically completes in 3-6 seconds. Check progress with check_operation_status, "
        "waiting at least 2 seconds between checks."
    )


@server.tool(
    name="start_search",
    description="Start an asynchronous deep search across the archive for the given query. "
    "Returns immediately with an operation ID — it does NOT wait for results. "
    "Searches typically take 3-6 seconds. Poll check_operation_status with the "
    "returned operation ID; do not poll more than once every 2 seconds.",
    structured_output=False,
)
def start_search(query: str) -> str:
    op = _start_operation(
        "search",
        query,
        result_template=f"Search for '{query}' finished: 3 matching documents — "
        "'Q3 capacity plan' (0.92), 'Incident 4411 retro' (0.87), 'Archive index 2024' (0.71).",
    )
    return (
        f"Started search for '{query}'. Operation ID: {op.operation_id}. "
        "Typically completes in 3-6 seconds. Check progress with check_operation_status, "
        "waiting at least 2 seconds between checks."
    )


@server.tool(
    name="check_operation_status",
    description="Check the status of an operation previously started with start_data_processing "
    "or start_search. Requires the operation ID those tools returned. Reports 'running' or "
    "'completed', and includes the result once completed. If still running, wait at least "
    "2 seconds before checking again.",
)
def check_operation_status(operationId: str) -> OperationStatus:
    op = _operations.get(operationId)
    if op is None:
        # Instructive text for unknown handles: tell the model how to recover,
        # not just that it failed.
        return OperationStatus(
            operationId=operationId,
            kind="unknown",
            status="not_found",
            result=None,
            guidance=f"No operation with ID '{operationId}' exists on this server. Operation IDs "
            "are returned by start_data_processing and start_search. Call "
            "list_all_operations to see every operation this server knows about.",
        )

    if op.is_completed:
        return OperationStatus(
            operationId=op.operation_id, kind=op.kind, status="completed",
            result=op.result, guidance=None,
        )
    return OperationStatus(
        operationId=op.operation_id, kind=op.kind, status="running", result=None,
        guidance="Still running. Wait at least 2 seconds before checking again.",
    )


@server.tool(
    name="list_all_operations",
    description="List every operation this server knows about, with its current status. "
    "Use this to recover an operation ID you no longer have, or to get an overview "
    "of running and completed operations.",
    structured_output=False,
)
def list_all_operations() -> str:
    if not _operations:
        return "No operations have been started yet. Start one with start_data_processing or start_search."

    return "\n".join(
        f"{op.operation_id}  kind={op.kind}  "
        f"status={'completed' if op.is_completed else 'running'}  input='{op.input}'"
        for op in sorted(_operations.values(), key=lambda op: op.started_at)
    )


if __name__ == "__main__":
    server.run()
