#!/usr/bin/env bash
# Spawns two upstream MCP servers (chapter05/csharp-filters) and the proxy,
# runs the smoke sequence, prints the proxy's log, and tears everything down.
set -euo pipefail

DIR="$(cd "$(dirname "$0")" && pwd)"
UPSTREAM="$DIR/../../chapter05/csharp-filters"
PROXY="$DIR/csharp"
LOGDIR="$(mktemp -d)"
PIDS=()
cleanup() { kill "${PIDS[@]}" 2>/dev/null || true; }
trap cleanup EXIT

dotnet build "$UPSTREAM" >/dev/null
dotnet build "$PROXY" >/dev/null

ASPNETCORE_URLS=http://localhost:5101 dotnet run --no-build --project "$UPSTREAM" >"$LOGDIR/upstream-A.log" 2>&1 & PIDS+=($!)
ASPNETCORE_URLS=http://localhost:5102 dotnet run --no-build --project "$UPSTREAM" >"$LOGDIR/upstream-B.log" 2>&1 & PIDS+=($!)
ASPNETCORE_URLS=http://localhost:5100 dotnet run --no-build --project "$PROXY"    >"$LOGDIR/proxy.log"     2>&1 & PIDS+=($!)

for port in 5100 5101 5102; do
  until curl -s -o /dev/null "http://localhost:$port/"; do sleep 0.3; done
done

# Stay inside one budget window: wait out the tail of the current minute.
SEC=$(date +%S); [ "$SEC" -gt 35 ] && sleep $((62 - SEC))

P=http://localhost:5100/
H=(-s -X POST -H 'Content-Type: application/json' \
   -H 'Accept: application/json, text/event-stream' \
   -H 'MCP-Protocol-Version: 2026-07-28')
LIST='{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{"_meta":{"io.modelcontextprotocol/clientInfo":{"name":"smoke","version":"0"},"io.modelcontextprotocol/protocolVersion":"2026-07-28"}}}'
CALL='{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"get_document","arguments":{"id":"roadmap"},"_meta":{"io.modelcontextprotocol/protocolVersion":"2026-07-28"}}}'
ADMIN='{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"admin_delete_document","arguments":{"id":"roadmap"},"_meta":{"io.modelcontextprotocol/protocolVersion":"2026-07-28"}}}'

echo "--- tools/list through the proxy (discovery is free)"
curl "${H[@]}" -H 'Mcp-Method: tools/list' -H 'X-Principal: alice' -d "$LIST" $P | head -c 120; echo

echo "--- alice: get_document -> default pool (5101)"
curl "${H[@]}" -H 'Mcp-Method: tools/call' -H 'Mcp-Name: get_document' -H 'X-Principal: alice' -d "$CALL" $P | grep -o '"text":"[^"]*"'

echo "--- alice: admin_delete_document -> dedicated pool (5102)"
curl "${H[@]}" -H 'Mcp-Method: tools/call' -H 'Mcp-Name: admin_delete_document' -H 'X-Principal: alice' -d "$ADMIN" $P | grep -o '"text":"[^"]*"'

echo "--- alice: three more calls, spending the budget"
for i in 3 4 5; do
  curl "${H[@]}" -H 'Mcp-Method: tools/call' -H 'Mcp-Name: get_document' -H 'X-Principal: alice' -d "$CALL" $P -o /dev/null -w "call $i: HTTP %{http_code}\n"
done

echo "--- alice: call 6, budget exhausted"
curl "${H[@]}" -H 'Mcp-Method: tools/call' -H 'Mcp-Name: get_document' -H 'X-Principal: alice' -d "$CALL" $P -w "\nHTTP %{http_code}\n"

echo "--- bob: unaffected"
curl "${H[@]}" -H 'Mcp-Method: tools/call' -H 'Mcp-Name: get_document' -H 'X-Principal: bob' -d "$CALL" $P -o /dev/null -w "bob: HTTP %{http_code}\n"

echo; echo "--- proxy log (one line per request, at the seam)"
grep -A1 'info: McpProxy' "$LOGDIR/proxy.log" | grep -v '^--' | paste - - | sed 's/info: McpProxy\[0\][[:space:]]*//'
