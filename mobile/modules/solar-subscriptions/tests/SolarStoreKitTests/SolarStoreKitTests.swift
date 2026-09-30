// Runs only against Xcode local StoreKit configuration: no Apple credentials or payments.
import XCTest
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
  private func waitForAccess(_ expected: Bool, productID: String? = nil) async throws -> [String: Any] {
    let deadline = Date().addingTimeInterval(10)
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

  func test01CatalogContainsBothPlansAndGenuineTwoWeekTrial() async throws {
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
      XCTAssertEqual(item["introEligible"] as? Bool, true)
      let offer = try XCTUnwrap(item["introductoryOffer"] as? [String: Any])
      XCTAssertEqual(offer["paymentMode"] as? String, "freeTrial")
      XCTAssertEqual(offer["periodUnit"] as? String, "week")
      XCTAssertEqual(offer["periodValue"] as? Int, 2)
      XCTAssertEqual(offer["periodCount"] as? Int, 1)
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
    try assertVerified(try await store.restore(), productID: monthly)
  }

  func test03YearlyPurchaseAndRevocation() async throws {
    let result = try await store.purchase(productID: yearly, appAccountToken: nil)
    XCTAssertEqual(result["outcome"] as? String, "purchased")
    try assertVerified(try XCTUnwrap(result["snapshot"] as? [String: Any]), productID: yearly)
    let transaction = try XCTUnwrap(session.allTransactions().first { $0.productIdentifier == yearly })
    try session.refundTransaction(identifier: transaction.identifier)
    let revoked = try await waitForAccess(false)
    XCTAssertTrue(entitlements(revoked).isEmpty)
  }

  func test04PendingAskToBuyDoesNotGrantUntilApproved() async throws {
    session.askToBuyEnabled = true
    let approved = expectation(description: "Verified approval reaches the native entitlement callback")
    store.changed = { snapshot in
      if self.entitlements(snapshot).contains(where: { $0["productId"] as? String == self.monthly }) { approved.fulfill() }
    }
    let result = try await store.purchase(productID: monthly, appAccountToken: nil)
    XCTAssertEqual(result["outcome"] as? String, "pending")
    XCTAssertTrue(entitlements(try XCTUnwrap(result["snapshot"] as? [String: Any])).isEmpty)
    let transaction = try XCTUnwrap(session.allTransactions().first { $0.productIdentifier == monthly })
    try session.approveAskToBuyTransaction(identifier: transaction.identifier)
    await fulfillment(of: [approved], timeout: 10)
    try assertVerified(try await waitForAccess(true, productID: monthly), productID: monthly)
  }

  func test05UserCancellationDoesNotGrantAccess() async throws {
    try await session.setSimulatedError(.generic(.userCancelled), forAPI: .purchase)
    do {
      let result = try await store.purchase(productID: monthly, appAccountToken: nil)
      XCTAssertEqual(result["outcome"] as? String, "cancelled")
    } catch { /* StoreKit may surface the simulated cancellation as an error. */ }
    let checked = await store.snapshot()
    XCTAssertTrue(entitlements(checked).isEmpty)
  }

  func test06CatalogFailureDisablesPurchaseButPreservesVerifiedAccess() async throws {
    _ = try await store.purchase(productID: monthly, appAccountToken: nil)
    try await session.setSimulatedError(.generic(.networkError(URLError(.notConnectedToInternet))), forAPI: .loadProducts)
    let unavailable = await store.snapshot()
    XCTAssertEqual(unavailable["catalogReady"] as? Bool, false)
    XCTAssertTrue(products(unavailable).isEmpty)
    try assertVerified(unavailable, productID: monthly)
    do {
      _ = try await store.purchase(productID: yearly, appAccountToken: nil)
      XCTFail("An unavailable product catalog must block a new purchase")
    } catch { }
  }

  func test08ForcedRenewalKeepsOriginalPurchaseAndUpdatesVerifiedTransaction() async throws {
    let first = try await store.purchase(productID: monthly, appAccountToken: nil)
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
    session.timeRate = .oneRenewalEveryTwoSeconds
    let result = try await store.purchase(productID: monthly, appAccountToken: nil)
    XCTAssertEqual(result["outcome"] as? String, "purchased")
    try assertVerified(try XCTUnwrap(result["snapshot"] as? [String: Any]), productID: monthly)
    let transaction = try XCTUnwrap(session.allTransactions().first { $0.productIdentifier == monthly })
    try session.disableAutoRenewForTransaction(identifier: transaction.identifier)
    let expired = try await waitForAccess(false)
    XCTAssertTrue(entitlements(expired).isEmpty)
    let restoredExpired = try await store.restore()
    XCTAssertTrue(entitlements(restoredExpired).isEmpty)
    for item in products(restoredExpired) { XCTAssertEqual(item["introEligible"] as? Bool, false) }
  }

  func test07UnverifiedTransactionFailsClosed() async throws {
    try await session.setSimulatedError(.verification(.invalidSignature), forAPI: .verification)
    do {
      _ = try await store.purchase(productID: monthly, appAccountToken: nil)
      XCTFail("An unverified purchase must not succeed")
    } catch {
      XCTAssertEqual((error as NSError).domain, "SolarSubscriptions")
      XCTAssertTrue(error.localizedDescription.contains("could not verify"))
    }
    let checked = await store.snapshot()
    XCTAssertTrue(entitlements(checked).isEmpty)
  }
}
