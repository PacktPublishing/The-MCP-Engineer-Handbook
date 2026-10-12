# Host-side policy for the actions the host lets the model request. There is one,
# share_resource(uri, destination), and the host only shares with destinations on its
# allowlist. Nothing in the context changes that: not the resource's text, not the
# provenance tags, not how confident the model sounds. A real host advertises the
# action as a tool and executes it only after authorize allows it.
from urllib.parse import urlsplit

from scripted_chat_model import ToolCallPart

ALLOWED_SHARE_HOSTS = {"notes.nimbus.example"}


def authorize(call: ToolCallPart) -> str:
    if call.name != "share_resource":
        return f"DENIED {describe(call)}: not an action this host offers."

    target = urlsplit(str(call.arguments.get("destination", "")))
    if target.scheme != "https" or target.hostname not in ALLOWED_SHARE_HOSTS:
        return f"DENIED {describe(call)}: destination is not on the share allowlist."

    return f"ALLOWED {describe(call)}"


def describe(call: ToolCallPart) -> str:
    return f"{call.name}({', '.join(f'{k}: {v}' for k, v in call.arguments.items())})"
