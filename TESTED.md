# Verification record

## Local verification — 2026-10-12

- SDK: .NET 10.0.401, MongoDB.Driver 3.12.0, Azure.Identity 1.21.0.
- Locked restore and Release build: passed, zero warnings/errors.
- Permanent .NET unit / mock API / driver-wiring tests: **101 passed, 0 failed, 0 skipped**.
- Permanent Python tests: **21 passed**: 17 NuGet audit-schema tests plus 4 actual-process Kestrel binding regressions (external endpoint configuration via environment/arguments/JSON refused; `ASPNETCORE_URLS` cannot expose the API; nonloopback access refused). No database credentials/calls used for those tests.
- Current NuGet direct + transitive vulnerability audit: passed for all three projects; no findings returned. Audit parser supports the actual .NET 10 clean sparse result shape and rejects malformed/error-bearing results.
- Live harness without explicit opt-in: **correctly fails** (exit 1, `live_opt_in_required`); this is a safety test, not a live pass.

## Managed Azure verification — pending required gate

The full sample has **not** itself been live-proven. An operator reported an independent M30 cross-collection commit/rollback proof, followed by a sample probe failure with unlabeled server code 117 before its first assertion. After narrowly scoped pre-commit code 117 normalization and safe phase/command diagnostics, the operator's rerun reached `race_20_transactions` and returned unlabeled `112 WriteConflict` from `update`. The same code-and-name-gated, precommit-only SDK retry normalization now covers 112 as well; commit/unknown-result exclusions and the original deadline remain unchanged. The complete live gate must be rerun. No failed concurrency run is counted as passing. Before publication as a working live-tested sample, an authorized operator must append the actual date, managed service tier/topology (no sensitive resource identifiers), tested commit SHA, auth modes, counts, and sanitized output for:

- [ ] Same-database cross-collection transaction capability on M30+ single-shard managed Azure DocumentDB.
- [ ] Native Entra OIDC full live probe (20/100 concurrency, replay/mismatch/concurrent same-key, two-product rollback, seed safety).
- [ ] Authorized native SCRAM full live probe, if both authentication modes are claimed tested.
- [ ] Real API before/restart/after probe; different process instance ID and persistent order/stock proof.
- [ ] GitHub read-only CI at the exact release head.

No local/mock result substitutes for these gates. No cross-shard transaction, external-payment exactly-once, public deployment, or multi-tenant claim is made.
