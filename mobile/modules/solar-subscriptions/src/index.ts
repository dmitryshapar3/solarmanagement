import { NativeModule, requireOptionalNativeModule } from "expo";
import type { BillingSnapshot, PurchaseResult } from "../../../src/features/subscription/billingPolicy";

type SubscriptionEvents = { entitlementsChanged: (snapshot: BillingSnapshot) => void };

declare class SolarSubscriptionsModule extends NativeModule<SubscriptionEvents> {
  getSnapshotAsync(): Promise<BillingSnapshot>;
  getEntitlementsAsync(): Promise<BillingSnapshot>;
  purchaseAsync(productId: string, appAccountToken: string): Promise<PurchaseResult>;
  finishAsync(transactionId: string, appAccountToken: string): Promise<void>;
  restoreAsync(): Promise<BillingSnapshot>;
  manageAsync(): Promise<void>;
}

export const solarSubscriptions = requireOptionalNativeModule<SolarSubscriptionsModule>("SolarSubscriptions");
