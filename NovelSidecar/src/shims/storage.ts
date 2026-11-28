class MemoryStorage {
  private db = new Map<string, { value: unknown; expires?: number }>();

  set<T>(key: string, value: T, expires?: Date | number): void {
    this.db.set(key, {
      value,
      expires: expires instanceof Date ? expires.getTime() : expires,
    });
  }

  get<T = unknown>(key: string): T | undefined {
    const item = this.db.get(key);
    if (!item) return undefined;
    if (item.expires && Date.now() > item.expires) {
      this.db.delete(key);
      return undefined;
    }
    return item.value as T;
  }

  getAllKeys(): string[] {
    return Array.from(this.db.keys());
  }

  delete(key: string): void {
    this.db.delete(key);
  }

  clearAll(): void {
    this.db.clear();
  }
}

class NoopWebStorage {
  get(): Record<string, string> {
    return {};
  }
}

export const storage = new MemoryStorage();
export const localStorage = new NoopWebStorage();
export const sessionStorage = new NoopWebStorage();
