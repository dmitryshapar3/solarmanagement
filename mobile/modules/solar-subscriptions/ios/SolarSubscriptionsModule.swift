import ExpoModulesCore

public final class SolarSubscriptionsModule: Module {
  private var store: SolarSubscriptionStore?

  public func definition() -> ModuleDefinition {
    Name("SolarSubscriptions")
    Events("entitlementsChanged")

    OnCreate { [weak self] in
      Task { @MainActor [weak self] in self?.subscriptionStore().start() }
    }
    OnDestroy { [weak self] in
      Task { @MainActor [weak self] in self?.store?.stop() }
    }
    AsyncFunction("getSnapshotAsync") { [weak self] (promise: Promise) in
      Task { @MainActor [weak self] in
        guard let self else { promise.reject("ERR_MODULE_UNAVAILABLE", "Subscriptions are unavailable."); return }
        promise.resolve(await self.subscriptionStore().snapshot())
      }
    }
    AsyncFunction("getEntitlementsAsync") { [weak self] (promise: Promise) in
      Task { @MainActor [weak self] in
        guard let self else { promise.reject("ERR_MODULE_UNAVAILABLE", "Subscriptions are unavailable."); return }
        promise.resolve(await self.subscriptionStore().snapshot(reloadProducts: false))
      }
    }
    AsyncFunction("purchaseAsync") { [weak self] (productID: String, appAccountToken: String?, promise: Promise) in
      Task { @MainActor [weak self] in
        guard let self else { promise.reject("ERR_MODULE_UNAVAILABLE", "Subscriptions are unavailable."); return }
        do { promise.resolve(try await self.subscriptionStore().purchase(productID: productID, appAccountToken: appAccountToken)) }
        catch { promise.reject("ERR_PURCHASE_FAILED", error.localizedDescription) }
      }
    }
    AsyncFunction("restoreAsync") { [weak self] (promise: Promise) in
      Task { @MainActor [weak self] in
        guard let self else { promise.reject("ERR_MODULE_UNAVAILABLE", "Subscriptions are unavailable."); return }
        do { promise.resolve(try await self.subscriptionStore().restore()) }
        catch { promise.reject("ERR_RESTORE_FAILED", "The App Store could not restore purchases. Please try again.") }
      }
    }
    AsyncFunction("manageAsync") { [weak self] (promise: Promise) in
      Task { @MainActor [weak self] in
        guard let self else { promise.reject("ERR_MODULE_UNAVAILABLE", "Subscriptions are unavailable."); return }
        do { try await self.subscriptionStore().manage(); promise.resolve() }
        catch { promise.reject("ERR_MANAGE_FAILED", "Open App Store account settings to manage your subscription.") }
      }
    }
  }

  @MainActor private func subscriptionStore() -> SolarSubscriptionStore {
    if let store { return store }
    let created = SolarSubscriptionStore()
    created.changed = { [weak self] snapshot in self?.sendEvent("entitlementsChanged", snapshot) }
    store = created
    return created
  }
}
