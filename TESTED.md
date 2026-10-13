# Verification record

## Local verification — 2026-10-12

- SDK: .NET 10.0.401, MongoDB.Driver 3.12.0, Azure.Identity 1.21.0.
- Locked restore and Release build: passed, zero warnings/errors.
- Permanent .NET unit / mock API / driver-wiring tests: **78 passed, 0 failed, 0 skipped**.
- Permanent Python NuGet audit-schema tests: **17 passed**.
- Current NuGet direct + transitive vulnerability audit: passed for all three projects; no findings returned. Audit parser supports the actual .NET 10 clean sparse result shape and rejects malformed/error-bearing results.
- Live harness without explicit opt-in: **correctly fails** (exit 1, `live_opt_in_required`); this is a safety test, not a live pass.

## Managed Azure verification — pending required gate

The local implementation has **not** itself been live-proven. Before publication as a working live-tested sample, an authorized operator must append the actual date, managed service tier/topology (no sensitive resource identifiers), tested commit SHA, auth modes, counts, and sanitized output for:

- [ ] Same-database cross-collection transaction capability on M30+ single-shard managed Azure DocumentDB.
- [ ] Native Entra OIDC full live probe (20/100 concurrency, replay/mismatch/concurrent same-key, two-product rollback, seed safety).
- [ ] Authorized native SCRAM full live probe, if both authentication modes are claimed tested.
- [ ] Real API before/restart/after probe; different process instance ID and persistent order/stock proof.
- [ ] GitHub read-only CI at the exact release head.

No local/mock result substitutes for these gates. No cross-shard transaction, external-payment exactly-once, public deployment, or multi-tenant claim is made.
