import { describe, it, expect, beforeEach, vi } from "vitest";
import { CommandExecutor, matchEventToCorrelation } from "../../src/commands/command-executor";
import type {
  BookmarkAdapter,
  CommandCorrelation,
  CommandExecutionResult,
  CompletionRequest,
  ExtensionCommand,
  ExtensionEvent,
  StorageRepository,
} from "../../src/api/contracts";

function makeCommand(
  overrides: Partial<ExtensionCommand> = {},
): ExtensionCommand {
  return {
    operationId: "op-1",
    leaseId: "lease-1",
    leaseExpiresAt: "2099-01-01T00:00:00Z",
    commandType: "Create",
    bookmarkId: "bm-1",
    browserNodeId: null,
    expectedVersion: 1,
    createdAt: "2026-06-22T09:00:00Z",
    payload: {
      type: "Bookmark",
      parentBrowserNodeId: "42",
      title: "Test",
      url: "https://example.com",
      position: 0,
    },
    ...overrides,
  };
}

function makeResult(overrides: Partial<CommandExecutionResult> = {}): CommandExecutionResult {
  return {
    succeeded: true,
    browserNodeId: "100",
    completedNodeMappings: [],
    retryable: false,
    errorCode: null,
    errorMessage: null,
    ...overrides,
  };
}

