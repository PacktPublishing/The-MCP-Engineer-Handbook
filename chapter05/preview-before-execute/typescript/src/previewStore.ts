import { randomBytes } from 'node:crypto';
import type { FindReplacePlan } from './findReplacePlanner.js';

export type RedeemOutcome =
  | { result: 'not_found' }
  | { result: 'expired' }
  | { result: 'valid'; plan: FindReplacePlan };

// Stores previewed plans keyed by token — the explicit handle pattern from
// Chapter 4 applied to preview-before-execute. This sample keeps the map in
// process memory; in production it belongs in the durable storage the server
// already uses (a database, Redis, ...) so that execute_find_and_replace can
// land on any server instance. The token, not the connection, carries the
// state. A production token would also bind the authenticated user, so a
// preview cannot be redeemed by someone else.
export class PreviewStore {
  static readonly timeToLiveMs = 5 * 60 * 1000;

  private readonly previews = new Map<string, { plan: FindReplacePlan; expiresAt: number }>();

  add(plan: FindReplacePlan): string {
    const token = randomBytes(6).toString('hex');
    this.previews.set(token, { plan, expiresAt: Date.now() + PreviewStore.timeToLiveMs });
    return token;
  }

  // Tokens are single-use: redeeming removes the entry whether or not the
  // subsequent execution succeeds. A failed execution means the state
  // changed, so a fresh preview is needed anyway.
  redeem(token: string): RedeemOutcome {
    const stored = this.previews.get(token);
    if (stored === undefined) return { result: 'not_found' };
    this.previews.delete(token);
    if (stored.expiresAt < Date.now()) return { result: 'expired' };
    return { result: 'valid', plan: stored.plan };
  }
}
