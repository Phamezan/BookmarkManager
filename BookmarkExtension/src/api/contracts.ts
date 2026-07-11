export type BrowserNodeType = "Bookmark" | "Folder";

export interface BrowserNode {
  browserNodeId: string;
  parentBrowserNodeId: string | null;
  type: BrowserNodeType;
  title: string;
  url: string | null;
  position: number;
  isProtected: boolean;
  children?: BrowserNode[];
}

/**
 * Transient state written by the `quick-bookmark` command and consumed by the
 * popup to render the bookmark editor. Lives in `chrome.storage.local` under
 * `bm.shortcutEditorState` and is cleared when the editor is done/dismissed.
 */
export interface ShortcutEditorState {
  bookmarkId: string;
  url: string;
  title: string;
  parentId: string;
  capturedAt: string;
  /** True when the command created a new node; false when it edited an existing one. */
  wasCreated: boolean;
}

export type ExtensionEventType =
  | "Created"
  | "Changed"
  | "Moved"
  | "Reordered"
  | "Removed";

export interface ExtensionEvent {
  eventId: string;
  eventType: ExtensionEventType;
  browserNodeId: string;
  occurredAt: string;
  causedByOperationId: string | null;
  payload: unknown;
}

export type CommandType =
  | "Create"
  | "Update"
  | "Move"
  | "Reorder"
  | "Delete"
  | "Restore";

export interface ExtensionCommand {
  operationId: string;
  leaseId: string;
  leaseExpiresAt: string;
  commandType: CommandType;
  bookmarkId: string;
  browserNodeId: string | null;
  expectedVersion: number;
  createdAt: string;
  payload: unknown;
}

export type SnapshotReason = "InitialImport" | "Repair" | "ImportCompleted";

export interface SnapshotRequest {
  requestId: string;
  reason: SnapshotReason;
}

export interface ExtensionConfig {
  configVersion: number;
  pollIntervalSeconds: number;
  snapshotRequest: SnapshotRequest | null;
}

export interface HeartbeatRequest {
  extensionVersion: string;
  braveVersion: string;
  localConfigVersion: number;
  pendingEventCount: number;
  lastSuccessfulSyncAt: string | null;
}

export interface HeartbeatResponse {
  extensionClientId: string;
  serverTime: string;
  configVersion: number;
  pollIntervalSeconds: number;
}

export interface SnapshotRootPayload {
  root: BrowserNode;
}

export interface SnapshotRequestPayload {
  requestId: string;
  configVersion: number;
  capturedAt: string;
  roots: SnapshotRootPayload[];
}

export interface NodeMapping {
  bookmarkId: string;
  browserNodeId: string;
}

export interface SnapshotResponse {
  requestId: string;
  acceptedAt: string;
  mappings: NodeMapping[];
}

export interface EventBatchRequest {
  batchId: string;
  extensionClientId: string;
  configVersion: number;
  events: ExtensionEvent[];
}

export interface EventBatchResponse {
  batchId: string;
  acceptedEventIds: string[];
  duplicateEventIds: string[];
  configVersion: number;
}

export interface ClaimRequest {
  configVersion: number;
  maxCommands: number;
}

export interface ClaimResponse {
  commands: ExtensionCommand[];
}

export type CompletionStatus = "Succeeded" | "RetryableFailure" | "PermanentFailure";

export interface CompletionRequest {
  leaseId: string;
  status: CompletionStatus;
  browserNodeId: string | null;
  completedNodeMappings: NodeMapping[];
  errorCode: string | null;
  errorMessage: string | null;
}

export interface ExtensionSettings {
  apiBaseUrl: string;
  setupComplete: boolean;
}

export interface ServerConfig {
  extensionClientId: string;
  configVersion: number;
  pollIntervalSeconds: number;
  snapshotRequest: SnapshotRequest | null;
}

export interface OutboxEntry {
  event: ExtensionEvent;
  createdAt: string;
  attemptCount: number;
  nextAttemptAt: string;
  lastErrorCode: string | null;
}

