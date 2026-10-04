// Runs only against Xcode local StoreKit configuration: no Apple credentials or payments.
import XCTest
import UIKit
import StoreKit
import StoreKitTest

@MainActor
final class SolarStoreKitTests: XCTestCase {
  private var session: SKTestSession!
  private var store: SolarSubscriptionStore!
  private let monthly = "com.dshapar.solar.monthly"
  private let yearly = "com.dshapar.solar.yearly"

  override func setUp() async throws {
    session = try SKTestSession(configurationFileNamed: "SolarLocal")
    session.resetToDefaultState()
    session.clearTransactions()
    session.disableDialogs = true
    session.timeRate = .realTime
    store = SolarSubscriptionStore()
    let sceneDeadline = Date().addingTimeInterval(10)
    while !UIApplication.shared.connectedScenes.contains(where: { $0.activationState == .foregroundActive }), Date() < sceneDeadline {
      try await Task.sleep(nanoseconds: 100_000_000)
    }
    XCTAssertTrue(UIApplication.shared.connectedScenes.contains(where: { $0.activationState == .foregroundActive }),
      "The StoreKit test host must present an active scene before purchase and restore actions")
  }
  override func tearDown() async throws {
    store?.stop()
    session?.clearTransactions()
    session?.resetToDefaultState()
    store = nil
    session = nil
  }
  private func entitlements(_ snapshot: [String: Any]) -> [[String: Any]] { snapshot["entitlements"] as? [[String: Any]] ?? [] }
  private func products(_ snapshot: [String: Any]) -> [[String: Any]] { snapshot["products"] as? [[String: Any]] ?? [] }
  private func pending(_ snapshot: [String: Any]) -> [[String: Any]] { snapshot["pendingTransactions"] as? [[String: Any]] ?? [] }
  private func waitForAccess(_ expected: Bool, productID: String? = nil, timeout: TimeInterval = 10) async throws -> [String: Any] {
    let deadline = Date().addingTimeInterval(timeout)
    repeat {
      let value = await store.snapshot(reloadProducts: false)
      let active = entitlements(value).contains { productID == nil || $0["productId"] as? String == productID }
      if active == expected { return value }
      try await Task.sleep(nanoseconds: 100_000_000)
    } while Date() < deadline
    XCTFail("StoreKit current entitlements did not reach the expected access state")
    return await store.snapshot(reloadProducts: false)
  }
  private func assertVerified(_ value: [String: Any], productID: String, file: StaticString = #filePath, line: UInt = #line) throws {
    let item = try XCTUnwrap(entitlements(value).first { $0["productId"] as? String == productID }, file: file, line: line)
    XCTAssertEqual(item["verified"] as? Bool, true, file: file, line: line)
    XCTAssertEqual(item["source"] as? String, "storekit-current-entitlements", file: file, line: line)
    XCTAssertFalse((item["signedTransaction"] as? String ?? "").isEmpty, file: file, line: line)
  }

  func test01CatalogContainsBothPlansWithoutASecondIntroductoryTrial() async throws {
    let snapshot = await store.snapshot()
    XCTAssertEqual(snapshot["catalogReady"] as? Bool, true)
    XCTAssertTrue(entitlements(snapshot).isEmpty)
    XCTAssertEqual(products(snapshot).count, 2)
    for (id, unit, price) in [(monthly, "month", "$4.99"), (yearly, "year", "$29.99")] {
      let item = try XCTUnwrap(products(snapshot).first { $0["id"] as? String == id })
      XCTAssertEqual(item["type"] as? String, "autoRenewable")
      XCTAssertEqual(item["periodUnit"] as? String, unit)
      XCTAssertEqual(item["periodValue"] as? Int, 1)
      XCTAssertEqual(item["displayPrice"] as? String, price)
      XCTAssertTrue(item["introductoryOffer"] is NSNull)
    }
  }

  func test02MonthlyPurchaseRestoreAndCancellation() async throws {
    let token = UUID()
    let result = try await store.purchase(productID: monthly, appAccountToken: token.uuidString)
    XCTAssertEqual(result["outcome"] as? String, "purchased")
    let snapshot = try XCTUnwrap(result["snapshot"] as? [String: Any])
    try assertVerified(snapshot, productID: monthly)
    XCTAssertEqual(entitlements(snapshot).first?["appAccountToken"] as? String, token.uuidString)
    let transaction = try XCTUnwrap(session.allTransactions().first { $0.productIdentifier == monthly })
    try session.disableAutoRenewForTransaction(identifier: transaction.identifier)
    try assertVerified(await store.snapshot(reloadProducts: false), productID: monthly)
    // Successful app delivery acknowledges the verified receipt before a later restore action.
    // Unacknowledged delivery and account fencing are covered separately in test11.
    let receipt = try XCTUnwrap(pending(snapshot).first)
    try await store.finish(transactionID: try XCTUnwrap(receipt["transactionId"] as? String), appAccountToken: token.uuidString)
    try assertVerified(try await store.restore(), productID: monthly)
  }

