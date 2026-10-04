import type { BillingAccess, BillingSnapshot, PurchaseResult } from "./billingPolicy";
import { canPurchaseSubscriptions, hasServerAccess } from "./billingPolicy";
import { TransactionReceiptSynchronizer } from "./TransactionReceiptSynchronizer";

export type BillingAction = "purchase" | "restore" | "manage";
export interface BillingAccountApi {
  getBillingAccess(signal?: AbortSignal): Promise<BillingAccess>;
  verifyAppleTransaction(receipt: string, signal?: AbortSignal): Promise<BillingAccess>;
}
export interface SubscriptionStore {
  getSnapshotAsync(): Promise<BillingSnapshot>;
  purchaseAsync(productId: string, accountToken: string): Promise<PurchaseResult>;
  restoreAsync(): Promise<BillingSnapshot>;
  manageAsync(): Promise<void>;
  finishAsync(transactionId: string, accountToken: string): Promise<void>;
}
export type SubscriptionState = {
  access: BillingAccess | null; snapshot: BillingSnapshot | null; isChecking: boolean;
  busy: BillingAction | null; error: string | null; notice: string | null;
};

/** Account-scoped orchestration. React and native lifecycle events are adapters. */
export class SubscriptionController {
  private state: SubscriptionState = { access: null, snapshot: null, isChecking: true, busy: null, error: null, notice: null };
  private readonly observers = new Set<(state: SubscriptionState) => void>();
  private readonly receipts: TransactionReceiptSynchronizer;
  private pending: AbortController | null = null;
  private action: BillingAction | null = null;
  private active = false;
  private foreground = true;
  private requested = false;
  private checkedAt = 0;
  private deadline: ReturnType<typeof setTimeout> | null = null;

  constructor(private readonly api: BillingAccountApi, private readonly native: SubscriptionStore | null,
    private readonly elapsedNow: () => number = () => performance.now()) {
    this.receipts = new TransactionReceiptSynchronizer(api, native);
  }
  get value(): SubscriptionState { return this.state; }
  get canUseAppStore(): boolean { return this.native !== null && this.state.access?.appleSubscriptionsEnabled === true; }
  get canPurchase(): boolean { return this.canUseAppStore && !this.state.isChecking && canPurchaseSubscriptions(this.state.snapshot); }

