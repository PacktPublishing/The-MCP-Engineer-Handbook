#!/usr/bin/env bash
# Re-runnable checks for the MRTR client loops (book loop and production bounds).
#
#   ./check-bounds.sh [typescript|python|csharp ...]   (default: all three; build each port first)
#
# Scenarios per port: scripted success (manual loop, the book's capped loop, and
# the SDK-native path), decline, retry-budget exhaustion, and the two bounds the
# chapter adds for production code:
#   - deadline: MRTR_TIMEOUT_MS fires while an interactive prompt is open
#   - cancel:   MRTR_CANCEL_AFTER_MS simulates the user pressing cancel mid-form
# For both, stdin stays open (and silent) for 30 s, so a prompt that ignores the
# deadline or the cancel would hang until then; the check requires the failure
# well before that. TypeScript and Python also check that an abandoned prompt
# does not swallow the line typed for the next one (C#: see the README).
set -u
cd "$(dirname "$0")"
pass=0
fail=0

# expect NAME WANT [VAR=VALUE ...] -- COMMAND ...
expect() {
  local name=$1 want=$2 vars=()
  shift 2
  while [[ $1 != -- ]]; do vars+=("$1"); shift; done
  shift
  local out
  out=$( (for v in "${vars[@]}"; do export "$v"; done; "$@") 2>&1 | tail -1)
  if [[ $out == *"$want"* ]]; then echo "ok   $name"; ((pass++)); else echo "FAIL $name: $out"; ((fail++)); fi
}

# expect_open_prompt NAME WANT VAR=VALUE -- COMMAND ...   (stdin held open, silent)
expect_open_prompt() {
  local name=$1 want=$2 var=$3
  shift 4
  local start out took fifo holder
  fifo=$(mktemp -u) && mkfifo "$fifo"
  sleep 30 >"$fifo" 2>/dev/null &  # keeps stdin open and silent
  holder=$!
  start=$(date +%s)
  out=$( (export "$var"; "$@" <"$fifo") 2>&1 | tail -1)
  took=$(($(date +%s) - start))
  kill "$holder" 2>/dev/null
  rm -f "$fifo"
  if [[ $out == *"$want"* && $took -lt 20 ]]; then
    echo "ok   $name (${took}s, incl. start-up and teardown)"; ((pass++))
  else
    echo "FAIL $name (${took}s): $out"; ((fail++))
  fi
}

ts() { (cd typescript && node "dist/$1" "${@:2}"); }
py() { (cd python && uv run --quiet "$1" "${@:2}"); }
cs() { (cd csharp && dotnet MrtrClient/bin/Debug/net10.0/MrtrClient.dll "$@"); }

BOOK='MRTR_ANSWERS=[{"title":"Sprint sync","duration":"30"},{"confirm":true}]'
AGAIN='MRTR_ANSWERS=[{"again":true},{"again":true},{"again":true}]'
DECLINE='MRTR_ANSWERS=[{"action":"decline"}]'

check_port() {
  local p=$1 run manual native
  case $p in
    typescript) run="ts mrtr-client.js"; native="ts native.js"; manual="" ;;
    python) run="py mrtr_client.py"; native="py native.py"; manual="" ;;
    csharp) run="cs --manual"; native="cs"; manual="--manual" ;;
  esac
  echo "== $p"
  expect "$p success (production loop)" "final result: Booked" "$BOOK" -- $run
  [[ $p != csharp ]] && expect "$p success (book loop, capped)" "final result: Booked" "$BOOK" MRTR_UNBOUNDED=1 -- $run
  expect "$p success (SDK-native)" "final result: Booked" "$BOOK" -- $native
  expect "$p decline" "not booked" "$DECLINE" -- $run
  expect "$p retry budget" "budget exhausted after 3 rounds" "$AGAIN" MRTR_MAX_ROUNDS=3 -- ${run%% *} ${run#* } never_satisfied
  expect_open_prompt "$p deadline while a prompt is open" "still gathering input after 1500 ms" MRTR_TIMEOUT_MS=1500 -- $run
  expect_open_prompt "$p cancel while a form is open" "cancelled by the user" MRTR_CANCEL_AFTER_MS=1200 -- $run
  expect_open_prompt "$p deadline while a prompt is open (SDK-native)" "still gathering input after 1500 ms" MRTR_TIMEOUT_MS=1500 -- $native
}

abandon_ts() {
  (sleep 1; echo "Second form"; sleep 0.5) | (cd typescript && node --input-type=module -e "
import { handleInputRequest } from './dist/input-handler.js';
const req = (m) => ({ method: 'elicitation/create', params: { message: m, requestedSchema: { type: 'object', properties: { title: { type: 'string' } }, required: ['title'] } } });
try { await handleInputRequest(req('abandoned'), AbortSignal.timeout(300)); } catch {}
const r = await handleInputRequest(req('next'));
console.log(r.content?.title === 'Second form' ? 'ABANDON-OK' : 'ABANDON-FAIL');") 2>&1 | grep -o 'ABANDON-[A-Z]*'
}

abandon_py() {
  (sleep 1.5; echo "Second form"; sleep 0.5) | (cd python && uv run --quiet python -c "
import anyio
from mcp_types import ElicitRequestFormParams
import input_handler as ih
schema = {'type': 'object', 'properties': {'title': {'type': 'string'}}, 'required': ['title']}
async def main():
    try:
        with anyio.fail_after(0.3):
            await ih.handle_elicitation(ElicitRequestFormParams(message='abandoned', requested_schema=schema))
    except TimeoutError:
        pass
    r = await ih.handle_elicitation(ElicitRequestFormParams(message='next', requested_schema=schema))
    print('ABANDON-OK' if (r.content or {}).get('title') == 'Second form' else 'ABANDON-FAIL')
anyio.run(main)") 2>&1 | grep -o 'ABANDON-[A-Z]*'
}

ports=("$@")
[[ ${#ports[@]} -eq 0 ]] && ports=(typescript python csharp)
for p in "${ports[@]}"; do
  check_port "$p"
  case $p in
    typescript) expect "typescript abandoned prompt keeps later input" ABANDON-OK -- abandon_ts ;;
    python) expect "python abandoned prompt keeps later input" ABANDON-OK -- abandon_py ;;
  esac
done
echo "$pass passed, $fail failed"
[[ $fail -eq 0 ]]
