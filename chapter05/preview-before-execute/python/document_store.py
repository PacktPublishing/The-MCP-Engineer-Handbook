"""In-memory stand-in for the document service this MCP server wraps."""

import threading
from dataclasses import dataclass, field

from find_replace_planner import FindReplacePlan


@dataclass
class Document:
    """A document in the store. version is bumped on every write; previews
    record the version they were computed against so a stale preview is
    detectable."""

    id: str
    lines: list[str]
    version: int = field(default=1)


class DocumentStore:
    def __init__(self) -> None:
        self._gate = threading.Lock()
        self._documents = {d.id: d for d in _seed()}

    def all(self) -> list[Document]:
        with self._gate:
            return sorted(self._documents.values(), key=lambda d: d.id)

    def get(self, document_id: str) -> Document | None:
        with self._gate:
            return self._documents.get(document_id)

    def try_apply(self, plan: FindReplacePlan) -> str | None:
        """Applies a plan atomically. Returns None on success, or the id of the
        first document that changed since the plan was computed (stale plan —
        nothing is applied in that case)."""
        with self._gate:
            for edit in plan.documents:
                doc = self._documents.get(edit.document_id)
                if doc is None or doc.version != edit.document_version:
                    return edit.document_id

            for edit in plan.documents:
                doc = self._documents[edit.document_id]
                for line in edit.lines:
                    doc.lines[line.line_number - 1] = line.after
                doc.version += 1

            return None


def _seed() -> list[Document]:
    return [
        Document(
            id="onboarding-guide",
            lines=[
                "Welcome to the team! This guide covers your first week.",
                "Your first task is to install the Aurora CLI and sign in.",
                "Aurora sign-in issues go to the platform team.",
                "All services deploy through the standard pipeline to staging first.",
                "Your onboarding buddy will schedule a walkthrough on day two.",
            ],
        ),
        Document(
            id="release-checklist",
            lines=[
                "1. Confirm the changelog is complete and reviewed.",
                "2. Run the full Aurora test suite against staging.",
                "3. Tag the release and update the Aurora version manifest.",
                "4. Announce the release in #announcements.",
            ],
        ),
        Document(
            id="support-runbook",
            lines=[
                "Check the status page before triaging any report.",
                "Auth incidents: restart the token service first.",
                "Aurora ingestion lag above 5 minutes pages the on-call engineer.",
                "Escalate unresolved incidents after 30 minutes.",
            ],
        ),
        Document(
            id="style-guide",
            lines=[
                "Write short sentences in the active voice.",
                "Prefer numbered steps for procedures.",
                "Screenshots must include alt text.",
            ],
        ),
        Document(
            id="team-charter",
            lines=[
                "We ship small changes frequently.",
                "Every change is reviewed by at least one other engineer.",
                "Documentation updates ship with the change, not after.",
            ],
        ),
    ]
