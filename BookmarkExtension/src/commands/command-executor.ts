import type {
  BookmarkAdapter,
  CommandCorrelation,
  CommandExecutionResult,
  CommandType,
  CompletionRequest,
  ExtensionCommand,
  ExtensionEvent,
  StorageRepository,
} from "../api/contracts";

const CORRELATION_TTL_MS = 10 * 60 * 1000;
/** Only a browser event arriving shortly after a command was applied can be
 * that command's echo — a real user edit made minutes later on the same
 * node must not be mistaken for one and dropped. */
const ECHO_MATCH_WINDOW_MS = 20 * 1000;

export interface CommandExecutorDeps {
  adapter: BookmarkAdapter;
  storage: StorageRepository;
  now: () => Date;
}

export class CommandExecutor {
  constructor(private deps: CommandExecutorDeps) {}

  async executeCommands(
    commands: ExtensionCommand[],
    completeFn: (operationId: string, input: CompletionRequest) => Promise<void>,
  ): Promise<void> {
    const now = this.deps.now();
    await this.deps.storage.pruneExpiredCorrelations(now);

    for (const command of commands) {
      await this.executeOne(command, completeFn);
    }
  }

  private async executeOne(
    command: ExtensionCommand,
    completeFn: (operationId: string, input: CompletionRequest) => Promise<void>,
  ): Promise<void> {
    const now = this.deps.now();

    // Idempotency: a re-delivered command (lost completion ack, lease
    // re-claim) must not run against the browser again — a repeated Create
    // would duplicate the bookmark. Re-report completion instead. Guarded by
    // `applied`, not by browserNodeId presence — for Move/Update/Delete the
    // correlation is saved with a non-null browserNodeId *before* apply runs,
    // so browserNodeId alone can't tell a completed apply from a pending one.
    const existing = await this.deps.storage.getCorrelation(command.operationId);
    if (existing && existing.applied) {
      await this.complete(
        command,
        {
          succeeded: true,
          browserNodeId: existing.browserNodeId,
          completedNodeMappings: existing.completedNodeMappings,
          retryable: false,
          errorCode: null,
          errorMessage: null,
        },
        completeFn,
      );
      return;
    }

    if (new Date(command.leaseExpiresAt) <= now) {
      await this.complete(
        command,
        {
          succeeded: false,
          browserNodeId: null,
          completedNodeMappings: [],
          retryable: true,
          errorCode: "LEASE_EXPIRED",
          errorMessage: "Lease expired before execution",
        },
        completeFn,
      );
      return;
    }

    const correlation: CommandCorrelation = {
      operationId: command.operationId,
      commandType: command.commandType,
      browserNodeId: command.browserNodeId,
      expectedParentBrowserNodeId: this.extractParentId(command),
      expectedTitle: this.extractTitle(command),
      expectedUrl: this.extractUrl(command),
      startedAt: now.toISOString(),
      expiresAt: new Date(now.getTime() + CORRELATION_TTL_MS).toISOString(),
      applied: false,
      completedNodeMappings: [],
    };
    await this.deps.storage.saveCorrelation(correlation);

    const result = await this.deps.adapter.apply(command);

    if (result.succeeded) {
      const updatedCorrelation: CommandCorrelation = {
        ...correlation,
        browserNodeId: result.browserNodeId ?? correlation.browserNodeId,
        applied: true,
        completedNodeMappings: result.completedNodeMappings,
      };
      await this.deps.storage.saveCorrelation(updatedCorrelation);
    }

    await this.complete(command, result, completeFn);
  }

  private async complete(
    command: ExtensionCommand,
    result: CommandExecutionResult,
    completeFn: (operationId: string, input: CompletionRequest) => Promise<void>,
  ): Promise<void> {
    const status = this.mapStatus(result);
    const completion: CompletionRequest = {
      leaseId: command.leaseId,
      status,
      browserNodeId: result.browserNodeId,
      completedNodeMappings: result.completedNodeMappings,
      errorCode: result.errorCode,
      errorMessage: result.errorMessage,
    };

    try {
      await completeFn(command.operationId, completion);
    } catch {
      // Completion failure — leave for lease expiry / retry
    }
  }

