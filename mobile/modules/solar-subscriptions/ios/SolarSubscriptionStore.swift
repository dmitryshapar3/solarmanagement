import Foundation
import StoreKit
import UIKit

@MainActor
final class SolarSubscriptionStore {
  static let productIDs = ["com.dshapar.solar.monthly", "com.dshapar.solar.yearly"]
  private var products: [Product] = []
  private var updatesTask: Task<Void, Never>?
  private var operationInProgress = false
  private var catalogError: String?
  var changed: (([String: Any]) -> Void)?

  deinit { updatesTask?.cancel() }

  func start() {
    guard updatesTask == nil else { return }
    updatesTask = Task { [weak self] in
      for await result in Transaction.updates {
        guard let self, !Task.isCancelled else { return }
        guard case .verified(let transaction) = result,
          Self.productIDs.contains(transaction.productID) else { continue }
        // Publish signed records for authenticated server verification. Delivery
        // stays unfinished until the server acknowledges this account's receipt.
        let snapshot = await self.snapshot(reloadProducts: false)
        self.changed?(snapshot)
      }
    }
  }

  func stop() {
    updatesTask?.cancel()
    updatesTask = nil
    changed = nil
  }

  func snapshot(reloadProducts: Bool = true) async -> [String: Any] {
    start()
    if reloadProducts {
      do {
        products = try await Product.products(for: Self.productIDs)
        catalogError = catalogReady ? nil : "Subscriptions are temporarily unavailable. Please try again later."
      } catch {
        products = []
        catalogError = "The App Store could not load subscriptions. Please try again."
      }
    }
    var displayProducts: [[String: Any]] = []
    for id in Self.productIDs {
      if let product = products.first(where: { $0.id == id }), let subscription = product.subscription {
        var introductoryOffer: Any = NSNull()
        if let offer = subscription.introductoryOffer {
          introductoryOffer = [
            "paymentMode": paymentMode(offer.paymentMode),
            "periodUnit": periodUnit(offer.period.unit),
            "periodValue": offer.period.value,
            "periodCount": offer.periodCount
          ] as [String: Any]
        }
        displayProducts.append([
          "id": product.id,
          "title": product.displayName,
          "displayPrice": product.displayPrice,
          "currencyCode": product.priceFormatStyle.currencyCode,
          "type": product.type == .autoRenewable ? "autoRenewable" : "unsupported",
          "periodUnit": periodUnit(subscription.subscriptionPeriod.unit),
          "periodValue": subscription.subscriptionPeriod.value,
          "subscriptionGroupId": subscription.subscriptionGroupID,
          "introductoryOffer": introductoryOffer,
          "introEligible": await subscription.isEligibleForIntroOffer
        ])
      }
    }
    var entitlements: [[String: Any]] = []
    for await result in Transaction.currentEntitlements {
      guard case .verified(let transaction) = result,
        Self.productIDs.contains(transaction.productID),
        transaction.productType == .autoRenewable,
        transaction.revocationDate == nil, !transaction.isUpgraded else { continue }
      // Do not compare expirationDate to the device clock. currentEntitlements
      // also contains verified subscriptions currently in Apple's grace period.
      entitlements.append(record(transaction, signedTransaction: result.jwsRepresentation, source: "storekit-current-entitlements"))
    }
    var pendingTransactions: [[String: Any]] = []
    for await result in Transaction.unfinished {
      guard case .verified(let transaction) = result,
        Self.productIDs.contains(transaction.productID),
        transaction.productType == .autoRenewable else { continue }
      pendingTransactions.append(record(transaction, signedTransaction: result.jwsRepresentation, source: "storekit-unfinished"))
    }
    return [
      "products": displayProducts,
      "entitlements": entitlements,
      "pendingTransactions": pendingTransactions,
      "catalogReady": catalogReady,
      "canMakePayments": AppStore.canMakePayments,
      "catalogError": catalogError as Any? ?? NSNull(),
      "checkedAt": ISO8601DateFormatter().string(from: Date())
    ]
  }

