export const CURRENT_SCHEMA_VERSION = 1;

type StorageLike = {
  get(keys: string | string[] | null): Promise<Record<string, unknown>>;
  set(items: Record<string, unknown>): Promise<void>;
};

export async function migrate(storage: StorageLike): Promise<void> {
  const result = await storage.get("bm.schemaVersion");
  const currentVersion = result["bm.schemaVersion"];

  if (currentVersion === undefined || currentVersion === null) {
    await storage.set({ "bm.schemaVersion": CURRENT_SCHEMA_VERSION });
    return;
  }

  const version = Number(currentVersion);
  if (!Number.isInteger(version)) {
    throw new Error("Corrupted schema version in storage");
  }

  if (version > CURRENT_SCHEMA_VERSION) {
    throw new Error(
      `Unknown schema version ${version}; extension update required`,
    );
  }
}