  func test03YearlyPurchaseAndRevocation() async throws {
    let result = try await store.purchase(productID: yearly, appAccountToken: UUID().uuidString)
    XCTAssertEqual(result["outcome"] as? String, "purchased")
    try assertVerified(try XCTUnwrap(result["snapshot"] as? [String: Any]), productID: yearly)
    let transaction = try XCTUnwrap(session.allTransactions().first { $0.productIdentifier == yearly })
    try session.refundTransaction(identifier: transaction.identifier)
    let revoked = try await waitForAccess(false)
    XCTAssertTrue(entitlements(revoked).isEmpty)
  }

  func test04PendingAskToBuyDoesNotGrantUntilApproved() async throws {
    session.askToBuyEnabled = true
    let started = ProcessInfo.processInfo.systemUptime
    func trace(_ phase: String, snapshot: [String: Any]? = nil) {
      let elapsed = String(format: "%.3f", ProcessInfo.processInfo.systemUptime - started)
      if let snapshot {
        let active = self.entitlements(snapshot)
        let unfinished = self.pending(snapshot)
        let verifiedCount = (active + unfinished).filter {
          $0["verified"] as? Bool == true && !($0["signedTransaction"] as? String ?? "").isEmpty
        }.count
        print("AskToBuy trace \(elapsed)s \(phase) entitlements=\(active.count) pending=\(unfinished.count) verifiedRecords=\(verifiedCount)")
      } else {
        print("AskToBuy trace \(elapsed)s \(phase)")
      }
    }
    let approved = expectation(description: "Verified approval reaches the native entitlement callback")
    var approvalDelivered = false
    store.changed = { snapshot in
      trace("changed callback", snapshot: snapshot)
      if !approvalDelivered && self.entitlements(snapshot).contains(where: { $0["productId"] as? String == self.monthly }) {
        approvalDelivered = true
        approved.fulfill()
      }
    }
    trace("before pending purchase")
    let result = try await store.purchase(productID: monthly, appAccountToken: UUID().uuidString)
    trace("after pending purchase", snapshot: result["snapshot"] as? [String: Any])
    XCTAssertEqual(result["outcome"] as? String, "pending")
    XCTAssertTrue(entitlements(try XCTUnwrap(result["snapshot"] as? [String: Any])).isEmpty)
    let transaction = try XCTUnwrap(session.allTransactions().first { $0.productIdentifier == monthly })
    trace("before approval")
    try session.approveAskToBuyTransaction(identifier: transaction.identifier)
    trace("after approval")
    await fulfillment(of: [approved], timeout: 10)
    trace("after callback wait")
    let checked = try await waitForAccess(true, productID: monthly)
    try assertVerified(checked, productID: monthly)
    trace("after current entitlement check", snapshot: checked)
  }

  func test05UserCancellationDoesNotGrantAccess() async throws {
    try await session.setSimulatedError(.generic(.userCancelled), forAPI: .purchase)
    do {
      let result = try await store.purchase(productID: monthly, appAccountToken: UUID().uuidString)
      XCTAssertEqual(result["outcome"] as? String, "cancelled")
    } catch {
      let cancelled: Bool
      if let storeKitError = error as? StoreKitError, case .userCancelled = storeKitError { cancelled = true }
      else {
        let legacy = error as NSError
        cancelled = legacy.domain == SKErrorDomain && legacy.code == SKError.Code.paymentCancelled.rawValue
      }
      XCTAssertTrue(cancelled, "Only a StoreKit user-cancellation error is an acceptable cancelled result: \(error)")
    }
    let checked = await store.snapshot()
    XCTAssertTrue(entitlements(checked).isEmpty)
  }

  func test06CatalogFailureDisablesPurchaseButPreservesVerifiedAccess() async throws {
    _ = try await store.purchase(productID: monthly, appAccountToken: UUID().uuidString)
    let existingTransactions = session.allTransactions().map { $0.identifier }
    try await session.setSimulatedError(.generic(.networkError(URLError(.notConnectedToInternet))), forAPI: .loadProducts)
    let unavailable = await store.snapshot()
    XCTAssertEqual(unavailable["catalogReady"] as? Bool, false)
    XCTAssertTrue(products(unavailable).isEmpty)
    try assertVerified(unavailable, productID: monthly)
    do {
      _ = try await store.purchase(productID: yearly, appAccountToken: UUID().uuidString)
      XCTFail("An unavailable product catalog must block a new purchase")
    } catch {
      XCTAssertEqual((error as NSError).domain, "SolarSubscriptions")
      XCTAssertTrue(error.localizedDescription.contains("Subscriptions are temporarily unavailable"))
    }
    XCTAssertEqual(session.allTransactions().map { $0.identifier }, existingTransactions)
  }

