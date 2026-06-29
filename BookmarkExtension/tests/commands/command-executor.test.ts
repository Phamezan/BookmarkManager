import { describe, it, expect, beforeEach, vi } from "vitest";
import { CommandExecutor, matchEventToCorrelation } from "../../src/commands/command-executor";
import type {
  BookmarkAdapter,
  CommandCorrelation,
  CommandExecutionResult,
  CompletionRequest,
  ExtensionCommand,
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
  let adapter: { apply: ReturnType<typeof vi.fn> } & Pick<BookmarkAdapter, "getFolderCatalog" | "getSubtree">;
  let storage: Pick<StorageRepository, "saveCorrelation" | "pruneExpiredCorrelations" | "getCorrelation">;
  let completions: { operationId: string; input: CompletionRequest }[];
  let executor: CommandExecutor;

  beforeEach(() => {
    adapter = {
      apply: vi.fn().mockResolvedValue(makeResult()),
      getFolderCatalog: vi.fn(),
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
});

describe("matchEventToCorrelation", () => {
  const now = new Date("2026-06-22T10:00:00Z");

  it("matches by browserNodeId", () => {
    const correlations: CommandCorrelation[] = [
      {
        operationId: "op-1",
        commandType: "Update",
        browserNodeId: "84",
        expectedParentBrowserNodeId: null,
        expectedTitle: "Test",
        expectedUrl: null,
        startedAt: "2026-06-22T09:55:00Z",
        expiresAt: "2026-06-22T10:05:00Z",
      },
    ];
    const match = matchEventToCorrelation("Changed", "84", correlations, now);
    expect(match).not.toBeNull();
    expect(match!.operationId).toBe("op-1");
  });

  it("returns null for no match", () => {
    const correlations: CommandCorrelation[] = [];
    const match = matchEventToCorrelation("Changed", "84", correlations, now);
    expect(match).toBeNull();
  });

  it("skips expired correlations", () => {
    const correlations: CommandCorrelation[] = [
      {
        operationId: "op-old",
        commandType: "Update",
        browserNodeId: "84",
        expectedParentBrowserNodeId: null,
        expectedTitle: null,
        expectedUrl: null,
        startedAt: "2026-06-22T09:00:00Z",
        expiresAt: "2026-06-22T09:10:00Z",
      },
    ];
    const match = matchEventToCorrelation("Changed", "84", correlations, now);
    expect(match).toBeNull();
  });
});
