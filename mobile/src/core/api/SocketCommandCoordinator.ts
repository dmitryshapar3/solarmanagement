import { translate as t } from "../i18n";
import type { IntegrationApi, RuleConflictChoice, SocketCommandReceipt } from "./IntegrationApi";

type CommandApi = Pick<IntegrationApi, "sendDeviceCommand" | "getDeviceCommand" | "getUnresolvedCommands" | "releaseDeviceCommand">;
export type SocketCommandState = SocketCommandReceipt & { message?: string };

/** Keeps one unresolved operation per device across screens; only receipt reads recover it. */
export class SocketCommandCoordinator {
  private readonly receipts = new Map<string, SocketCommandState>();
  private readonly running = new Set<string>();
  private readonly listeners = new Set<() => void>();
  private generation = 0;

  constructor(private readonly api: CommandApi,
    private readonly createId: () => string | Promise<string> = async () => (await import("expo-crypto")).randomUUID()) {}

  reset(): void {
    ++this.generation;
    this.receipts.clear();
    this.running.clear();
    this.notify();
  }

  subscribe(listener: () => void): () => void {
    this.listeners.add(listener);
    return () => { this.listeners.delete(listener); };
  }

  get(deviceId: string): SocketCommandState | null {
    const value = this.receipts.get(deviceId);
    return value ? { ...value } : null;
  }

  get sessionEpoch(): number { return this.generation; }
  isRunning(deviceId: string): boolean { return this.running.has(deviceId); }

  async recover(deviceId: string): Promise<void> {
    const generation = this.generation;
    const previous = this.receipts.get(deviceId);
    const results = await this.api.getUnresolvedCommands(deviceId);
    if (generation !== this.generation || this.running.has(deviceId) || this.receipts.get(deviceId) !== previous) return;
    const unresolved = results.map(receipt => validateReceipt(receipt, deviceId)).filter(commandUnresolved);
    if (!unresolved.length || previous && commandUnresolved(previous)) return;
    const oldest = unresolved.sort((a, b) => Date.parse(a.createdAt) - Date.parse(b.createdAt))[0]!;
    this.receipts.set(deviceId, oldest);
    this.notify();
  }

  async send(deviceId: string, isOn: boolean, onRuleConflict?: RuleConflictChoice): Promise<SocketCommandState> {
    const existing = this.receipts.get(deviceId);
    if (this.running.has(deviceId) || existing && commandUnresolved(existing)) {
      throw new Error(t("A previous command is unconfirmed. Check its result before sending another command."));
    }
    const generation = this.generation;
    this.running.add(deviceId);
    this.notify();
    let pending: SocketCommandState | null = null;
    try {
      const recovered = (await this.api.getUnresolvedCommands(deviceId)).map(receipt => validateReceipt(receipt, deviceId))
        .filter(commandUnresolved).sort((a, b) => Date.parse(a.createdAt) - Date.parse(b.createdAt));
      if (generation !== this.generation) throw new Error(t("The command session changed."));
      if (recovered.length) {
        this.receipts.set(deviceId, recovered[0]!);
        throw new Error(t("A previous command is unconfirmed. Check its result before sending another command."));
      }
      const commandId = await this.createId();
      if (generation !== this.generation) throw new Error(t("The command session changed."));
      pending = { commandId, deviceId, isOn, status: "pending", rejection: null, createdAt: new Date().toISOString(), completedAt: null };
      this.receipts.set(deviceId, pending);
      this.notify();
      const receipt = validateReceipt(await this.api.sendDeviceCommand(deviceId, commandId, isOn, onRuleConflict), deviceId, commandId, isOn);
      if (generation !== this.generation) throw new Error(t("The command session changed."));
      this.receipts.set(deviceId, receipt);
      return { ...receipt };
    } catch (exception) {
      if (generation !== this.generation || !pending) throw exception;
      const uncertain = { ...pending, status: "uncertain", message: "The command may have executed. Check its result before sending another command." };
      this.receipts.set(deviceId, uncertain);
      return { ...uncertain };
    } finally {
      if (generation === this.generation) { this.running.delete(deviceId); this.notify(); }
    }
  }

  async check(deviceId: string, release = false): Promise<SocketCommandState> {
    const previous = this.receipts.get(deviceId);
    if (!previous) throw new Error(t("No command is awaiting a result for this device."));
    if (release && previous.status !== "uncertain") throw new Error(t("This command is still pending. Check its result before allowing another command."));
    if (this.running.has(deviceId)) throw new Error(t("A command check is already in progress."));
    const generation = this.generation;
    this.running.add(deviceId);
    this.notify();
    try {
      const response = release ? await this.api.releaseDeviceCommand(deviceId, previous.commandId)
        : await this.api.getDeviceCommand(deviceId, previous.commandId);
      const result = validateReceipt(response, deviceId, previous.commandId, previous.isOn);
      if (generation !== this.generation) throw new Error(t("The command session changed."));
      this.receipts.set(deviceId, result);
      return { ...result };
    } catch (exception) {
      if (generation !== this.generation) throw exception;
      const retained = { ...previous, message: previous.status === "pending"
        ? "The command remains pending. Its latest result is unavailable; check again before sending another command."
        : "The latest result is unavailable. The previous command result is retained." };
      this.receipts.set(deviceId, retained);
      return { ...retained };
    } finally {
      if (generation === this.generation) { this.running.delete(deviceId); this.notify(); }
    }
  }

  private notify(): void {
    for (const listener of this.listeners) {
      try { listener(); }
      catch { /* View failures cannot release or replace an unresolved command. */ }
    }
  }
}

function validateReceipt(value: SocketCommandReceipt, deviceId: string, commandId?: string, isOn?: boolean): SocketCommandState {
  if (!value || typeof value.commandId !== "string" || typeof value.deviceId !== "string"
    || value.deviceId.toLowerCase() !== deviceId.toLowerCase() || typeof value.isOn !== "boolean"
    || commandId !== undefined && value.commandId.toLowerCase() !== commandId.toLowerCase()
    || isOn !== undefined && value.isOn !== isOn || typeof value.status !== "string"
    || !["pending", "acknowledged", "uncertain", "uncertain_closed", "rejected"].includes(value.status)) {
    throw new Error(t("The command response did not identify this operation."));
  }
  return { ...value };
}

export function commandUnresolved(receipt: SocketCommandState | null): boolean {
  return receipt?.status === "pending" || receipt?.status === "uncertain";
}

export function socketCommandMessage(receipt: SocketCommandState): string {
  if (receipt.message) return receipt.message;
  if (receipt.status === "acknowledged") return "The provider acknowledged the command. Device state comes from the next observation.";
  if (receipt.status === "rejected") return "The provider rejected this command. No successful switch was confirmed.";
  if (receipt.status === "uncertain_closed") return "Another command is allowed. The previous command result remains unknown and its operation may still finish.";
  if (receipt.status === "pending") return "The command is pending. Check its result before sending another command.";
  return "The command is unconfirmed. Check its result before sending another command.";
}