export interface CommandCorrelation {
  operationId: string;
  commandType: CommandType;
  browserNodeId: string | null;
  expectedParentBrowserNodeId: string | null;
  expectedTitle: string | null;
  expectedUrl: string | null;
  startedAt: string;
  expiresAt: string;
  /** True once adapter.apply succeeded — distinguishes a genuinely applied
   *  command from one whose apply is still pending or failed, for idempotent
   *  redelivery. */
  applied: boolean;
  /** Node mappings produced by the apply, replayed on idempotent redelivery
   *  so a lost completion ack does not lose Restore's child mappings. */
  completedNodeMappings: NodeMapping[];
}

export type SyncStatusState =
  | "NotConfigured"
  | "Connecting"
  | "Healthy"
  | "Offline"
  | "PermissionRequired"
  | "SnapshotRequired"
  | "Error";

export interface SyncStatus {
  state: SyncStatusState;
  lastAttemptAt: string | null;
  lastSuccessAt: string | null;
  sanitizedErrorCode: string | null;
  pendingEventCount: number;
}

export interface BackupTrackedEntry {
  downloadId: number;
  filename: string;
  timestamp: string;
}

export interface BackupState {
  entries: BackupTrackedEntry[];
  lastAutoBackupAt: string | null;
}

export interface BackupSettings {
  subfolder: string;
}

export interface CommandExecutionResult {
  succeeded: boolean;
  browserNodeId: string | null;
  completedNodeMappings: NodeMapping[];
  retryable: boolean;
  errorCode: string | null;
  errorMessage: string | null;
}

export interface ApiClient {
  heartbeat(input: HeartbeatRequest): Promise<HeartbeatResponse>;
  getConfig(): Promise<ExtensionConfig>;
  uploadSnapshot(input: SnapshotRequestPayload): Promise<SnapshotResponse>;
  sendEvents(input: EventBatchRequest): Promise<EventBatchResponse>;
  claimCommands(input: ClaimRequest): Promise<ClaimResponse>;
  completeCommand(
    operationId: string,
    input: CompletionRequest,
  ): Promise<void>;
}

export interface BookmarkAdapter {
  getSubtree(browserNodeId: string): Promise<BrowserNode>;
  apply(command: ExtensionCommand): Promise<CommandExecutionResult>;
}

export interface StorageRepository {
  getSettings(): Promise<ExtensionSettings | null>;
  saveSettings(value: ExtensionSettings): Promise<void>;
  getServerConfig(): Promise<ServerConfig | null>;
  saveServerConfig(value: ServerConfig): Promise<void>;
  getShortcutEditorState(): Promise<ShortcutEditorState | null>;
  saveShortcutEditorState(state: ShortcutEditorState): Promise<void>;
  clearShortcutEditorState(): Promise<void>;
  getLastActiveFolder(): Promise<string>;
  saveLastActiveFolder(folderId: string): Promise<void>;
  enqueueEvent(event: ExtensionEvent): Promise<void>;
  getReadyEvents(limit: number, now: Date): Promise<OutboxEntry[]>;
  acknowledgeEvents(eventIds: string[]): Promise<void>;
  saveCorrelation(value: CommandCorrelation): Promise<void>;
  getCorrelation(operationId: string): Promise<CommandCorrelation | null>;
  getAllCorrelations(): Promise<CommandCorrelation[]>;
  pruneExpiredCorrelations(now: Date): Promise<void>;
  updateSyncStatus(value: SyncStatus): Promise<void>;
  getSyncStatus(): Promise<SyncStatus | null>;
  getSnapshotState(): Promise<{ lastRequestId: string | null; preparing: SnapshotRootPayload[] | null }>;
  saveSnapshotState(state: { lastRequestId: string | null; preparing: SnapshotRootPayload[] | null }): Promise<void>;
  getBackupState(): Promise<BackupState>;
  saveBackupState(state: BackupState): Promise<void>;
  getBackupSettings(): Promise<BackupSettings>;
  saveBackupSettings(settings: BackupSettings): Promise<void>;
  clearAll(): Promise<void>;
}
