# Verification record

## Local verification — 2026-10-12

- SDK: .NET 10.0.401, MongoDB.Driver 3.12.0, Azure.Identity 1.21.0.
- Locked restore and Release build: passed, zero warnings/errors.
- Permanent .NET unit / mock API / driver-wiring tests: **103 passed, 0 failed, 0 skipped**.
- Permanent Python tests: **21 passed**: 17 NuGet audit-schema tests plus 4 actual-process Kestrel binding regressions (external endpoint configuration via environment/arguments/JSON refused; `ASPNETCORE_URLS` cannot expose the API; nonloopback access refused).
- Current NuGet direct + transitive vulnerability audit: passed for all three projects; no findings returned. The audit parser accepts the verified .NET 10 clean sparse result shape and rejects malformed/error-bearing results.
- Live harness without explicit opt-in correctly fails (exit 1, `live_opt_in_required`); this safety test is not a live pass.

## Actual managed Azure verification — 2026-10-12

Tested source: `fe2f5d69881838104ce043f0eda805869330b5fb`.
Runtime transaction and HTTP code are unchanged from `469e93f13f87515c15b96fcaff3b26de9d6205bf`, used for the native HTTP restart test; the later commit repairs only the probe's array-membership query and adds its offline regression.

Topology: disposable **managed Azure DocumentDB M30, MongoDB 8.0 compatibility, one shard, one database per authentication path, no HA**. No local MongoDB/OSS container or in-memory store substituted for these tests. Credentialed test outputs are retained separately, without credentials in this repository.

- An independent two-collection, same-database probe committed both writes and rolled both back on abort. Its synthetic records were cleaned.
- The full sample probe passed **18 assertions under native SCRAM and 18 under Microsoft Entra OIDC**, using the real MongoDB driver and Azure database.
- For **each authentication mode**, 20 concurrent requests against stock 5 produced exactly 5 persisted unique orders, 15 sold-out results, final stock 0. At concurrency 100: exactly 5 orders, 95 sold-out results, final stock 0.
- Sequential same-key replay (including reordered items) returned the same order and deducted stock once. Twenty simultaneous same-key requests produced one new order and one decrement. Same-key/different-payload requests conflicted without changing stock; a new key represented a new purchase.
- An injected in-process exception after the first reservation rolled back both inventory and order state. A second product being sold out likewise rolled back the first product's reservation and the order.
- Reseeding preserved existing stock/orders; a fresh client replayed the persistent order.
- **Both authentication modes passed real HTTP before/restart/after probes: 8 assertions before and 6 after per mode.** The process-generated instance ID changed; replay returned the existing order without further stock changes. HTTP checks include unauthenticated 401, new-order 201, same-key replay 200, mismatched payload 409, and two-product stock deduction exactly once.

Earlier failing runs are **not** counted as passing: actual Azure contention exposed unlabeled `117 ConflictingOperationInProgress` and `112 WriteConflict`. Exact code/name normalization is limited to precommit callback `find`/`insert`/`update`, rethrowing to the SDK's abort/retry path within the original 20-second budget; commit and unknown-result exclusions remain. The subsequent probe-only `AnyEq` serializer failure was reproduced offline and replaced with typed `ElemMatch`, without weakening stock/order/count assertions. The passing results above are from the corrected source.

## Rendered-browser verification — 2026-10-13

Tested application source: `90d44b1e6c908d725375e3ccd873c7835be9e011`. The browser gate was rerun after the loopback navigation policy was resolved. Managed Chrome rendered the actual loopback operator UI against the separately seeded `orders_demo_ui_20261013` database on managed Azure DocumentDB M30, using native SCRAM; no mock store or fallback data was substituted.

