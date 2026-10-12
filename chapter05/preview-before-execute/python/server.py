"""Find-and-replace as a logical operation, with preview-before-execute in
both of the chapter's variants: a dry_run parameter on the combined tool, and
a separate preview tool whose token gates execution. Failures are isError
results whose text states what did not happen and what to do instead —
positive instructions the model can act on, and a flag a program can check
without parsing English."""

from typing import Annotated

from mcp.server import MCPServer
from mcp_types import CallToolResult, TextContent
from pydantic import Field

import find_replace_planner as planner
from document_store import DocumentStore
from preview_store import TIME_TO_LIVE_SECONDS, PreviewStore, RedeemResult

server = MCPServer(name="preview-before-execute", version="0.1.0")

documents = DocumentStore()
previews = PreviewStore()

TTL_MINUTES = TIME_TO_LIVE_SECONDS // 60


def text(s: str) -> CallToolResult:
    return CallToolResult(content=[TextContent(type="text", text=s)])


def error_text(s: str) -> CallToolResult:
    return CallToolResult(content=[TextContent(type="text", text=s)], is_error=True)


# --- Variant 1: one tool with a dry_run parameter ---------------------------


@server.tool(
    name="find_and_replace",
    description=(
        "Replaces every occurrence of 'find' with 'replace' across all documents, applied as one atomic edit batch. "
        "This is a write operation that modifies documents. "
        "Set dry_run to true to see which lines would change and which internal operations would run, without changing anything. "
        "A dry run is advisory: a later call with dry_run false recomputes the changes against the documents as they are then."
    ),
    structured_output=False,
)
def find_and_replace(
    find: Annotated[str, Field(description="Exact text to search for (case-sensitive).")],
    replace: Annotated[str, Field(description="Text to replace each occurrence with.")],
    dry_run: Annotated[
        bool,
        Field(description="If true, make no changes; return the would-be changes and the internal operations instead."),
    ] = False,
) -> CallToolResult:
    if len(find) == 0:
        return error_text("No changes were made: 'find' must be non-empty. Provide the exact text to search for.")

    plan = planner.build(documents, find, replace)
    if len(plan.documents) == 0:
        return text(
            f'No occurrences of "{find}" found in any document, so there is nothing to replace. '
            "No changes were made. Call list_documents and read_document to inspect the current content."
        )

    if dry_run:
        return text(
            "Dry run — no changes were made.\n\n" + planner.render_preview(plan)
            + "\nTo apply these changes, call find_and_replace again with the same find and replace and dry_run: false. "
            "That call recomputes the changes, so if the documents change in the meantime it applies whatever matches then; "
            "use preview_find_and_replace instead to bind execution to this exact plan."
        )

    stale_document = documents.try_apply(plan)
    if stale_document is not None:
        return error_text(
            f'No changes were made: document "{stale_document}" changed while the operation was being prepared. Retry the call.'
        )

    return text(planner.render_executed(plan))


# --- Variant 2: separate preview and execute tools, linked by a token -------


@server.tool(
    name="preview_find_and_replace",
    description=(
        "Previews a find-and-replace across all documents: which lines would change and which internal operations would run. "
        "Makes no changes. Returns a preview_token — passing it to execute_find_and_replace is the only way to apply the changes."
    ),
    structured_output=False,
)
def preview_find_and_replace(
    find: Annotated[str, Field(description="Exact text to search for (case-sensitive).")],
    replace: Annotated[str, Field(description="Text to replace each occurrence with.")],
) -> CallToolResult:
    if len(find) == 0:
        return error_text("No preview was created: 'find' must be non-empty. Provide the exact text to search for.")

    plan = planner.build(documents, find, replace)
    if len(plan.documents) == 0:
        return text(
            f'No occurrences of "{find}" found in any document, so there is nothing to replace and no preview token was issued. '
            "Call list_documents and read_document to inspect the current content."
        )

    token = previews.add(plan)
    return text(
        f"Preview {token} created — no changes have been made yet.\n\n" + planner.render_preview(plan)
        + f'\nTo apply these changes, call execute_find_and_replace with preview_token "{token}". '
        + f"The token is single-use and expires in {TTL_MINUTES} minutes."
    )


@server.tool(
    name="execute_find_and_replace",
    description=(
        "Applies the changes described by a preview created with preview_find_and_replace. "
        "Requires that preview's preview_token; call preview_find_and_replace first to review the changes and obtain the token."
    ),
    structured_output=False,
)
def execute_find_and_replace(
    # Optional in the schema on purpose: a call without the token gets an
    # instructive result steering the model to the preview tool, instead of a
    # generic missing-argument error.
    preview_token: Annotated[
        str | None,
        Field(description="Token returned by preview_find_and_replace for the plan to apply."),
    ] = None,
) -> CallToolResult:
    if preview_token is None or preview_token.strip() == "":
        return error_text(
            "No changes were made. execute_find_and_replace applies a previously previewed plan and needs its preview_token. "
            "Call preview_find_and_replace with your find and replace text first — it shows exactly what will change and returns the token to pass here."
        )

    result, plan = previews.redeem(preview_token)
    if result is RedeemResult.NOT_FOUND:
        return error_text(
            f'No changes were made. Preview token "{preview_token}" was not found — tokens are single-use and expire after '
            f"{TTL_MINUTES} minutes. Call preview_find_and_replace to create a fresh preview and token."
        )
    if result is RedeemResult.EXPIRED:
        return error_text(
            f'No changes were made. Preview "{preview_token}" has expired (previews are valid for '
            f"{TTL_MINUTES} minutes). Call preview_find_and_replace again to re-review the changes and get a fresh token."
        )

    stale_document = documents.try_apply(plan)
    if stale_document is not None:
        return error_text(
            f'No changes were made. Document "{stale_document}" was modified after preview "{preview_token}" was created, '
            "so the previewed plan no longer matches the current content. "
            "Call preview_find_and_replace again to review the changes against the current documents."
        )

    return text(f"Preview {preview_token} executed. " + planner.render_executed(plan))


# --- Read-only helpers so the corpus can be inspected ------------------------


@server.tool(
    name="list_documents",
    description="Lists all documents in the store. Read-only.",
    structured_output=False,
)
def list_documents() -> str:
    return "\n".join(f"{d.id} ({len(d.lines)} lines)" for d in documents.all())


@server.tool(
    name="read_document",
    description="Returns the full text of one document. Read-only.",
    structured_output=False,
)
def read_document(
    document_id: Annotated[str, Field(description='Document id, e.g. "onboarding-guide".')],
) -> CallToolResult:
    doc = documents.get(document_id)
    if doc is None:
        return error_text(f'Document "{document_id}" was not found. Call list_documents to see the available ids.')
    return text("\n".join(doc.lines))


if __name__ == "__main__":
    server.run()
