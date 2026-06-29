import { describe, it, expect, beforeEach } from "vitest";
import { migrate, CURRENT_SCHEMA_VERSION } from "../../src/storage/migrations";
import { FakeStorage } from "../helpers/fake-chrome-storage";

describe("migrations", () => {
  let storage: FakeStorage;

  beforeEach(() => {
    storage = new FakeStorage();
  });

  it("sets schema version to current when no version exists", async () => {
    await migrate(storage);
    expect(storage.getRaw("bm.schemaVersion")).toBe(CURRENT_SCHEMA_VERSION);
  });

  it("does nothing when already at current version", async () => {
    await storage.set({ "bm.schemaVersion": CURRENT_SCHEMA_VERSION });
    await migrate(storage);
    expect(storage.getRaw("bm.schemaVersion")).toBe(CURRENT_SCHEMA_VERSION);
  });

  it("throws when schema version is newer than current", async () => {
    await storage.set({ "bm.schemaVersion": CURRENT_SCHEMA_VERSION + 1 });
    await expect(migrate(storage)).rejects.toThrow("Unknown schema version");
  });

  it("throws on corrupted schema version", async () => {
    await storage.set({ "bm.schemaVersion": "not-a-number" });
    await expect(migrate(storage)).rejects.toThrow("Corrupted schema version");
  });
});
