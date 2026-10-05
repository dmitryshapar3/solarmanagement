export type ActionSession = { readonly sessionEpoch: number; onSessionChange: (observer: () => void) => () => void };

export type ScopedActionContext = {
  signal: AbortSignal;
  isCurrent: () => boolean;
  publish: (update: () => void) => boolean;
};
type ActionCallbacks = {
  started?: () => void;
  failed?: (error: unknown) => void;
  finished?: () => void;
};

/** Fences view results; cancellation never means that a server mutation was rolled back. */
export class ScopedActionScope {
  private active = false;
  private generation = 0;
  private pending: AbortController | null = null;
  constructor(private readonly sessionEpoch: () => number, private readonly ownerCurrent: () => boolean) {}

  activate(): void { this.active = true; }
  cancel(): void { this.pending?.abort(); }
  invalidate(): void {
    ++this.generation;
    this.pending?.abort();
    this.pending = null;
  }
  dispose(): void { this.active = false; this.invalidate(); }
  capture(): () => boolean {
    const generation = this.generation, session = this.sessionEpoch();
    return () => this.active && this.ownerCurrent() && generation === this.generation && session === this.sessionEpoch();
  }
  async run(action: (context: ScopedActionContext) => Promise<void>, callbacks: ActionCallbacks = {}): Promise<boolean> {
    if (!this.active || !this.ownerCurrent() || this.pending) return false;
    const controller = new AbortController();
    this.pending = controller;
    const current = this.capture();
    const isCurrent = () => current() && !controller.signal.aborted;
    const context = { signal: controller.signal, isCurrent, publish: (update: () => void) => {
      if (!isCurrent()) return false;
      update(); return true;
    } };
    try {
      callbacks.started?.();
      await action(context);
    } catch (error) { if (isCurrent()) callbacks.failed?.(error); }
    finally {
      if (this.pending === controller) {
        this.pending = null;
        if (current()) callbacks.finished?.();
      }
    }
    return true;
  }
}
