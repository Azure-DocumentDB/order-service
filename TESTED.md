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

## Remaining publication gates

- **Rendered-browser interaction and visual review: not verified.** Navigation to the loopback sample URL was blocked by the browser tool's local-address policy. HTTP/API tests are not a rendered-browser pass.
- GitHub CI must pass on the final PR head before release. CI deliberately has no Azure credentials and does not repeat the live tests above.
- The repository remains private and unmerged pending these gates. This document is evidence, not release approval.

## Limits

No forced unknown-commit network-fault test, cross-shard transaction, HA/failover, sustained-load benchmark, external-payment exactly-once, public deployment, or multi-tenant claim is made. The explicit in-process abort and real process restart tests are not server-crash proof. The driver may suppress abort-command errors; the application does not claim acknowledged rollback for every failed network operation. Unknown outcomes require retrying the same key. Synthetic acceptance fixtures are retained in the owned disposable resource group until its verified teardown, not dropped by the sample.
