// Simulated document-management API backing execute_endpoint.
// A small in-memory store stands in for the real service; each endpoint
// handler returns plausible JSON or throws an ApiError whose message is
// written to guide the model toward a correct retry.

export class ApiError extends Error {}

// The simulation has no authentication; all calls act as this user.
const ACTING_USER = 'user-001';

interface User {
  id: string;
  name: string;
  email: string;
}

interface Group {
  id: string;
  name: string;
  memberIds: string[];
}

interface Doc {
  id: string;
  title: string;
  ownerId: string;
  tags: string[];
  createdAt: string;
  updatedAt: string;
  currentVersion: string;
}

interface Version {
  documentId: string;
  versionId: string;
  authorId: string;
  createdAt: string;
  note: string;
  content: string;
}

type Level = 'read' | 'write' | 'admin';

interface Grant {
  grantId: string;
  documentId: string;
  granteeType: 'user' | 'group';
  granteeId: string;
  level: Level;
}

const users: User[] = [
  { id: 'user-001', name: 'Alice Chen', email: 'alice.chen@example.com' },
  { id: 'user-002', name: 'Bob Martinez', email: 'bob.martinez@example.com' },
  { id: 'user-003', name: 'Carol Okafor', email: 'carol.okafor@example.com' },
  { id: 'user-004', name: 'Dana Kim', email: 'dana.kim@example.com' },
];

const groups: Group[] = [
  { id: 'grp-001', name: 'Leadership', memberIds: ['user-001', 'user-002'] },
  { id: 'grp-002', name: 'Finance', memberIds: ['user-003', 'user-004'] },
];

const docs: Doc[] = [
  {
    id: 'doc-001',
    title: 'Quarterly Report Q1 2026',
    ownerId: 'user-001',
    tags: ['finance', 'quarterly'],
    createdAt: '2026-01-05T09:00:00Z',
    updatedAt: '2026-04-02T14:30:00Z',
    currentVersion: 'v3',
  },
  {
    id: 'doc-002',
    title: 'Employee Handbook',
    ownerId: 'user-003',
    tags: ['hr', 'policy'],
    createdAt: '2025-08-12T10:00:00Z',
    updatedAt: '2025-08-12T10:00:00Z',
    currentVersion: 'v1',
  },
  {
    id: 'doc-003',
    title: 'Product Roadmap 2026',
    ownerId: 'user-002',
    tags: ['product', 'planning'],
    createdAt: '2025-11-20T08:15:00Z',
    updatedAt: '2026-02-01T11:00:00Z',
    currentVersion: 'v2',
  },
];

const versions: Version[] = [
  {
    documentId: 'doc-001',
    versionId: 'v1',
    authorId: 'user-001',
    createdAt: '2026-01-05T09:00:00Z',
    note: 'Initial draft',
    content: 'Quarterly Report Q1 2026 — draft outline. Revenue and expense sections pending.',
  },
  {
    documentId: 'doc-001',
    versionId: 'v2',
    authorId: 'user-002',
    createdAt: '2026-02-10T16:45:00Z',
    note: 'Added revenue figures',
    content:
      'Quarterly Report Q1 2026. Revenue grew 12% quarter over quarter, driven by enterprise renewals. Expense section pending.',
  },
  {
    documentId: 'doc-001',
    versionId: 'v3',
    authorId: 'user-001',
    createdAt: '2026-04-02T14:30:00Z',
    note: 'Final: expenses and outlook',
    content:
      'Quarterly Report Q1 2026. Revenue grew 12% quarter over quarter, driven by enterprise renewals. Operating expenses held flat. Outlook for Q2 remains positive.',
  },
  {
    documentId: 'doc-002',
    versionId: 'v1',
    authorId: 'user-003',
    createdAt: '2025-08-12T10:00:00Z',
    note: 'Initial publication',
    content: 'Employee Handbook. Covers onboarding, leave policy, and code of conduct.',
  },
  {
    documentId: 'doc-003',
    versionId: 'v1',
    authorId: 'user-002',
    createdAt: '2025-11-20T08:15:00Z',
    note: 'Initial roadmap',
    content: 'Product Roadmap 2026. H1: platform consolidation. H2: TBD.',
  },
  {
    documentId: 'doc-003',
    versionId: 'v2',
    authorId: 'user-004',
    createdAt: '2026-02-01T11:00:00Z',
    note: 'H2 themes added',
    content: 'Product Roadmap 2026. H1: platform consolidation. H2: analytics and insights suite.',
  },
];

