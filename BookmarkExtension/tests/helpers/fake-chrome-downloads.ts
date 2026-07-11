export class FakeDownloads {
  public calls: { method: string; args: unknown[] }[] = [];
  public failRemoveIds = new Set<number>();
  private nextId = 1;

  async download(options: { url: string; filename: string; conflictAction?: "uniquify" }): Promise<number> {
    this.calls.push({ method: "download", args: [options] });
    return this.nextId++;
  }

  async removeFile(downloadId: number): Promise<void> {
    this.calls.push({ method: "removeFile", args: [downloadId] });
    if (this.failRemoveIds.has(downloadId)) {
      throw new Error("File not found");
    }
  }
}
