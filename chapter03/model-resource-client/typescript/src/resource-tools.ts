// The two wrapper tools from §3.3.3: MCP resources have no model-native integration
// point, so the host creates one — list_resources and read_resource, aggregating across
// every connected server. The methods are the book extract; the class, the metadata
// record, and the two format helpers are the plumbing the extract elides.

import {
  Client,
  type ReadResourceResult,
  type Resource,
} from '@modelcontextprotocol/client';
import {
  StdioClientTransport,
  type StdioServerParameters,
} from '@modelcontextprotocol/client/stdio';

// What the model sees per resource. serverName is the routing key for read_resource.
export interface ResourceMetadata {
  serverName: string;
  uri: string;
  name: string;
  title?: string;
  description?: string;
  mimeType?: string;
}

export class ResourceTools {
  constructor(private readonly clientManager: ClientManager) {}

  // Lists available resources from connected servers.
  async listResources(): Promise<ResourceMetadata[]> {
    const resourceMetadata: ResourceMetadata[] = [];
    for (const serverName of this.clientManager.getServerNames()) {
      const client = this.clientManager.getClient(serverName);
      const { resources } = await client.listResources();
      resourceMetadata.push(...formatResourceMetadata(resources, serverName));
    }
    return resourceMetadata;
  }

  // Reads a resource by server name and URI.
  async readResource(serverName: string, uri: string): Promise<string> {
    const result = await
      this.clientManager.getClient(serverName).readResource({ uri });
    return formatResourceContent(result);
  }
}

// Tag every entry with the HOST-ASSIGNED server name so the model can route reads —
// and so two servers with colliding URIs (like the two spawns in this demo) stay apart.
function formatResourceMetadata(resources: Resource[], serverName: string): ResourceMetadata[] {
  return resources.map((r) => ({
    serverName,
    uri: r.uri,
    name: r.name,
    title: r.title,
    description: r.description,
    mimeType: r.mimeType,
  }));
}

// Models (and many chat APIs) only take text from tools; flatten accordingly.
function formatResourceContent(result: ReadResourceResult): string {
  return result.contents
    .map((c) =>
      'text' in c ? String(c.text) : `[binary content: ${c.mimeType ?? 'unknown type'}]`,
    )
    .join('\n');
}

// Owns one Client per connected server, keyed by a label the HOST assigns. Never key
// on the server's self-reported name — identity claims from the server are not trusted
// input for routing decisions (§3.3.3).
export class ClientManager {
  private readonly clients = new Map<string, Client>();

  async connect(serverName: string, transportOptions: StdioServerParameters): Promise<void> {
    const client = new Client(
      { name: serverName, version: '0.1.0' },
      { versionNegotiation: { mode: 'auto' } },
    );
    await client.connect(new StdioClientTransport(transportOptions));
    this.clients.set(serverName, client);
  }

  getServerNames(): string[] {
    return [...this.clients.keys()];
  }

  getClient(serverName: string): Client {
    const client = this.clients.get(serverName);
    if (client === undefined) throw new Error(`No connected server is labeled '${serverName}'.`);
    return client;
  }

  async closeAll(): Promise<void> {
    for (const client of this.clients.values()) await client.close();
  }
}