  private mapStatus(
    result: CommandExecutionResult,
  ): "Succeeded" | "RetryableFailure" | "PermanentFailure" {
    if (result.succeeded) return "Succeeded";
    return result.retryable ? "RetryableFailure" : "PermanentFailure";
  }

  private extractParentId(command: ExtensionCommand): string | null {
    const payload = command.payload as Record<string, unknown>;
    if (typeof payload.parentBrowserNodeId === "string") {
      return payload.parentBrowserNodeId;
    }
    return null;
  }

  private extractTitle(command: ExtensionCommand): string | null {
    const payload = command.payload as Record<string, unknown>;
    if (typeof payload.title === "string") {
      return payload.title;
    }
    return null;
  }

  private extractUrl(command: ExtensionCommand): string | null {
    const payload = command.payload as Record<string, unknown>;
    if (typeof payload.url === "string") {
      return payload.url;
    }
    return null;
  }
}

/** Command types that can legitimately cause each browser event type. */
const EVENT_TO_COMMAND_TYPES: Record<string, CommandType[]> = {
  Created: ["Create", "Restore"],
  Changed: ["Update"],
  Moved: ["Move", "Reorder"],
  Reordered: ["Reorder"],
  Removed: ["Delete"],
};

function extractCreatedNode(
  payload: unknown,
): { title?: string; url?: string | null; parentBrowserNodeId?: string | null } | null {
  const node = (payload as { node?: unknown } | null)?.node;
  if (node === null || typeof node !== "object") return null;
  return node as { title?: string; url?: string | null; parentBrowserNodeId?: string | null };
}

function extractMovedParentId(payload: unknown): string | null {
  const parentId = (payload as { parentBrowserNodeId?: unknown } | null)?.parentBrowserNodeId;
  return typeof parentId === "string" ? parentId : null;
}

/**
 * Finds the live correlation for a command the executor recently applied, so
 * the resulting browser event can be stamped with `causedByOperationId`
 * instead of echoing back to the server as a fresh user edit.
 */
export function matchEventToCorrelation(
  event: ExtensionEvent,
  correlations: CommandCorrelation[],
  now: Date,
): CommandCorrelation | null {
  const nowMs = now.getTime();
  for (const corr of correlations) {
    if (new Date(corr.expiresAt).getTime() < nowMs) continue;
    if (!EVENT_TO_COMMAND_TYPES[event.eventType]?.includes(corr.commandType)) continue;
    // Echo suppression only applies right after the command was applied —
    // a match against a stale correlation would wrongly drop a genuine user
    // edit made minutes later on the same node.
    if (nowMs - new Date(corr.startedAt).getTime() > ECHO_MATCH_WINDOW_MS) continue;

    if (corr.browserNodeId !== null) {
      if (corr.browserNodeId === event.browserNodeId) return corr;
      // A Reorder is applied as per-child moves; those Moved events carry the
      // reordered parent (the correlation's node) as the new parent.
      if (
        corr.commandType === "Reorder" &&
        event.eventType === "Moved" &&
        extractMovedParentId(event.payload) === corr.browserNodeId
      ) {
        return corr;
      }
      continue;
    }

    // Pending Create/Restore correlation: the browser id is not known yet, so
    // require the created node's title, url, and parent to match what the
    // command asked for — never blindly absorb an unrelated creation.
    if (event.eventType === "Created") {
      const node = extractCreatedNode(event.payload);
      if (!node) continue;
      if (corr.expectedTitle !== null && corr.expectedTitle !== node.title) continue;
      if (corr.expectedUrl !== (node.url ?? null)) continue;
      if (
        corr.expectedParentBrowserNodeId !== null &&
        corr.expectedParentBrowserNodeId !== (node.parentBrowserNodeId ?? null)
      ) {
        continue;
      }
      return corr;
    }
  }
  return null;
}