const grants: Grant[] = [
  { grantId: 'grant-001', documentId: 'doc-001', granteeType: 'user', granteeId: 'user-001', level: 'admin' },
  { grantId: 'grant-002', documentId: 'doc-001', granteeType: 'group', granteeId: 'grp-002', level: 'read' },
  { grantId: 'grant-003', documentId: 'doc-001', granteeType: 'group', granteeId: 'grp-001', level: 'write' },
  { grantId: 'grant-004', documentId: 'doc-002', granteeType: 'user', granteeId: 'user-003', level: 'admin' },
  { grantId: 'grant-005', documentId: 'doc-002', granteeType: 'group', granteeId: 'grp-001', level: 'read' },
  { grantId: 'grant-006', documentId: 'doc-003', granteeType: 'user', granteeId: 'user-002', level: 'admin' },
];

let nextDocNum = 4;
let nextGrantNum = 7;

function nowIso(): string {
  return new Date().toISOString();
}

function getDoc(id: string): Doc {
  const doc = docs.find((d) => d.id === id);
  if (!doc) {
    throw new ApiError(
      `No document with id "${id}". Use GET /api/documents to list documents, ` +
        `or GET /api/documents/search to find one by title or content.`,
    );
  }
  return doc;
}

function getUser(id: string): User {
  const user = users.find((u) => u.id === id);
  if (!user) {
    throw new ApiError(`No user with id "${id}". Use GET /api/users to list users.`);
  }
  return user;
}

function getGroup(id: string): Group {
  const group = groups.find((g) => g.id === id);
  if (!group) {
    throw new ApiError(`No group with id "${id}". Use GET /api/groups to list groups.`);
  }
  return group;
}

function requireBody(body: unknown, endpoint: string): Record<string, unknown> {
  if (body === undefined || body === null || typeof body !== 'object' || Array.isArray(body)) {
    throw new ApiError(
      `${endpoint} requires a JSON object request body. ` +
        `Use describe_endpoint to see the request schema.`,
    );
  }
  return body as Record<string, unknown>;
}

const LEVEL_RANK: Record<Level, number> = { read: 1, write: 2, admin: 3 };

function docMeta(doc: Doc) {
  const { id, title, ownerId, createdAt, updatedAt } = doc;
  return { id, title, ownerId, createdAt, updatedAt };
}

// Handlers are keyed by "<METHOD> <path template>"; params holds the values
// captured from the {placeholders} in the template.
type Handler = (params: Record<string, string>, query: URLSearchParams, body: unknown) => unknown;

