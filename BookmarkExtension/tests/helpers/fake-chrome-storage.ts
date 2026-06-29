export class FakeStorage {
  private data = new Map<string, unknown>();
  public calls: { method: string; args: unknown[] }[] = [];

  async get(keys: string | string[] | null): Promise<Record<string, unknown>> {
    this.calls.push({ method: "get", args: [keys] });
    const result: Record<string, unknown> = {};

    if (keys === null) {
      for (const [key, value] of this.data) {
        result[key] = value;
      }
      return result;
    }

    const keyArray = Array.isArray(keys) ? keys : [keys];
    for (const key of keyArray) {
      if (this.data.has(key)) {
        result[key] = this.data.get(key);
      }
    }
    return result;
  }

  async set(items: Record<string, unknown>): Promise<void> {
    this.calls.push({ method: "set", args: [items] });
    for (const [key, value] of Object.entries(items)) {
      this.data.set(key, value);
    }
  }

  async remove(keys: string | string[]): Promise<void> {
    this.calls.push({ method: "remove", args: [keys] });
    const keyArray = Array.isArray(keys) ? keys : [keys];
    for (const key of keyArray) {
      this.data.delete(key);
    }
  }

  async clear(): Promise<void> {
    this.calls.push({ method: "clear", args: [] });
    this.data.clear();
  }

  has(key: string): boolean {
    return this.data.has(key);
  }

  getRaw(key: string): unknown {
    return this.data.get(key);
  }

  reset(): void {
    this.data.clear();
    this.calls = [];
  }
}