describe("CommandExecutor", () => {
  let adapter: { apply: ReturnType<typeof vi.fn> } & Pick<BookmarkAdapter, "getSubtree">;
  let storage: Pick<StorageRepository, "saveCorrelation" | "pruneExpiredCorrelations" | "getCorrelation">;
  let completions: { operationId: string; input: CompletionRequest }[];
  let executor: CommandExecutor;

  beforeEach(() => {
    adapter = {
      apply: vi.fn().mockResolvedValue(makeResult()),
      getSubtree: vi.fn(),
    } as never;
    storage = {
      saveCorrelation: vi.fn().mockResolvedValue(undefined),
      pruneExpiredCorrelations: vi.fn().mockResolvedValue(undefined),
      getCorrelation: vi.fn().mockResolvedValue(null),
    } as never;
    completions = [];
    executor = new CommandExecutor({
      adapter: adapter as never,
      storage: storage as never,
      now: () => new Date("2026-06-22T10:00:00Z"),
    });
  });

  it("applies a command and completes with Succeeded", async () => {
    await executor.executeCommands([makeCommand()], async (op, input) => {
      completions.push({ operationId: op, input });
    });

    expect(adapter.apply).toHaveBeenCalledOnce();
    expect(completions).toHaveLength(1);
    expect(completions[0]!.input.status).toBe("Succeeded");
    expect(completions[0]!.input.browserNodeId).toBe("100");
  });

  it("saves correlation before applying", async () => {
    await executor.executeCommands([makeCommand()], async () => {});

    expect(storage.saveCorrelation).toHaveBeenCalled();
    const corr = vi.mocked(storage.saveCorrelation).mock.calls[0]![0] as CommandCorrelation;
    expect(corr.operationId).toBe("op-1");
    expect(corr.commandType).toBe("Create");
    expect(corr.browserNodeId).toBeNull();
  });

  it("updates correlation with new browserNodeId after create", async () => {
    await executor.executeCommands([makeCommand()], async () => {});

    const calls = vi.mocked(storage.saveCorrelation).mock.calls;
    expect(calls.length).toBeGreaterThanOrEqual(2);
    const updatedCorr = calls[1]![0] as CommandCorrelation;
    expect(updatedCorr.browserNodeId).toBe("100");
  });

  it("completes with PermanentFailure on protected node error", async () => {
    adapter.apply.mockResolvedValueOnce(
      makeResult({
        succeeded: false,
        retryable: false,
        errorCode: "PROTECTED_NODE",
        errorMessage: "Cannot modify protected node",
        browserNodeId: null,
      }),
    );

    await executor.executeCommands([makeCommand()], async (op, input) => {
      completions.push({ operationId: op, input });
    });

    expect(completions[0]!.input.status).toBe("PermanentFailure");
    expect(completions[0]!.input.errorCode).toBe("PROTECTED_NODE");
  });

  it("completes with RetryableFailure on transient error", async () => {
    adapter.apply.mockResolvedValueOnce(
      makeResult({
        succeeded: false,
        retryable: true,
        errorCode: "BRAVE_ERROR",
        errorMessage: "Transient failure",
        browserNodeId: null,
      }),
    );

    await executor.executeCommands([makeCommand()], async (op, input) => {
      completions.push({ operationId: op, input });
    });

    expect(completions[0]!.input.status).toBe("RetryableFailure");
  });

  it("skips command with expired lease", async () => {
    const expiredCommand = makeCommand({
      leaseExpiresAt: "2020-01-01T00:00:00Z",
    });

    await executor.executeCommands([expiredCommand], async (op, input) => {
      completions.push({ operationId: op, input });
    });

    expect(adapter.apply).not.toHaveBeenCalled();
    expect(completions[0]!.input.status).toBe("RetryableFailure");
    expect(completions[0]!.input.errorCode).toBe("LEASE_EXPIRED");
  });

  it("prunes expired correlations before execution", async () => {
    await executor.executeCommands([makeCommand()], async () => {});
    expect(storage.pruneExpiredCorrelations).toHaveBeenCalledOnce();
  });

  it("handles completion failure gracefully", async () => {
    await executor.executeCommands(
      [makeCommand()],
      async () => {
        throw new Error("Network failure");
      },
    );
    // Should not throw — completion failure is caught
    expect(adapter.apply).toHaveBeenCalledOnce();
  });

  it("does not re-apply a re-delivered command whose correlation was applied", async () => {
    vi.mocked(storage.getCorrelation).mockResolvedValue({
      operationId: "op-1",
      commandType: "Create",
      browserNodeId: "100",
      expectedParentBrowserNodeId: "42",
      expectedTitle: "Test",
      expectedUrl: "https://example.com",
      startedAt: "2026-06-22T09:59:00Z",
      expiresAt: "2026-06-22T10:09:00Z",
      applied: true,
      completedNodeMappings: [{ bookmarkId: "b-1", browserNodeId: "100" }],
    });

    await executor.executeCommands([makeCommand()], async (op, input) => {
      completions.push({ operationId: op, input });
    });

    expect(adapter.apply).not.toHaveBeenCalled();
    expect(completions).toHaveLength(1);
    expect(completions[0]!.input.status).toBe("Succeeded");
    expect(completions[0]!.input.browserNodeId).toBe("100");
    expect(completions[0]!.input.completedNodeMappings).toEqual([
      { bookmarkId: "b-1", browserNodeId: "100" },
    ]);
  });

  it("executes normally when the stored correlation has not been applied yet", async () => {
    vi.mocked(storage.getCorrelation).mockResolvedValue({
      operationId: "op-1",
      commandType: "Create",
      browserNodeId: null,
      expectedParentBrowserNodeId: "42",
      expectedTitle: "Test",
      expectedUrl: "https://example.com",
      startedAt: "2026-06-22T09:59:00Z",
      expiresAt: "2026-06-22T10:09:00Z",
      applied: false,
      completedNodeMappings: [],
    });

    await executor.executeCommands([makeCommand()], async (op, input) => {
      completions.push({ operationId: op, input });
    });

    expect(adapter.apply).toHaveBeenCalledOnce();
    expect(completions[0]!.input.status).toBe("Succeeded");
  });

  it("re-applies a re-delivered command whose prior apply failed (not marked applied)", async () => {
    vi.mocked(storage.getCorrelation).mockResolvedValue({
      operationId: "op-1",
      commandType: "Create",
      browserNodeId: "42",
      expectedParentBrowserNodeId: "42",
      expectedTitle: "Test",
      expectedUrl: "https://example.com",
      startedAt: "2026-06-22T09:59:00Z",
      expiresAt: "2026-06-22T10:09:00Z",
      applied: false,
      completedNodeMappings: [],
    });

    await executor.executeCommands([makeCommand()], async (op, input) => {
      completions.push({ operationId: op, input });
    });

    expect(adapter.apply).toHaveBeenCalledOnce();
  });
});

