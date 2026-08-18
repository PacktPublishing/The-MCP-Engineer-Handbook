// Computes a find-and-replace plan and renders it for the model. The plan is
// the single source of truth for both the preview text and the execution, so
// what was previewed is exactly what runs.

import type { DocumentStore } from './documentStore.js';

export interface LineEdit {
  lineNumber: number;
  before: string;
  after: string;
  occurrences: number;
}

export interface DocumentEdit {
  documentId: string;
  documentVersion: number;
  lines: LineEdit[];
}

export interface FindReplacePlan {
  find: string;
  replace: string;
  documents: DocumentEdit[];
}

export function totalLines(plan: FindReplacePlan): number {
  return plan.documents.reduce((sum, d) => sum + d.lines.length, 0);
}

export function totalOccurrences(plan: FindReplacePlan): number {
  return plan.documents.reduce((sum, d) => sum + d.lines.reduce((s, l) => s + l.occurrences, 0), 0);
}

export function buildPlan(store: DocumentStore, find: string, replace: string): FindReplacePlan {
  const documentEdits: DocumentEdit[] = [];
  for (const doc of store.all()) {
    const lineEdits: LineEdit[] = [];
    doc.lines.forEach((line, i) => {
      if (!line.includes(find)) return;
      lineEdits.push({
        lineNumber: i + 1,
        before: line,
        after: line.replaceAll(find, replace),
        occurrences: line.split(find).length - 1,
      });
    });

    if (lineEdits.length > 0) {
      documentEdits.push({ documentId: doc.id, documentVersion: doc.version, lines: lineEdits });
    }
  }

  return { find, replace, documents: documentEdits };
}

// The preview: which lines change, and the sequence of internal API
// operations the server will run — the granularity we gave up by folding
// search + edit batch + commit into one logical operation, reintroduced
// where it matters.
export function renderPreview(plan: FindReplacePlan): string {
  const lines: string[] = [];
  lines.push(
    `${totalOccurrences(plan)} occurrence(s) of "${plan.find}" on ${totalLines(plan)} line(s) ` +
      `in ${plan.documents.length} document(s) would be replaced with "${plan.replace}":`,
  );
  for (const edit of plan.documents) {
    lines.push('');
    lines.push(`${edit.documentId}:`);
    for (const line of edit.lines) {
      lines.push(`  line ${line.lineNumber}:`);
      lines.push(`    - ${line.before}`);
      lines.push(`    + ${line.after}`);
    }
  }

  lines.push('');
  lines.push('Internal operations that execution will run, in order:');
  let step = 1;
  lines.push(`  ${step++}. documents.search("${plan.find}") -> ${plan.documents.length} document(s)`);
  lines.push(`  ${step++}. edits.create_batch()`);
  for (const edit of plan.documents) {
    for (const line of edit.lines) {
      lines.push(`  ${step++}. edits.replace_line(document: "${edit.documentId}", line: ${line.lineNumber})`);
    }
  }

  lines.push(`  ${step}. edits.commit_batch() -- all edits become visible atomically here`);
  return lines.join('\n') + '\n';
}

export function renderExecuted(plan: FindReplacePlan): string {
  const ids = plan.documents.map((d) => d.documentId).join(', ');
  return (
    `Replaced ${totalOccurrences(plan)} occurrence(s) of "${plan.find}" with "${plan.replace}" ` +
    `on ${totalLines(plan)} line(s) across ${plan.documents.length} document(s): ${ids}. ` +
    'All edits were applied in one atomic batch. ' +
    'Call read_document to see the updated content.'
  );
}