  func test08ForcedRenewalKeepsOriginalPurchaseAndUpdatesVerifiedTransaction() async throws {
    let first = try await store.purchase(productID: monthly, appAccountToken: UUID().uuidString)
    let firstSnapshot = try XCTUnwrap(first["snapshot"] as? [String: Any])
    let original = try XCTUnwrap(entitlements(firstSnapshot).first)
    try session.forceRenewalOfSubscription(productIdentifier: monthly)
    let deadline = Date().addingTimeInterval(10)
    var renewed = await store.snapshot(reloadProducts: false)
    while entitlements(renewed).first?["transactionId"] as? String == original["transactionId"] as? String && Date() < deadline {
      try await Task.sleep(nanoseconds: 100_000_000)
      renewed = await store.snapshot(reloadProducts: false)
    }
    try assertVerified(renewed, productID: monthly)
    let next = try XCTUnwrap(entitlements(renewed).first)
    XCTAssertEqual(next["originalTransactionId"] as? String, original["originalTransactionId"] as? String)
    XCTAssertNotEqual(next["transactionId"] as? String, original["transactionId"] as? String)
  }

  func test09NaturalExpirationWithAcceleratedAppleClockAndRestore() async throws {
    // Apple documents accelerated renewal periods as an alternative to forcing
    // expiry. This exercises actual local period expiration and notifications.
    session.timeRate = .oneRenewalEveryTenSeconds
    let result = try await store.purchase(productID: monthly, appAccountToken: UUID().uuidString)
    XCTAssertEqual(result["outcome"] as? String, "purchased")
    try assertVerified(try XCTUnwrap(result["snapshot"] as? [String: Any]), productID: monthly)
    let transaction = try XCTUnwrap(session.allTransactions().first { $0.productIdentifier == monthly })
    try session.disableAutoRenewForTransaction(identifier: transaction.identifier)
    let expired = try await waitForAccess(false, timeout: 20)
    XCTAssertTrue(entitlements(expired).isEmpty)
    let restoredExpired = try await store.restore()
    XCTAssertTrue(entitlements(restoredExpired).isEmpty)
    for item in products(restoredExpired) { XCTAssertTrue(item["introductoryOffer"] is NSNull) }
  }

  func test07UnverifiedTransactionFailsClosed() async throws {
    try await session.setSimulatedError(.verification(.invalidSignature), forAPI: .verification)
    do {
      _ = try await store.purchase(productID: monthly, appAccountToken: UUID().uuidString)
      XCTFail("An unverified purchase must not succeed")
    } catch {
      XCTAssertEqual((error as NSError).domain, "SolarSubscriptions")
      XCTAssertTrue(error.localizedDescription.contains("could not verify"))
    }
    let checked = await store.snapshot()
    XCTAssertTrue(entitlements(checked).isEmpty)
  }

  func test10PurchaseRequiresStableServerAccountIdentity() async throws {
    for token in [nil, "not-a-uuid", "00000000-0000-0000-0000-000000000000"] as [String?] {
      do {
        _ = try await store.purchase(productID: monthly, appAccountToken: token)
        XCTFail("A purchase without the authenticated server account must fail")
      } catch {
        XCTAssertEqual((error as NSError).domain, "SolarSubscriptions")
        XCTAssertTrue(error.localizedDescription.contains("account could not be identified"))
      }
    }
    XCTAssertTrue(session.allTransactions().isEmpty)
  }

  func test11UnfinishedDeliverySurvivesRestartUntilCorrectAccountAcknowledgesIt() async throws {
    let owner = UUID()
    let result = try await store.purchase(productID: monthly, appAccountToken: owner.uuidString)
    let purchased = try XCTUnwrap(result["snapshot"] as? [String: Any])
    let receipt = try XCTUnwrap(pending(purchased).first)
    let transactionID = try XCTUnwrap(receipt["transactionId"] as? String)
    XCTAssertEqual(receipt["appAccountToken"] as? String, owner.uuidString)
    XCTAssertEqual(receipt["source"] as? String, "storekit-unfinished")
    // Leaving delivery unfinished models a failed/uncertain server response.
    store.stop()
    store = SolarSubscriptionStore()
    let restarted = await store.snapshot()
    XCTAssertTrue(pending(restarted).contains { $0["transactionId"] as? String == transactionID })
    do {
      try await store.finish(transactionID: transactionID, appAccountToken: UUID().uuidString)
      XCTFail("A foreign Solar account must not acknowledge the owner's transaction")
    } catch {
      XCTAssertTrue(error.localizedDescription.contains("another Solar account"))
    }
    let denied = await store.snapshot()
    XCTAssertTrue(pending(denied).contains { $0["transactionId"] as? String == transactionID })
    try await store.finish(transactionID: transactionID, appAccountToken: owner.uuidString)
    let acknowledged = await store.snapshot()
    XCTAssertFalse(pending(acknowledged).contains { $0["transactionId"] as? String == transactionID })
    try await store.finish(transactionID: transactionID, appAccountToken: owner.uuidString)
    try assertVerified(await store.snapshot(), productID: monthly)
  }
}
