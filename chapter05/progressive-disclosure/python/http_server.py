# Streamable HTTP entry point — serves the stateless 2026-07-28 era over HTTP.
# The stdio entry point (server.py) serves both eras, locked per connection.
from server import server

server.run(transport="streamable-http")
