import type {
  BookmarkAdapter,
  CommandCorrelation,
  CommandExecutionResult,
  CompletionRequest,
  ExtensionCommand,
  NodeMapping,
  StorageRepository,
} from "../api/contracts";

const CORRELATION_TTL_MS = 10 * 60 * 1000;

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
    };
    await this.deps.storage.saveCorrelation(correlation);

    const result = await this.deps.adapter.apply(command);

    if (result.succeeded && result.browserNodeId !== null) {
      const updatedCorrelation: CommandCorrelation = {
        ...correlation,
        browserNodeId: result.browserNodeId,
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

export function matchEventToCorrelation(
  eventType: string,
  browserNodeId: string,
  correlations: CommandCorrelation[],
  now: Date,
): CommandCorrelation | null {
  const nowMs = now.getTime();
  for (const corr of correlations) {
    if (new Date(corr.expiresAt).getTime() < nowMs) continue;

    if (corr.browserNodeId === browserNodeId) {
      return corr;
    }

    if (corr.commandType === "Create" && corr.browserNodeId === null) {
      return corr;
    }
  }
  return null;
}

export { type NodeMapping };
