"""Computes a find-and-replace plan and renders it for the model. The plan is
the single source of truth for both the preview text and the execution, so
what was previewed is exactly what runs."""

from __future__ import annotations

from dataclasses import dataclass
from typing import TYPE_CHECKING

if TYPE_CHECKING:
    from document_store import DocumentStore


@dataclass(frozen=True)
class LineEdit:
    line_number: int
    before: str
    after: str
    occurrences: int


@dataclass(frozen=True)
class DocumentEdit:
    document_id: str
    document_version: int
    lines: list[LineEdit]


@dataclass(frozen=True)
class FindReplacePlan:
    find: str
    replace: str
    documents: list[DocumentEdit]

    @property
    def total_lines(self) -> int:
        return sum(len(d.lines) for d in self.documents)

    @property
    def total_occurrences(self) -> int:
        return sum(line.occurrences for d in self.documents for line in d.lines)


def build(store: DocumentStore, find: str, replace: str) -> FindReplacePlan:
    document_edits: list[DocumentEdit] = []
    for doc in store.all():
        line_edits = [
            LineEdit(i + 1, line, line.replace(find, replace), line.count(find))
            for i, line in enumerate(doc.lines)
            if find in line
        ]
        if line_edits:
            document_edits.append(DocumentEdit(doc.id, doc.version, line_edits))

    return FindReplacePlan(find, replace, document_edits)


def render_preview(plan: FindReplacePlan) -> str:
    """The preview: which lines change, and the sequence of internal API
    operations the server will run — the granularity we gave up by folding
    search + edit batch + commit into one logical operation, reintroduced
    where it matters."""
    parts = [
        f'{plan.total_occurrences} occurrence(s) of "{plan.find}" on {plan.total_lines} line(s) '
        f'in {len(plan.documents)} document(s) would be replaced with "{plan.replace}":'
    ]
    for edit in plan.documents:
        parts.append("")
        parts.append(f"{edit.document_id}:")
        for line in edit.lines:
            parts.append(f"  line {line.line_number}:")
            parts.append(f"    - {line.before}")
            parts.append(f"    + {line.after}")

    parts.append("")
    parts.append("Internal operations that execution will run, in order:")
    step = 1
    parts.append(f'  {step}. documents.search("{plan.find}") -> {len(plan.documents)} document(s)')
    step += 1
    parts.append(f"  {step}. edits.create_batch()")
    step += 1
    for edit in plan.documents:
        for line in edit.lines:
            parts.append(f'  {step}. edits.replace_line(document: "{edit.document_id}", line: {line.line_number})')
            step += 1

    parts.append(f"  {step}. edits.commit_batch() -- all edits become visible atomically here")
    return "\n".join(parts) + "\n"


def render_executed(plan: FindReplacePlan) -> str:
    ids = ", ".join(d.document_id for d in plan.documents)
    return (
        f'Replaced {plan.total_occurrences} occurrence(s) of "{plan.find}" with "{plan.replace}" '
        f"on {plan.total_lines} line(s) across {len(plan.documents)} document(s): {ids}. "
        "All edits were applied in one atomic batch. "
        "Call read_document to see the updated content."
    )
