import type { FindReplacePlan } from './findReplacePlanner.js';

// A document in the store. version is bumped on every write; previews record
// the version they were computed against so a stale preview is detectable.
export interface Document {
  id: string;
  lines: string[];
  version: number;
}

// In-memory stand-in for the document service this MCP server wraps.
export class DocumentStore {
  private readonly documents = new Map<string, Document>(seed().map((d) => [d.id, d]));

  all(): Document[] {
    return [...this.documents.values()].sort((a, b) => (a.id < b.id ? -1 : a.id > b.id ? 1 : 0));
  }

  get(id: string): Document | undefined {
    return this.documents.get(id);
  }

  // Applies a plan atomically. Returns null on success, or the id of the
  // first document that changed since the plan was computed (stale plan —
  // nothing is applied in that case).
  tryApply(plan: FindReplacePlan): string | null {
    for (const edit of plan.documents) {
      const doc = this.documents.get(edit.documentId);
      if (doc === undefined || doc.version !== edit.documentVersion) return edit.documentId;
    }

    for (const edit of plan.documents) {
      const doc = this.documents.get(edit.documentId)!;
      for (const line of edit.lines) doc.lines[line.lineNumber - 1] = line.after;
      doc.version++;
    }

    return null;
  }
}

function seed(): Document[] {
  return [
    {
      id: 'onboarding-guide',
      version: 1,
      lines: [
        'Welcome to the team! This guide covers your first week.',
        'Your first task is to install the Aurora CLI and sign in.',
        'Aurora sign-in issues go to the platform team.',
        'All services deploy through the standard pipeline to staging first.',
        'Your onboarding buddy will schedule a walkthrough on day two.',
      ],
    },
    {
      id: 'release-checklist',
      version: 1,
      lines: [
        '1. Confirm the changelog is complete and reviewed.',
        '2. Run the full Aurora test suite against staging.',
        '3. Tag the release and update the Aurora version manifest.',
        '4. Announce the release in #announcements.',
      ],
    },
    {
      id: 'support-runbook',
      version: 1,
      lines: [
        'Check the status page before triaging any report.',
        'Auth incidents: restart the token service first.',
        'Aurora ingestion lag above 5 minutes pages the on-call engineer.',
        'Escalate unresolved incidents after 30 minutes.',
      ],
    },
    {
      id: 'style-guide',
      version: 1,
      lines: [
        'Write short sentences in the active voice.',
        'Prefer numbered steps for procedures.',
        'Screenshots must include alt text.',
      ],
    },
    {
      id: 'team-charter',
      version: 1,
      lines: [
        'We ship small changes frequently.',
        'Every change is reviewed by at least one other engineer.',
        'Documentation updates ship with the change, not after.',
      ],
    },
  ];
}
