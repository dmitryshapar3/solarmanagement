import { transactionsForAccount } from "./billingPolicy";
import type { BillingAccess, BillingSnapshot } from "./billingPolicy";
import type { BillingAccountApi, SubscriptionStore } from "./SubscriptionController";

/** Durable server acknowledgement precedes finishing a native delivery. */
export class TransactionReceiptSynchronizer {
  private readonly verified = new Set<string>();
  private readonly acknowledged = new Set<string>();
  constructor(private readonly api: BillingAccountApi, private readonly native: SubscriptionStore | null) {}
  async synchronize(snapshot: BillingSnapshot, initial: BillingAccess, signal: AbortSignal,
    current: () => boolean, beforeVerify: () => void): Promise<BillingAccess> {
    let access = initial;
    if (!this.native || !initial.appleSubscriptionsEnabled) return access;
    for (const transaction of transactionsForAccount(snapshot, initial.appAccountToken)) {
      if (!current()) return access;
      if (this.acknowledged.has(transaction.signedTransaction)) continue;
      if (!this.verified.has(transaction.signedTransaction)) {
        beforeVerify();
        access = await this.api.verifyAppleTransaction(transaction.signedTransaction, signal);
        if (!current()) return access;
        this.verified.add(transaction.signedTransaction);
      }
      // Expired access is still a successful server receipt, not a failed purchase delivery.
      await this.native.finishAsync(transaction.transactionId, initial.appAccountToken);
      if (current()) this.acknowledged.add(transaction.signedTransaction);
    }
    return access;
  }
}
