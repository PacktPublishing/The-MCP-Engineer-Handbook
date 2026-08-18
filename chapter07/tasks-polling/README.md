# tasks-polling

Companion sample for **Chapter 7, §7.3 (The Tasks Extension)** — a minimal C# demo of the client-side task methods.

The server (`csharp/`) has one slow tool, `generate_report`. Its entire tasks opt-in is one line: setting `McpServerOptions.TaskStore` (here `InMemoryMcpTaskStore`) makes the SDK declare `io.modelcontextprotocol/tasks`, serve `tasks/get`/`tasks/update`/`tasks/cancel` from the store, and answer task-declaring clients with `resultType: "task"` while the tool runs in the background; `tasks/cancel` surfaces as the tool's `CancellationToken`.

The client (`csharp-client/`) walks the lifecycle:

1. `CallToolRawAsync` — declares the extension on the request (no client capability configuration needed; the SDK injects it into the per-request `_meta`) and returns `ResultOrCreatedTask<CallToolResult>`: the server, not the client, decided to answer with a task.
2. `GetTaskAsync` in a loop honoring `PollIntervalMs` — a few `Working` polls, then `Completed` with the `CallToolResult` inlined in the snapshot (there is no `tasks/result`).
3. `CancelTaskAsync` on a second, long task — cooperative intent, confirmed `Cancelled` by a follow-up `tasks/get`.
4. Plain `CallToolAsync` — the transparent path: it declares the extension and polls any task handle to completion internally. (Its polling helper, `PollTaskToCompletionAsync`, is private in preview.1 — the manual loop in step 2 is the public way to observe the lifecycle.)

## Run

From this directory: `dotnet run --project csharp-client` (it spawns the server itself via `dotnet run --project csharp`; pass a different server project path as the first argument if running from elsewhere).
