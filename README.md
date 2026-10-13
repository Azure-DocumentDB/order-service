# Transactional orders on Azure DocumentDB

A small **.NET 10 API for the managed Azure DocumentDB service**: reserve multiple products, create an order, and record its idempotency key in one real database transaction. A vanilla web dashboard makes replay and stock changes visible. No payment service, paid model, Docker database, OSS DocumentDB server, or production in-memory fallback is involved.

> **Compatibility first:** use a disposable **M30-or-higher, single-shard** managed cluster, with **unsharded `inventory` and `orders` collections in one database**. This sample does not assert distributed/cross-shard transaction support. Check the [managed transaction guide](https://learn.microsoft.com/azure/documentdb/how-to-transactions) and [current limits](https://learn.microsoft.com/azure/documentdb/limits). The documented transaction lifetime is 30 seconds; this sample budgets the driver's complete transaction/retry operation to 20 seconds, then allows a separate 3-second outcome lookup. A single transaction contains at most eight inventory updates. The live probes—not unit tests—are the compatibility gate. See [TESTED.md](TESTED.md) for the reviewed managed-service and rendered-browser evidence, exact tested revisions, release verification, and limits.

## What the transaction guarantees

```text
POST /api/orders + Idempotency-Key
       │
       └─ MongoDB session.WithTransactionAsync
            1. Read orders[_id = key]; replay only if payload fingerprint matches
            2. Insert orders[_id = key] (unique order + immutable idempotency record)
            3. For EACH line: update inventory where _id=product AND Stock >= quantity
                             $inc Stock by -quantity
            4. Commit all writes together; a missing/insufficient product aborts all
```

- **No oversell:** stock is a conditional write inside the transaction, not a read/then-write in application memory. No process lock is used.
- **Atomic batches:** a two-product order either reserves both products and records the order, or does neither. Products are updated in sorted ID order to reduce contention.
- **Persistent idempotency:** the unique `orders._id` is the request key; a SHA-256 fingerprint binds normalized product IDs/quantities to it. Reordering JSON line items is equivalent. Same key/different payload → `409 idempotency_conflict`. The record and inventory change commit together. Records do not expire in this sample.
- **A new key is a new purchase**, even for the same products. After a lost response, retry the **same key and payload**, never a newly generated key.
- **Retries:** MongoDB.Driver's `WithTransactionAsync` handles transient transaction labels and retries **the same commit** for `UnknownTransactionCommitResult`. Exceptions are never swallowed inside the callback. A managed-service `117 ConflictingOperationInProgress` or `112 WriteConflict` on a callback `find`/`insert`/`update` is rethrown with the missing transient label so the SDK aborts before retrying the entire callback with the same key/order and original 20-second budget. This normalization is **never** applied to commit errors, unknown-commit labels, collection creation, or unrelated codes. The SDK supplies exponential-jitter retry backoff; it suppresses abort-command errors per its specification, so the application does not claim receipt of an abort acknowledgement. A duplicate-key race or unresolved commit triggers a primary/majority read of the saved order. If outcome cannot be established, the API returns `503 outcome_unknown_retry_same_key` rather than claiming failure or blindly repeating an external charge. HTTP disconnection can also leave the caller unsure: retry the original key.
- **Not exactly-once payments:** there are no external side effects in the callback. To add payments/email, design an outbox and the external provider's idempotency contract separately.

## Prerequisites and permissions

- .NET SDK **10.0.401**, pinned in `global.json`; Python 3 for standard-library probe/audit helpers.
- An existing, reachable managed Azure DocumentDB M30+ cluster. Account for Azure charges and firewall/private-network access yourself; these commands do not provision or destroy resources.
- A Microsoft Entra identity configured for **native MongoDB OIDC data-plane access** on that cluster. Grant only the data-plane permissions needed on the dedicated demo database: read inventory/orders, insert/update demo records, and create the two collections for seed. Azure control-plane Reader alone is **not** database authorization. A cluster admin should prepare any narrowly scoped database role assignment according to [Entra authentication documentation](https://learn.microsoft.com/azure/documentdb/how-to-connect-entra-authentication).
- Local `az login` or a properly configured managed/workload identity for `DefaultAzureCredential`. The callback always requests `https://ossrdbms-aad.database.windows.net/.default`; the server cannot change its audience. Interactive browser auth is disabled.

## Configure and run

Use a **new disposable database name**, not an existing application database. The application requires the `orders_demo_` prefix and a 3–40-character lowercase suffix. Secrets stay in your shell/secret store, not source control. `.env` files are ignored but are **not loaded automatically**.

```sh
export DOCUMENTDB_URI='mongodb+srv://YOUR-CLUSTER.mongocluster.cosmos.azure.com/?tls=true&retrywrites=false&authSource=admin'
export DOCUMENTDB_ALLOWED_HOST='YOUR-CLUSTER.mongocluster.cosmos.azure.com'
export DOCUMENTDB_DATABASE='orders_demo_your_disposable_run'
export DOCUMENTDB_AUTH='entra'
export DOCUMENTDB_ALLOW_WRITES='true'
export DOCUMENTDB_CONFIRM_SINGLE_SHARD='true' # only after checking actual topology
export ORDER_API_TOKEN="$(python3 -c 'import secrets; print(secrets.token_urlsafe(32))')"

dotnet restore OrderService.slnx --locked-mode
dotnet build OrderService.slnx --configuration Release --no-restore
dotnet run --project src/OrderService --configuration Release --no-build --no-launch-profile -- seed
dotnet run --project src/OrderService --configuration Release --no-build --no-launch-profile
```

Open **http://127.0.0.1:5080**, enter `ORDER_API_TOKEN`, then load inventory. The seed creates two synthetic products: `notebook` (50) and `pen` (100). **Rerunning seed never resets existing stock or removes orders**: it uses `$setOnInsert`. No HTTP seed/reset/delete endpoint exists. Collections are created before transactions begin. The app does not shard collections; `DOCUMENTDB_CONFIRM_SINGLE_SHARD` is an operator assertion, not automatic topology proof.

The dashboard intentionally stays small: after a failed refresh, previously loaded inventory/orders remain visible alongside the explicit error result. Treat those lists as stale until a successful Load; an error is not confirmation of current stock. The result may be below the fold on a narrow screen. Actual 401/409 responses were browser-tested; offline/server-down and malformed-response faults were not injected. No favicon is supplied (its harmless 404 does not prevent scripts/styles from loading).

The default—and intentionally fixed—binding is `127.0.0.1:5080`; `ASPNETCORE_URLS` cannot expose it. Any configured `Kestrel:Endpoints` (JSON, environment, or command line) causes startup refusal before a listener opens; hosting URLs are explicitly preferred as defense in depth. Every `/api/*` request needs the operator bearer token. The token is held only in page memory, never cookies/localStorage. No CORS policy grants cross-origin access; non-GET browser requests with a different `Origin` are rejected. CSP disallows inline scripts and framing. This is **not a multi-tenant or production identity boundary**. Do not reverse-proxy it to the internet without replacing the demo bearer with proper user authentication/authorization, TLS, rate limiting, and operational telemetry. Database TLS is always enabled and strict; the SRV destination must match the exact managed-host allowlist before credentials are requested.

### API

```sh
curl -sS http://127.0.0.1:5080/api/inventory \
  -H "Authorization: Bearer $ORDER_API_TOKEN"
curl -sS http://127.0.0.1:5080/api/orders \
  -H "Authorization: Bearer $ORDER_API_TOKEN" \
  -H 'Content-Type: application/json' -H 'Idempotency-Key: example-purchase-1' \
  --data '{"items":[{"productId":"notebook","quantity":1},{"productId":"pen","quantity":2}]}'
```

- `GET /api/inventory?limit=25&cursor=<last-id>` and `GET /api/orders?...`: default 25, max 100; keyset pagination by `_id`, **not chronological**; follow `nextCursor` until null. IDs/cursors use 1–64 ASCII letters, digits, `_` or `-`.
- `POST /api/orders`: `Idempotency-Key` must be exactly one **header**, 1–64 allowed characters; query-string keys do not count. JSON must contain 1–8 distinct products with integer quantities 1–100. Unknown JSON fields are rejected. Body limit 8 KiB. No customer/PII fields.
- `201` new order, `200` replay. Responses contain server-generated order ID and UTC timestamp; `X-Request-Id` is a separate server-generated correlation ID, **not** the idempotency key.
- Stable errors: `400 invalid_order / invalid_idempotency_key / invalid_json / invalid_pagination`, `401 unauthorized`, `403 cross_origin_forbidden / writes_disabled`, `409 out_of_stock / idempotency_conflict`, `415 json_required`, `503 outcome_unknown_retry_same_key / database_unavailable_retry_same_key`. Errors never echo raw driver exceptions, secrets, stack traces or request bodies.
- `GET /health` is liveness only, explicitly `dependencyCheck:false`. Its per-process `instanceId` enables restart verification; a 200 health response is **not proof of database access**.

## Run the gates

### Offline / CI (no Azure credentials)

```sh
dotnet restore OrderService.slnx --locked-mode --no-http-cache
dotnet build OrderService.slnx --no-restore --configuration Release
dotnet test tests/OrderService.Tests --no-build --no-restore --configuration Release
python3 -m unittest discover -s tools -p 'test_*.py'
python3 tools/check-vulnerabilities.py
```

Unit tests and an injected mock `IOrderStore` test validation/security/HTTP contracts; mock driver tests verify session propagation, conditional updates, transaction options and exception propagation. Four Python actual-process regressions verify Kestrel endpoint rejection from environment/arguments/JSON, fixed-loopback health despite `ASPNETCORE_URLS`, and refusal on the machine's nonloopback interface. These need port 5080 free and a nonloopback network interface; they perform no database calls. **Mocks cannot prove transaction isolation or rollback.** All dependencies are locked, and the audit fails closed on malformed/error-bearing responses and findings at any severity, including transitives. The GitHub workflow uses SHA-pinned actions, read-only permissions and no Azure credentials/cloud deployment.

### Actual managed Azure gate (required before claiming live-tested)

```sh
export RUN_LIVE_TESTS=true
dotnet run --project tests/OrderService.Probes --configuration Release --no-build -- --live
```

The harness outputs JSON counters and exits nonzero when opt-in/config/permissions/features are missing—**never a skipped pass**. Run first with `DOCUMENTDB_AUTH=entra`, then with explicitly authorized debug native SCRAM credentials if proving both auth paths. It creates unique synthetic fixture products and orders (no destructive reset):

1. Race **20**, then **100**, purchases against five units: exactly five accepted, all others out-of-stock, stock zero, five unique persisted orders.
2. Replay the same key with reordered items; reject a payload mismatch; race 20 callers on the same key.
3. New key creates a separate purchase.
4. Inject a failure after the first stock update via a test-only observer: both stock changes and the inserted order must roll back. No failure switch exists in the HTTP API.
5. Insufficient second product rolls back the first; rerun seed preserves state; a new Mongo client finds the persistent replay.

### Actual API restart gate

Run while the API is up and no one else is purchasing the two seeded products:

```sh
python3 tools/http_probe.py --phase before --state-file /PRIVATE/PATH/restart.json
# Stop ONLY this sample API process, then restart it using the same environment/database.
python3 tools/http_probe.py --phase after --state-file /PRIVATE/PATH/restart.json
```

The first phase checks real HTTP auth, new/replay/mismatch semantics, and exactly one deduction from each product. The second requires a **different process instance ID**, the same persisted order ID and no further deduction. Use a new state-file path per run; files contain synthetic IDs/stock only, never credentials, and are created owner-only. Output is JSON and failure is nonzero.

### Optional native SCRAM diagnosis

Entra OIDC remains the default. An operator who is explicitly authorized to test the cluster's native credentials may set `DOCUMENTDB_AUTH=scram`, `DOCUMENTDB_USERNAME` and `DOCUMENTDB_PASSWORD` in a private shell/secret store. Credentials must be separate environment variables—not URI userinfo. The same TLS/host/write guards and transaction logic apply. Never commit, log, paste into a URL, or put these credentials into GitHub CI. Revert to Entra after diagnosis.

## Boundaries and cleanup

This demonstration intentionally omits payment capture, customer identity, tenant isolation, inventory replenishment, order cancellation, background workers and cloud deployment. Idempotency metadata is retained with each order; deleting orders would remove replay protection. Stock/order consistency assumes writes go through this transaction logic and the database is not reset underneath it. Set write opt-in false to disable this sample's mutations; it does not revoke the identity's database privileges.

Fixtures consume small amounts of storage. When finished, an authorized Azure/database operator may remove the **entire confirmed-disposable demo database** or dedicated cluster using their normal process. No repository command automatically deletes cloud resources or data. Never apply cleanup to a production database.
