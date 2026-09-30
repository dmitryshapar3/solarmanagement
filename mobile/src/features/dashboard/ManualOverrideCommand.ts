export type ManualOverrideDirection = "on" | "off";

type CommandCallbacks = {
  busyChanged: (value: ManualOverrideDirection | null) => void;
  completed: () => Promise<void> | void;
  failed: (error: unknown) => void;
};

export class ManualOverrideCommand {
  private active = false;
  private viewGeneration = 0;
  private pending: { direction: ManualOverrideDirection; generation: number } | null = null;

  get busy(): ManualOverrideDirection | null {
    return this.pending?.direction ?? null;
  }

  activate(): void {
    this.active = true;
  }

  deactivate(): void {
    // Losing focus cannot cancel a device mutation that may already be executing remotely.
    this.active = false;
    ++this.viewGeneration;
  }

  async run(direction: ManualOverrideDirection, send: () => Promise<unknown>, callbacks: CommandCallbacks): Promise<boolean> {
    if (!this.active || this.pending) return false;
    const request = { direction, generation: ++this.viewGeneration };
    this.pending = request;
    callbacks.busyChanged(direction);
    const viewIsCurrent = () => this.active && request.generation === this.viewGeneration;
    try {
      await send();
      if (viewIsCurrent()) await callbacks.completed();
    } catch (error) {
      if (viewIsCurrent()) callbacks.failed(error);
    } finally {
      if (this.pending === request) {
        this.pending = null;
        if (this.active) callbacks.busyChanged(null);
      }
    }
    return true;
  }
}