export const handlers: Record<string, Handler> = {
  'GET /api/documents': (_params, query) => {
    let result = [...docs].sort((a, b) => b.updatedAt.localeCompare(a.updatedAt));
    const ownerId = query.get('ownerId');
    if (ownerId) result = result.filter((d) => d.ownerId === ownerId);
    const total = result.length;
    const offset = Number(query.get('offset') ?? 0);
    const limit = Number(query.get('limit') ?? 20);
    return { documents: result.slice(offset, offset + limit).map(docMeta), total };
  },

  'POST /api/documents': (_params, _query, rawBody) => {
    const body = requireBody(rawBody, 'POST /api/documents');
    if (typeof body.title !== 'string' || typeof body.content !== 'string') {
      throw new ApiError(
        'POST /api/documents requires a body with string fields "title" and "content" ' +
          '(and optionally "tags", an array of strings).',
      );
    }
    const id = `doc-${String(nextDocNum++).padStart(3, '0')}`;
    const createdAt = nowIso();
    const doc: Doc = {
      id,
      title: body.title,
      ownerId: ACTING_USER,
      tags: Array.isArray(body.tags) ? body.tags.map(String) : [],
      createdAt,
      updatedAt: createdAt,
      currentVersion: 'v1',
    };
    docs.push(doc);
    versions.push({
      documentId: id,
      versionId: 'v1',
      authorId: ACTING_USER,
      createdAt,
      note: 'Initial version',
      content: body.content,
    });
    grants.push({
      grantId: `grant-${String(nextGrantNum++).padStart(3, '0')}`,
      documentId: id,
      granteeType: 'user',
      granteeId: ACTING_USER,
      level: 'admin',
    });
    return { id, title: doc.title, ownerId: doc.ownerId, createdAt, currentVersion: 'v1' };
  },

  'GET /api/documents/{id}': (params) => {
    const doc = getDoc(params.id);
    const head = versions.find((v) => v.documentId === doc.id && v.versionId === doc.currentVersion);
    return { ...doc, content: head?.content ?? '' };
  },

  'PATCH /api/documents/{id}': (params, _query, rawBody) => {
    const doc = getDoc(params.id);
    const body = requireBody(rawBody, 'PATCH /api/documents/{id}');
    if (body.title === undefined && body.content === undefined && body.tags === undefined) {
      throw new ApiError(
        'PATCH /api/documents/{id} requires at least one of "title", "content", or "tags" in the body.',
      );
    }
    if (body.title !== undefined) doc.title = String(body.title);
    if (body.tags !== undefined && Array.isArray(body.tags)) doc.tags = body.tags.map(String);
    if (body.content !== undefined) {
      const versionId = `v${versions.filter((v) => v.documentId === doc.id).length + 1}`;
      versions.push({
        documentId: doc.id,
        versionId,
        authorId: ACTING_USER,
        createdAt: nowIso(),
        note: 'Content update',
        content: String(body.content),
      });
      doc.currentVersion = versionId;
    }
    doc.updatedAt = nowIso();
    return { id: doc.id, title: doc.title, updatedAt: doc.updatedAt, currentVersion: doc.currentVersion };
  },

  'DELETE /api/documents/{id}': (params) => {
    const doc = getDoc(params.id);
    docs.splice(docs.indexOf(doc), 1);
    for (let i = versions.length - 1; i >= 0; i--) {
      if (versions[i].documentId === doc.id) versions.splice(i, 1);
    }
    for (let i = grants.length - 1; i >= 0; i--) {
      if (grants[i].documentId === doc.id) grants.splice(i, 1);
    }
    return { deleted: doc.id };
  },

  'GET /api/documents/search': (_params, query) => {
    const q = query.get('q');
    if (!q) {
      throw new ApiError(
        'Missing required query parameter "q". ' +
          'Example: execute_endpoint with path "/api/documents/search" and query "q=quarterly report".',
      );
    }
    const limit = Number(query.get('limit') ?? 10);
    const terms = q.toLowerCase().split(/\s+/).filter(Boolean);
    const results = docs
      .map((doc) => {
        const head = versions.find((v) => v.documentId === doc.id && v.versionId === doc.currentVersion);
        const haystack = `${doc.title} ${head?.content ?? ''}`.toLowerCase();
        const matched = terms.filter((t) => haystack.includes(t)).length;
        return { id: doc.id, title: doc.title, relevance: Math.round((matched / terms.length) * 100) / 100 };
      })
      .filter((r) => r.relevance >= 0.5) // documents must match at least half the terms
      .sort((a, b) => b.relevance - a.relevance)
      .slice(0, limit);
    return { results };
  },

  'GET /api/users': () => ({ users }),

  'GET /api/users/{id}': (params) => getUser(params.id),

  'GET /api/users/{id}/groups': (params) => {
    const user = getUser(params.id);
    return {
      userId: user.id,
      groups: groups.filter((g) => g.memberIds.includes(user.id)).map(({ id, name }) => ({ id, name })),
    };
  },

  'GET /api/groups': () => ({
    groups: groups.map((g) => ({ id: g.id, name: g.name, memberCount: g.memberIds.length })),
  }),

  'GET /api/groups/{id}/members': (params) => {
    const group = getGroup(params.id);
    return { groupId: group.id, members: group.memberIds.map(getUser) };
  },

  'POST /api/groups/{id}/members': (params, _query, rawBody) => {
    const group = getGroup(params.id);
    const body = requireBody(rawBody, 'POST /api/groups/{id}/members');
    if (typeof body.userId !== 'string') {
      throw new ApiError('POST /api/groups/{id}/members requires a body with a string field "userId".');
    }
    const user = getUser(body.userId);
    if (group.memberIds.includes(user.id)) {
      throw new ApiError(`User "${user.id}" is already a member of group "${group.id}" (${group.name}).`);
    }
    group.memberIds.push(user.id);
    return { groupId: group.id, userId: user.id, memberCount: group.memberIds.length };
  },

  'GET /api/documents/{id}/permissions': (params) => {
    const doc = getDoc(params.id);
    return {
      documentId: doc.id,
      grants: grants
        .filter((g) => g.documentId === doc.id)
        .map((g) => ({
          grantId: g.grantId,
          granteeType: g.granteeType,
          granteeId: g.granteeId,
          granteeName: g.granteeType === 'user' ? getUser(g.granteeId).name : getGroup(g.granteeId).name,
          level: g.level,
        })),
    };
  },

  'POST /api/documents/{id}/permissions': (params, _query, rawBody) => {
    const doc = getDoc(params.id);
    const body = requireBody(rawBody, 'POST /api/documents/{id}/permissions');
    const { granteeType, granteeId, level } = body as Record<string, unknown>;
    if (granteeType !== 'user' && granteeType !== 'group') {
      throw new ApiError('Field "granteeType" must be "user" or "group".');
    }
    if (typeof granteeId !== 'string') {
      throw new ApiError('Field "granteeId" must be a user or group ID string, e.g. "user-002" or "grp-001".');
    }
    if (level !== 'read' && level !== 'write' && level !== 'admin') {
      throw new ApiError('Field "level" must be one of "read", "write", or "admin".');
    }
    // Validate the grantee exists.
    if (granteeType === 'user') getUser(granteeId);
    else getGroup(granteeId);
    const existing = grants.find(
      (g) => g.documentId === doc.id && g.granteeType === granteeType && g.granteeId === granteeId,
    );
    if (existing) {
      existing.level = level;
      return { grantId: existing.grantId, documentId: doc.id, granteeType, granteeId, level };
    }
    const grant: Grant = {
      grantId: `grant-${String(nextGrantNum++).padStart(3, '0')}`,
      documentId: doc.id,
      granteeType,
      granteeId,
      level,
    };
    grants.push(grant);
    return { grantId: grant.grantId, documentId: doc.id, granteeType, granteeId, level };
  },

  'DELETE /api/documents/{id}/permissions/{grantId}': (params) => {
    const doc = getDoc(params.id);
    const idx = grants.findIndex((g) => g.documentId === doc.id && g.grantId === params.grantId);
    if (idx === -1) {
      throw new ApiError(
        `No grant "${params.grantId}" on document "${doc.id}". ` +
          `Use GET /api/documents/{id}/permissions to list the grants and their IDs.`,
      );
    }
    grants.splice(idx, 1);
    return { documentId: doc.id, revoked: params.grantId };
  },

  'GET /api/documents/{id}/permissions/check': (params, query) => {
    const doc = getDoc(params.id);
    const userId = query.get('userId');
    if (!userId) {
      throw new ApiError(
        'Missing required query parameter "userId". ' +
          'Example: path "/api/documents/doc-001/permissions/check" with query "userId=user-002".',
      );
    }
    const user = getUser(userId);
    const sources: { via: string; level: Level }[] = [];
    for (const g of grants.filter((g) => g.documentId === doc.id)) {
      if (g.granteeType === 'user' && g.granteeId === user.id) {
        sources.push({ via: 'direct', level: g.level });
      } else if (g.granteeType === 'group' && getGroup(g.granteeId).memberIds.includes(user.id)) {
        sources.push({ via: `group:${getGroup(g.granteeId).name}`, level: g.level });
      }
    }
    const level =
      sources.length === 0
        ? 'none'
        : sources.reduce((best, s) => (LEVEL_RANK[s.level] > LEVEL_RANK[best] ? s.level : best), 'read' as Level);
    return { documentId: doc.id, userId: user.id, level, sources };
  },

  'GET /api/documents/{id}/versions': (params) => {
    const doc = getDoc(params.id);
    return {
      documentId: doc.id,
      currentVersion: doc.currentVersion,
      versions: versions
        .filter((v) => v.documentId === doc.id)
        .map((v) => ({
          versionId: v.versionId,
          author: { id: v.authorId, name: getUser(v.authorId).name },
          createdAt: v.createdAt,
          note: v.note,
        })),
    };
  },

  'GET /api/documents/{id}/versions/{versionId}': (params) => {
    const doc = getDoc(params.id);
    const version = versions.find((v) => v.documentId === doc.id && v.versionId === params.versionId);
    if (!version) {
      throw new ApiError(
        `No version "${params.versionId}" of document "${doc.id}". ` +
          `Use GET /api/documents/{id}/versions to list the versions.`,
      );
    }
    return {
      documentId: doc.id,
      versionId: version.versionId,
      author: { id: version.authorId, name: getUser(version.authorId).name },
      createdAt: version.createdAt,
      note: version.note,
      content: version.content,
    };
  },

  'GET /api/documents/{id}/versions/compare': (params, query) => {
    const doc = getDoc(params.id);
    const fromId = query.get('from');
    const toId = query.get('to');
    if (!fromId || !toId) {
      throw new ApiError(
        'Both query parameters "from" and "to" are required, e.g. query "from=v1&to=v3". ' +
          'Use GET /api/documents/{id}/versions to list the version IDs.',
      );
    }
    const from = versions.find((v) => v.documentId === doc.id && v.versionId === fromId);
    const to = versions.find((v) => v.documentId === doc.id && v.versionId === toId);
    if (!from || !to) {
      throw new ApiError(
        `Version "${!from ? fromId : toId}" does not exist on document "${doc.id}". ` +
          `Use GET /api/documents/{id}/versions to list the versions.`,
      );
    }
    return {
      documentId: doc.id,
      from: from.versionId,
      to: to.versionId,
      contentChanged: from.content !== to.content,
      sizeDelta: to.content.length - from.content.length,
      authors: [getUser(from.authorId).name, getUser(to.authorId).name],
    };
  },

  'POST /api/documents/{id}/versions/{versionId}/restore': (params) => {
    const doc = getDoc(params.id);
    const source = versions.find((v) => v.documentId === doc.id && v.versionId === params.versionId);
    if (!source) {
      throw new ApiError(
        `No version "${params.versionId}" of document "${doc.id}" to restore. ` +
          `Use GET /api/documents/{id}/versions to list the versions.`,
      );
    }
    const newVersionId = `v${versions.filter((v) => v.documentId === doc.id).length + 1}`;
    const createdAt = nowIso();
    versions.push({
      documentId: doc.id,
      versionId: newVersionId,
      authorId: ACTING_USER,
      createdAt,
      note: `Restored from ${source.versionId}`,
      content: source.content,
    });
    doc.currentVersion = newVersionId;
    doc.updatedAt = createdAt;
    return { documentId: doc.id, restoredFrom: source.versionId, newVersion: newVersionId, createdAt };
  },
};