  subscribe(observer: (state: SubscriptionState) => void): () => void {
    this.observers.add(observer); observer(this.state);
    return () => { this.observers.delete(observer); };
  }
  start(): void { this.active = true; void this.refresh(); }
  stop(): void {
    this.active = false; this.pending?.abort(); this.pending = null; this.clearDeadline();
    this.requested = false; this.observers.clear();
  }
  private current = (controller: AbortController): boolean => this.active && this.pending === controller && !controller.signal.aborted;
  private publish(next: Partial<SubscriptionState>): void {
    if (!this.active) return;
    this.state = { ...this.state, ...next };
    if ("access" in next) this.scheduleDeadline();
    for (const observer of this.observers) observer(this.state);
  }
  private clearDeadline(): void { if (this.deadline !== null) clearTimeout(this.deadline); this.deadline = null; }
  private scheduleDeadline(): void {
    this.clearDeadline();
    const access = this.state.access;
    if (!hasServerAccess(access) || !access?.accessValidUntil) return;
    const remaining = Date.parse(access.accessValidUntil) - Date.parse(access.serverNow) - (this.elapsedNow() - this.checkedAt);
    if (!Number.isFinite(remaining)) { this.state = { ...this.state, access: null }; return; }
    this.deadline = setTimeout(() => { this.publish({ access: null }); void this.refresh(); }, Math.max(0, Math.min(remaining, 2_147_483_647)));
  }
  invalidate(): void {
    this.pending?.abort(); this.pending = null; this.requested = false;
    this.publish({ access: null, isChecking: true });
  }
  billingDenied(): void { this.invalidate(); void this.refresh(); }
  appStateChanged(isForeground: boolean): void {
    this.foreground = isForeground;
    if (this.action) { this.publish({ access: null }); this.requested = true; }
    else this.invalidate();
    if (isForeground) void this.refresh();
  }
  private retryRequested(): void {
    if (this.active && this.requested && this.foreground) { this.requested = false; void this.refresh(); }
  }
  refresh = async (): Promise<void> => {
    if (!this.active) return;
    if (this.pending || this.action) { this.requested = true; return; }
    const controller = new AbortController(); this.pending = controller;
    this.publish({ isChecking: true });
    try {
      this.checkedAt = this.elapsedNow();
      let access = await this.api.getBillingAccess(controller.signal);
      if (this.native && access.appleSubscriptionsEnabled) {
        const snapshot = await this.native.getSnapshotAsync();
        if (!this.current(controller)) return;
        this.publish({ snapshot });
        access = await this.synchronize(snapshot, access, controller);
      }
      if (this.current(controller)) this.publish({ access, error: null });
    } catch {
      if (this.current(controller)) this.publish({ access: null, error: "Your account access could not be checked. Connect to the server and try again." });
    } finally {
      if (this.pending === controller) this.pending = null;
      if (this.active && !controller.signal.aborted) this.publish({ isChecking: false });
      this.retryRequested();
    }
  };
  private synchronize(snapshot: BillingSnapshot, access: BillingAccess, controller: AbortController): Promise<BillingAccess> {
    return this.receipts.synchronize(snapshot, access, controller.signal, () => this.current(controller), () => { this.checkedAt = this.elapsedNow(); });
  }
  private async run(name: BillingAction, operation: (controller: AbortController) => Promise<void>): Promise<void> {
    if (this.pending || this.action || !this.active) return;
    const controller = new AbortController(); this.pending = controller; this.action = name;
    this.publish({ busy: name, error: null, notice: null });
    try { await operation(controller); }
    catch (error) {
      if (this.current(controller)) this.publish({ access: null, error: error instanceof Error ? error.message : "The App Store action could not be completed." });
    } finally {
      this.action = null;
      if (this.pending === controller) this.pending = null;
      if (this.active) this.publish({ busy: null, isChecking: false });
      this.retryRequested();
    }
  }
  purchase = async (productId: string): Promise<void> => {
    const access = this.state.access;
    if (!this.native || !this.canPurchase || !access) {
      this.publish({ error: "Subscriptions are temporarily unavailable. Please try again later." }); return;
    }
    const native = this.native;
    await this.run("purchase", async controller => {
      const result = await native.purchaseAsync(productId, access.appAccountToken);
      if (!this.current(controller)) return;
      this.publish({ snapshot: result.snapshot }); this.checkedAt = this.elapsedNow();
      const authoritative = await this.api.getBillingAccess(controller.signal);
      const next = await this.synchronize(result.snapshot, authoritative, controller);
      if (!this.current(controller)) return;
      this.publish({ access: next });
      if (result.outcome === "pending") this.publish({ notice: "Your purchase is awaiting App Store approval. Access starts after server verification." });
      else if (result.outcome === "purchased" && next.status !== "active") this.publish({ notice: "The purchase has not activated this account. Restore purchases or contact support." });
    });
  };
  restore = async (): Promise<void> => {
    if (!this.native || !this.canUseAppStore || !this.state.access) return;
    const native = this.native;
    await this.run("restore", async controller => {
      const snapshot = await native.restoreAsync();
      if (!this.current(controller)) return;
      this.publish({ snapshot }); this.checkedAt = this.elapsedNow();
      const authoritative = await this.api.getBillingAccess(controller.signal);
      const access = await this.synchronize(snapshot, authoritative, controller);
      if (!this.current(controller)) return;
      this.publish({ access, notice: access.status === "active" ? "Your subscription has been restored."
        : "No active subscription was found for this Solar account. Use the account that made the purchase." });
    });
  };
  manage = async (): Promise<void> => {
    if (!this.native || !this.canUseAppStore) return;
    const native = this.native;
    await this.run("manage", async () => { await native.manageAsync(); this.requested = true; });
  };
}
