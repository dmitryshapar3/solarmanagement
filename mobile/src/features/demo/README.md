# Offline demonstration installation

`DemoApiClient` is a drop-in `ApiClient` subclass for `DeyeSolarApi`:

```ts
const demoApi = new DeyeSolarApi(new DemoApiClient());
```

The parent auth layer should activate this separate instance through a clearly labeled **Try demo** action without calling `login`, saving a session, or supplying credentials. Use `DEMO_USERNAME` and `DEMO_API_BASE_URL` for display, and expose a persistent demo notice: synthetic data, changes kept only during this session, no hardware affected. Exiting demo should discard the instance and restore the regular auth flow; never carry demo data or tokens into production.

The client implements all routes currently used by `DeyeSolarApi`. Settings, rule CRUD and socket state changes stay in memory. Synthetic device commands appear in the local run history. Time controls generate sample charts for the chosen calendar interval. The fixtures make no claims about real weather, meter values or bills, and do not run a background automation engine.

`setBaseUrl` is forbidden, bearer tokens are ignored, the inherited transport always rejects, and unknown routes return an error. No operation uses the network or persistent storage. Integration links and Apple purchases remain the parent UI's responsibility; demo access itself should not require a purchase.

Run the safety and functional tests with:

```bash
node --import tsx --test src/features/demo/DemoApiClient.test.mts
```

Apple's general checklist accepts a complete demo mode. Guideline 2.1(a) also retains a prior-approval caveat when replacing a review account because of legal/security obligations. Explain the offline mode and real hardware controls in review notes, and resolve review access with Apple before relying solely on this mode for submission. [App Review Guidelines](https://developer.apple.com/app-store/review/guidelines/).
