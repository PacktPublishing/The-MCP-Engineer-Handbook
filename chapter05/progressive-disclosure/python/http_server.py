# Streamable HTTP entry point — the 2026-07-28 stateless era in mcp 2.0.0b1
# works over HTTP only; stdio (server.py) still negotiates the legacy handshake.
from server import server

server.run(transport="streamable-http")