- Authenticated inventory/orders loading passed: initial stock was 50 notebooks / 100 pens, with no orders.
- A UI request for 2 notebooks and 3 pens returned **201**, created one order, and refreshed stock to **48 / 97**. Repeating the same key and payload returned **200 / replayed=true**, the same order ID, one persisted order, and unchanged stock.
- Changing quantities under that key displayed **409 / idempotency_conflict**. An explicit inventory/orders refresh confirmed unchanged stock and order count.
- Invalid-token reads and writes displayed **401 / unauthorized**. Reload cleared the token; an empty-token load also displayed 401 without showing fallback inventory/orders. The password input is masked; localStorage and sessionStorage remained empty.
- Keyboard Tab/Enter activated Load, Send/replay, and Generate NEW key, with a visible 3px focus outline. A second-item out-of-stock request under a new key displayed **409 / out_of_stock**; an explicit refresh confirmed both products' stock and the single order remained unchanged.
- Desktop 1200×840 and narrow 390×844 / 320×740 viewports were checked. Narrow layouts collapsed to one column; measured document widths matched viewport widths without page-level horizontal overflow in the checked states. Desktop and mobile screenshots were visually reviewed. This is not a full accessibility audit or physical-device test.
- No uncaught browser page errors were recorded. Console resource errors were accounted for: deliberate 401/409 cases plus one nonblocking missing `favicon.ico` 404.

Credential-free screenshots, observed response/stock records, browser request/console/error logs, and preserved offline/live evidence are retained by the maintainer. The operator token was supplied through supported file-to-stdin browser actions, not embedded in source or evidence. This browser run did not repeat the earlier .NET/Python suites or Entra/live concurrency probes; their records above remain unchanged. The visible-failure claim covers actual 401/409 responses, not a browser offline/server-down fault. After a failed refresh, previously loaded lists remain visible alongside the explicit error result.

## Release verification — 2026-10-13

- Independent final review covered the complete PR change, application/probe/test source, dependency locks, prior findings, preserved live evidence, browser response logs and all four screenshots. The earlier loopback-binding blocker is resolved. The enumerated managed-service and rendered-browser acceptance gates passed; no release-blocking defect was found.
- The stale-list behavior after failed refresh is a documented, nonblocking operator-UI limitation: the error result is explicit, no successful fetch or fallback data is fabricated, and server-side authorization/conditional stock checks remain enforced. On narrow screens the result may be below the fold. The missing favicon is cosmetic, not a script/style or application failure.
- Fresh local locked restore and Release build passed with zero warnings/errors; **103 .NET tests** and **17 audit-parser Python tests** passed, all three current-feed direct/transitive dependency audits passed, and the no-opt-in live probe failed closed. The four process-binding cases require a free port 5080; the acceptance server was deliberately left untouched. The complete **21-test Python suite** is required in the isolated final-head GitHub CI runner (the preserved earlier local 21-test pass is not counted as a fresh rerun).
- Source and full-history secret scans passed before publication. Release uses the existing [PR #1](https://github.com/Azure-DocumentDB/order-service/pull/1), an exact-head successful [CI run](https://github.com/Azure-DocumentDB/order-service/actions), and a normal head-guarded merge without administrative bypass. CI deliberately has no Azure credentials and does not repeat the live tests above. The PR and Actions records provide the final release SHA/status rather than this document predicting its own commit hash.
- Publication verification additionally requires successful merged-main CI and an anonymous fresh clone with locked restore/build/tests and current dependency audit. Repository visibility is not a claim of public application hosting. Resource-group teardown remains a separate operator responsibility; this release review neither performs nor claims cloud cleanup.

## Limits

No forced unknown-commit network-fault test, cross-shard transaction, HA/failover, sustained-load benchmark, external-payment exactly-once, public deployment, or multi-tenant claim is made. The explicit in-process abort and real process restart tests are not server-crash proof. The driver may suppress abort-command errors; the application does not claim acknowledged rollback for every failed network operation. Unknown outcomes require retrying the same key. Synthetic acceptance fixtures are retained in the owned disposable resource group until its verified teardown, not dropped by the sample.
