// Completions server (section 5.3.3).
//
// In the TypeScript SDK, completions attach to the resource template itself:
// each template variable can carry a `complete` callback, and the SDK declares
// the `completions` capability and answers `completion/complete` for you.

import { McpServer, ResourceTemplate } from '@modelcontextprotocol/server';
import { serveStdio } from '@modelcontextprotocol/server/stdio';

// The catalog the completion callback completes against. A real server would
// consult its actual resource space (and should fuzzy-match, rate-limit, and
// keep sensitive paths out — see the chapter's guidance).
const KNOWN_PATHS = [
  'docs/readme.md',
  'docs/reference.md',
  'docs/release-notes.md',
  'docs/setup.md',
  'src/main.py',
  'src/utils.py',
  'tests/test_main.py',
];

function createServer(): McpServer {
  const server = new McpServer({ name: 'completions-demo', version: '0.1.0' });

  server.registerResource(
    'project-file',
    new ResourceTemplate('file:///{path}', {
      list: undefined,
      complete: {
        // Filter paths matching the partial input; spec allows max 100 values.
        path: (value) => KNOWN_PATHS.filter((p) => p.startsWith(value)).slice(0, 100),
      },
    }),
    { description: 'Read a project file by path.' },
    async (uri, variables) => {
      const path = variables.path as string;
      if (!KNOWN_PATHS.includes(path)) {
        throw new Error(`unknown path: ${path}`);
      }
      return { contents: [{ uri: uri.href, text: `Contents of ${path}` }] };
    },
  );

  return server;
}

serveStdio(createServer);
