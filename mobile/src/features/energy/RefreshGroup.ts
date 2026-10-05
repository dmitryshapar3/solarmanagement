/** A second refresh cannot start while any member of the first group is still running. */
export class RefreshGroup {
  private pending = false;
  async run(loading: boolean, operations: ReadonlyArray<() => Promise<unknown> | void>): Promise<boolean> {
    if (loading || this.pending) return false;
    this.pending = true;
    try {
      const results = await Promise.allSettled(operations.map(operation => Promise.resolve().then(operation)));
      const failed = results.find(result => result.status === "rejected");
      if (failed?.status === "rejected") throw failed.reason;
      return true;
    } finally { this.pending = false; }
  }
}
