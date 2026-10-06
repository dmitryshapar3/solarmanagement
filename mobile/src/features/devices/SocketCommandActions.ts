import type { SocketCommandCoordinator, SocketCommandState } from "../../core/api/SocketCommandCoordinator";
import type { RuleConflictChoice } from "../../core/api/IntegrationApi";
import { ManualOverrideCommand, type ManualOverrideDirection } from "../dashboard/ManualOverrideCommand";

type Commands = Pick<SocketCommandCoordinator, "send" | "check" | "get" | "isRunning" | "sessionEpoch">;
type Callbacks = { changed: () => void; started: () => void; acknowledged: (deviceId: string) => Promise<void>;
  failed: (error: unknown, fallback: string) => void };

/** View lifecycle only. Receipt recovery and idempotency belong to the command coordinator. */
export class SocketCommandActions {
  private readonly gates = new Map<string, ManualOverrideCommand>();
  private active = false;
  constructor(private readonly commands: Commands, private readonly callbacks: Callbacks) {}
  activate(): void { this.active = true; for (const gate of this.gates.values()) gate.activate(); }
  deactivate(): void { this.active = false; for (const gate of this.gates.values()) gate.deactivate(); }
  busy(deviceId: string | null): ManualOverrideDirection | null { return deviceId ? this.gates.get(deviceId)?.busy ?? null : null; }
  send(deviceId: string, isOn: boolean, onRuleConflict?: RuleConflictChoice): Promise<boolean> {
    return this.run(deviceId, isOn, () => this.commands.send(deviceId, isOn, onRuleConflict), "Unable to change socket state.");
  }
  check(deviceId: string, release = false): Promise<boolean> {
    return this.run(deviceId, this.commands.get(deviceId)?.isOn ?? false,
      () => this.commands.check(deviceId, release), "Unable to check the command result.");
  }
  private async run(deviceId: string, isOn: boolean, operation: () => Promise<SocketCommandState>, fallback: string): Promise<boolean> {
    if (!this.active || this.commands.isRunning(deviceId)) return false;
    let gate = this.gates.get(deviceId);
    if (!gate) { gate = new ManualOverrideCommand(); gate.activate(); this.gates.set(deviceId, gate); }
    const epoch = this.commands.sessionEpoch;
    let acknowledged = false;
    return gate.run(isOn ? "on" : "off", async () => { acknowledged = (await operation()).status === "acknowledged"; }, {
      busyChanged: direction => { if (direction !== null) this.callbacks.started(); this.callbacks.changed(); },
      completed: async () => { if (epoch === this.commands.sessionEpoch && acknowledged) await this.callbacks.acknowledged(deviceId); },
      failed: error => { if (epoch === this.commands.sessionEpoch) this.callbacks.failed(error, fallback); }
    });
  }
}