describe("matchEventToCorrelation", () => {
  const now = new Date("2026-06-22T10:00:00Z");

  function makeCorrelation(overrides: Partial<CommandCorrelation> = {}): CommandCorrelation {
    return {
      operationId: "op-1",
      commandType: "Update",
      browserNodeId: "84",
      expectedParentBrowserNodeId: null,
      expectedTitle: "Test",
      expectedUrl: null,
      startedAt: "2026-06-22T09:59:50Z",
      expiresAt: "2026-06-22T10:05:00Z",
      applied: true,
      completedNodeMappings: [],
      ...overrides,
    };
  }

  function makeEvent(overrides: Partial<ExtensionEvent> = {}): ExtensionEvent {
    return {
      eventId: "evt-1",
      eventType: "Changed",
      browserNodeId: "84",
      occurredAt: "2026-06-22T10:00:00Z",
      causedByOperationId: null,
      payload: { title: "Test", url: null },
      ...overrides,
    };
  }

  it("matches by browserNodeId when the command type can cause the event", () => {
    const match = matchEventToCorrelation(makeEvent(), [makeCorrelation()], now);
    expect(match).not.toBeNull();
    expect(match!.operationId).toBe("op-1");
  });

  it("returns null for no match", () => {
    const match = matchEventToCorrelation(makeEvent(), [], now);
    expect(match).toBeNull();
  });

  it("skips expired correlations", () => {
    const correlations = [makeCorrelation({ expiresAt: "2026-06-22T09:10:00Z" })];
    const match = matchEventToCorrelation(makeEvent(), correlations, now);
    expect(match).toBeNull();
  });

  it("skips correlations outside the echo-match window, even if not yet expired", () => {
    // startedAt five minutes before `now`: well within the 10-minute
    // idempotency TTL, but a genuine user edit that far after the command
    // was applied must not be mistaken for the command's own echo.
    const correlations = [
      makeCorrelation({ startedAt: "2026-06-22T09:55:00Z", expiresAt: "2026-06-22T10:05:00Z" }),
    ];
    const match = matchEventToCorrelation(makeEvent(), correlations, now);
    expect(match).toBeNull();
  });

  it("does not match a Removed event to a Create correlation", () => {
    const correlations = [makeCorrelation({ commandType: "Create", browserNodeId: null })];
    const event = makeEvent({
      eventType: "Removed",
      payload: { removedNode: { browserNodeId: "84" } },
    });
    expect(matchEventToCorrelation(event, correlations, now)).toBeNull();
  });

  it("matches a pending Create correlation only when title, url, and parent match", () => {
    const correlations = [
      makeCorrelation({
        commandType: "Create",
        browserNodeId: null,
        expectedTitle: "New Bookmark",
        expectedUrl: "https://example.com/a",
        expectedParentBrowserNodeId: "42",
      }),
    ];
    const event = makeEvent({
      eventType: "Created",
      browserNodeId: "900",
      payload: {
        node: {
          browserNodeId: "900",
          parentBrowserNodeId: "42",
          title: "New Bookmark",
          url: "https://example.com/a",
        },
      },
    });
    expect(matchEventToCorrelation(event, correlations, now)?.operationId).toBe("op-1");
  });

  it("does not match a pending Create correlation with a different title", () => {
    const correlations = [
      makeCorrelation({
        commandType: "Create",
        browserNodeId: null,
        expectedTitle: "New Bookmark",
        expectedUrl: "https://example.com/a",
        expectedParentBrowserNodeId: "42",
      }),
    ];
    const event = makeEvent({
      eventType: "Created",
      browserNodeId: "900",
      payload: {
        node: {
          browserNodeId: "900",
          parentBrowserNodeId: "42",
          title: "User Made This",
          url: "https://example.com/a",
        },
      },
    });
    expect(matchEventToCorrelation(event, correlations, now)).toBeNull();
  });

  it("matches a Moved event to a Reorder correlation via the parent id", () => {
    const correlations = [makeCorrelation({ commandType: "Reorder", browserNodeId: "7" })];
    const event = makeEvent({
      eventType: "Moved",
      browserNodeId: "301",
      payload: {
        oldParentBrowserNodeId: "7",
        oldPosition: 2,
        parentBrowserNodeId: "7",
        position: 0,
      },
    });
    expect(matchEventToCorrelation(event, correlations, now)?.operationId).toBe("op-1");
  });

  it("matches a Removed event to a Delete correlation by node id", () => {
    const correlations = [makeCorrelation({ commandType: "Delete", browserNodeId: "84" })];
    const event = makeEvent({
      eventType: "Removed",
      payload: { removedNode: { browserNodeId: "84" } },
    });
    expect(matchEventToCorrelation(event, correlations, now)?.operationId).toBe("op-1");
  });
});