  func purchase(productID: String, appAccountToken: String?) async throws -> [String: Any] {
    guard Self.productIDs.contains(productID) else { throw failure("Choose a valid subscription.") }
    guard !operationInProgress else { throw failure("An App Store action is already in progress.") }
    operationInProgress = true
    defer { operationInProgress = false }
    _ = await snapshot()
    guard AppStore.canMakePayments, catalogReady,
      let product = products.first(where: { $0.id == productID }) else {
      throw failure("Subscriptions are temporarily unavailable. Please try again later.")
    }
    guard let appAccountToken, let token = UUID(uuidString: appAccountToken), token != UUID(uuidString: "00000000-0000-0000-0000-000000000000") else {
      throw failure("The subscription account could not be identified.")
    }
    if let subscription = product.subscription, subscription.introductoryOffer?.paymentMode == .freeTrial,
      await subscription.isEligibleForIntroOffer {
      throw failure("Subscriptions are temporarily unavailable. Please try again later.")
    }
    guard let scene = UIApplication.shared.connectedScenes.first(where: { $0.activationState == .foregroundActive }) else {
      throw failure("An App Store action requires the app to be in the foreground.")
    }
    let options: Set<Product.PurchaseOption> = [.appAccountToken(token)]
    let purchaseResult: Product.PurchaseResult
    if #available(iOS 17.0, *) { purchaseResult = try await product.purchase(confirmIn: scene, options: options) }
    else { purchaseResult = try await product.purchase(options: options) }
    switch purchaseResult {
    case .success(let result):
      guard case .verified(let transaction) = result, transaction.productID == productID else {
        throw failure("The App Store could not verify this purchase. Please restore purchases or contact support.")
      }
      let updated = await snapshot()
      changed?(updated)
      return ["outcome": "purchased", "snapshot": updated]
    case .pending:
      return ["outcome": "pending", "snapshot": await snapshot(reloadProducts: false)]
    case .userCancelled:
      return ["outcome": "cancelled", "snapshot": await snapshot(reloadProducts: false)]
    @unknown default:
      throw failure("The App Store returned an unsupported purchase result. Please try again.")
    }
  }

  func finish(transactionID: String, appAccountToken: String) async throws {
    guard let identifier = UInt64(transactionID), let token = UUID(uuidString: appAccountToken), token != UUID(uuidString: "00000000-0000-0000-0000-000000000000") else {
      throw failure("The subscription account could not be identified.")
    }
    for await result in Transaction.unfinished {
      guard case .verified(let transaction) = result, transaction.id == identifier,
        Self.productIDs.contains(transaction.productID), transaction.productType == .autoRenewable else { continue }
      guard transaction.appAccountToken == token else {
        throw failure("The transaction belongs to another Solar account.")
      }
      await transaction.finish()
      return
    }
    // A repeated server acknowledgement of an already finished record is safe.
  }

  func restore() async throws -> [String: Any] {
    guard !operationInProgress else { throw failure("An App Store action is already in progress.") }
    operationInProgress = true
    defer { operationInProgress = false }
    // Only this explicit user action invokes sync; ordinary startup uses the
    // automatically updated StoreKit currentEntitlements sequence.
    try await AppStore.sync()
    let updated = await snapshot()
    changed?(updated)
    return updated
  }

  func manage() async throws {
    guard !ProcessInfo.processInfo.isiOSAppOnMac,
      let scene = UIApplication.shared.connectedScenes
        .compactMap({ $0 as? UIWindowScene })
        .first(where: { $0.activationState == .foregroundActive }) else {
      throw failure("Manage your subscription in the App Store account settings.")
    }
    try await AppStore.showManageSubscriptions(in: scene)
    changed?(await snapshot())
  }

  private var catalogReady: Bool {
    let expected = Self.productIDs.compactMap { id in products.first(where: { $0.id == id }) }
    guard expected.count == Self.productIDs.count,
      expected.allSatisfy({ $0.type == .autoRenewable && $0.subscription != nil }),
      let monthly = expected[0].subscription, let yearly = expected[1].subscription else { return false }
    return monthly.subscriptionPeriod.unit == .month && monthly.subscriptionPeriod.value == 1
      && yearly.subscriptionPeriod.unit == .year && yearly.subscriptionPeriod.value == 1
      && monthly.subscriptionGroupID == yearly.subscriptionGroupID
  }

  private func record(_ transaction: Transaction, signedTransaction: String, source: String) -> [String: Any] {
    [
      "productId": transaction.productID,
      "transactionId": String(transaction.id),
      "originalTransactionId": String(transaction.originalID),
      "verified": true,
      "source": source,
      "expiresAt": transaction.expirationDate.map { ISO8601DateFormatter().string(from: $0) } as Any? ?? NSNull(),
      "appAccountToken": transaction.appAccountToken?.uuidString as Any? ?? NSNull(),
      "signedTransaction": signedTransaction
    ]
  }

  private func periodUnit(_ unit: Product.SubscriptionPeriod.Unit) -> String {
    switch unit {
    case .day: return "day"
    case .week: return "week"
    case .month: return "month"
    case .year: return "year"
    @unknown default: return "unknown"
    }
  }

  private func paymentMode(_ mode: Product.SubscriptionOffer.PaymentMode) -> String {
    switch mode {
    case .freeTrial: return "freeTrial"
    case .payAsYouGo: return "payAsYouGo"
    case .payUpFront: return "payUpFront"
    default: return "unknown"
    }
  }

  private func failure(_ message: String) -> NSError {
    NSError(domain: "SolarSubscriptions", code: 1, userInfo: [NSLocalizedDescriptionKey: message])
  }
}
