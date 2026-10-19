# WopiHost.CellBridge

> [!WARNING]
> **Experimental.** This package wires the open-source [cellbridge](https://github.com/PatrickMatthiesen/cellbridge) MS-FSSHTTP engine into WopiHost as an alternative to the proprietary [`WopiHost.Cobalt`](../WopiHost.Cobalt/README.md). It compiles and its unit tests drive real MS-FSSHTTPB requests through the WOPI seam, but it has **not** been exercised against Office Online Server — see [What is still unknown](#what-is-still-unknown). `WopiHost.Cobalt` remains the production path.

[cellbridge](https://github.com/PatrickMatthiesen/cellbridge) (MIT, .NET 10, by [Patrick Matthiesen](https://github.com/PatrickMatthiesen)) implements [MS-FSSHTTP](https://learn.microsoft.com/openspecs/sharepoint_protocols/ms-fsshttp/) (the SOAP/MTOM cell-storage envelope), [MS-FSSHTTPB](https://learn.microsoft.com/openspecs/sharepoint_protocols/ms-fsshttpb/) (the binary storage index / data-element / knowledge graph) and the chunking that shreds Office packages into reusable parts — the whole stack that `Microsoft.CobaltCore.dll` provides to `WopiHost.Cobalt`. It was written so desktop Word/Excel/PowerPoint can open, edit and save documents against a plain ASP.NET Core server the way they do against SharePoint; this package reuses its protocol engine behind WopiHost's existing `ICobaltProcessor` seam.

## How it fits

```
Office Online Server ──X-WOPI-Override: COBALT──▶ WopiHost.Core (POST /wopi/files/{id})
                                                       │  ICobaltProcessor.ProcessCobalt(file, principal, body)
                                                       ├── WopiHost.Cobalt     → Microsoft.CobaltCore (proprietary)
                                                       └── WopiHost.CellBridge → CellBridge.AspNetCore + FssHttpB + Storage (MIT)
```

* `ICobaltProcessor` (in `WopiHost.Abstractions`) is the only contract the WOPI endpoint depends on, so the two backends are drop-in alternatives and exactly one is registered per host.
* `CellBridgeProcessor` imports each WOPI file into cellbridge's document store on first use (`CellBridgeDocumentService.ImportAsync`), executes the request through `CellBridgeDocumentService.ExecuteAsync` / `FssHttpLockCoordinator`, and when a save advances cellbridge's content version it copies the materialized Office package back out through `IWopiWritableFile.OpenWriteAsync()` so `GetFile` and non-Cobalt clients see the result. This mirrors what `CobaltProcessor` does with `CobaltFile` + `GenericFda`.
* State lives in cellbridge's **in-memory** stores (`InMemoryStateStore` / `InMemoryContentStore`), the same single-process posture as `WopiHost.Cobalt`'s `LocalHostBlobStore`. cellbridge also ships PostgreSQL and filesystem stores for multi-instance deployments; swapping them in is a constructor change.
* Authorization stays with WOPI: the `COBALT` endpoint already requires the token's update permission, so `WopiCellBridgeAccessEvaluator` grants read/write inside cellbridge unconditionally instead of duplicating grants there.

## Enable it

The released dependency baseline is CellBridge `0.1.0-beta.4`. The normal package build supports coordinated FSSHTTP and base locks, while whole-file WOPI saving remains unavailable. Coordinated `PutFile` additionally requires the unreleased `SaveWholeFileAsync` API and an explicit source build. No package publication is part of this change:

```sh
dotnet restore src/WopiHost.CellBridge/WopiHost.CellBridge.csproj --configfile nuget.config \
  -p:CellBridgeSourceRoot=/absolute/path/to/CellBridge
dotnet build src/WopiHost.CellBridge/WopiHost.CellBridge.csproj --no-restore \
  -p:CellBridgeSourceRoot=/absolute/path/to/CellBridge
```

Run these commands from the WopiHost root. The explicit configuration gives both source checkouts the same package-source mapping. Their default configurations use different names for nuget.org, which can cause `NU1100` on an empty package cache.

`CellBridgeSourceRoot` replaces the two package references with source project references and enables the `CELLBRIDGE_WHOLE_FILE_SAVES` compilation flag. Without it, the adapter builds against beta.4 without registering `IWopiCoordinatedFileMutations`. Whole-file requests then return 501 and `CheckFileInfo.SupportsUpdate` stays false. The beta.4 packages do not contain the new API.

Use the same compatible CellBridge version for every writer against the protocol store. Stop older beta.4 hosts before enabling whole-file saves because those writers do not preserve the new durable receipt field. The source-enabled CI workflow pins the implementation dependency; mixed hosting with older writers is unsupported.

Build the package-backed adapter and run its tests with:

```sh
dotnet build WOPI.slnx
dotnet test --project test/WopiHost.CellBridge.Tests/WopiHost.CellBridge.Tests.csproj
```

Then in the sample host:

```jsonc
{
  "Sample": { "CoauthoringProvider": "CellBridge" },   // None | CobaltCore | CellBridge
  "Wopi": {
    "CellBridge": {
      "SerializationProfile": "SharePoint13_11",       // or "Current" (MS-FSSHTTPB v12 framing)
      "WebOrigin": ""                                  // WebUrl attribute on SOAP responses, if that framing is in use
    }
  }
}
```

or in your own host:

```csharp
builder.Services.AddCellBridgeProcessor(builder.Configuration);
```

When `ICobaltProcessor` is registered, `WopiHost.Core` advertises `SupportsCobalt` / `SupportsCoauth` in `CheckFileInfo` and routes `X-WOPI-Override: COBALT` bodies to it.

## Coordinated host integration

`AddCoordinatedCellBridgeProcessor` is an alternative to the experimental adapter above. It uses CellBridge's shared request processor, protocol store, authorization policy and external publisher. WOPI locks and FSSHTTP leases then use the same document state.

```csharp
// Keep the host's existing AddWopi, storage and permission-provider setup.
// Supply a durable CellBridge StorageProvider and the host's document policy.
builder.Services.AddCellBridge<MyDocumentPolicy>(protocolStorage);
builder.Services.AddCoordinatedCellBridgeProcessor<MyConditionalFileStore>(
    builder.Configuration);
```

`MyDocumentPolicy` implements `ICellBridgeAuthorizationPolicy`. It must revalidate authoritative permissions inside document transitions using immutable host-owned permission snapshots. WOPI's `IWopiPermissionProvider` supplies an additional per-request ceiling. A ceiling cannot grant permission denied by the document policy.

`MyConditionalFileStore` implements `IWopiRevisionStore`. It is a real storage capability, not a wrapper around `OpenWriteAsync`. It must return a coherent file snapshot and revision, and atomically commit new bytes, a never-reused revision token and a durable receipt for the complete delivery fingerprint. Every other destination writer must advance the same revision token. Receipts must survive restart, deletion, recreation and rebinding. The host's read-side WOPI provider must expose this same destination's bytes and revisions. `WopiRevisionDestination` adapts this capability to CellBridge's publishing API.

Configure a stable namespace for resource identity and the public origin:

```json
{
  "Wopi": {
    "CellBridge": {
      "Coordinated": {
        "ResourceNamespace": "cde0bd84-29ea-4edb-bfc4-470187995b1e",
        "WebOrigin": "https://documents.example.com"
      }
    }
  }
}
```

Keep the namespace stable across restarts and replicas. SOAP requests must target the authenticated WOPI file route or supply its matching protocol resource GUID. An unknown or different supplied GUID is rejected before import. The adapter derives IDs from the namespace and opaque, case-sensitive file ID. It uses explicit authenticated subjects, with no anonymous fallback.

The packaged publishing worker discovers pending revisions after restart. A lost acknowledgement retries the same operation, and a remote revision conflict keeps both the remote bytes and the pending local save. The destination's receipt makes duplicate delivery safe. `AllowVolatileProtocolState=true` is available for disposable single-process experiments; it disables restart recovery and is off by default.

The Core lock routes pass the authenticated principal and expected token to the coordinated adapter. Acquire, refresh, release and replace are atomic. Tokens use ordinal comparison. An active WOPI lock excludes FSSHTTP leases and saves, and an active FSSHTTP lease excludes WOPI acquisition. Foreign protocol lock tokens are not exposed as WOPI tokens.

The source-enabled coordinated adapter implements `IWopiCoordinatedFileMutations`. For `.docx`, `.xlsx` and `.pptx` files, `PUT` and `POST` on `/wopi/files/{id}/contents` call `SaveWholeFileAsync`; they never open a legacy write stream. CellBridge stages and validates the package and graph, then commits the revision, history, receipt and external publication entry under its shared authority. A matching active WOPI lock and its authenticated owner are required for an existing nonempty file. The unlocked create-new case is restricted to a current zero-byte file with no active WOPI or FSSHTTP locks. Permissions, lifecycle and coordination state are checked again at commit. `ReadOnly` removes write access even when `UserCanWrite` is also present.

A successful response waits until the ordered destination queue has acknowledged the accepted revision. `X-WOPI-ItemVersion` is the destination's acknowledged revision, which the host's read provider must expose as well. Delivery attempts are bounded to 16 entries and 30 seconds per request. Transient storage failures during import, save or delivery and still-pending delivery return 503. Destination conflicts and blocked publication return 409 without replacing the remote bytes. A 503 can precede acceptance or follow an uncertain commit; reuse the operation ID to recover its durable receipt. The accepted local revision and its durable queue entry remain available for recovery. Other file formats return 501 and do not advertise update support. Invalid package content in a supported format returns 400; storage or host size limits return 413. Lock conflicts return 409 with only the current WOPI token or an empty token; foreign protocol tokens remain private.

Clients may send the optional WopiHost extension `X-WopiHost-OperationId` as a nonempty GUID in `D` format. Retrying the same operation ID, subject, bytes and lock token reuses its accepted revision and retries its existing publication entry. Reusing an ID with different bytes or lock authority returns 409. Calls without the header receive a new operation identity per request. Ordinary WOPI clients do not need this extension. `X-WOPI-CorrelationId` is not an idempotency key. Completion callbacks are best-effort request hooks and do not replace durable revision receipts or publication work.

Only the selected whole-file endpoint can enter this path. A client cannot enable it by spoofing `X-WOPI-Override`. Registering only `IWopiEditingCoordinator` still leaves whole-file saving unavailable.

The remaining mutation routes return 501 with an `X-WopiHost-MutationFailureReason` explanation:

| Route | Missing coordination |
| --- | --- |
| `PUT_RELATIVE`, including non-overwrite creation | Destination namespace reservation, target authorization and lifecycle binding; overwrite also needs target lock authority and revision publication. |
| `RENAME_FILE` | Atomic catalog name and protocol path updates. |
| `DELETE` | Authoritative tombstone and publication retirement before destination deletion. |
| `CREATE_CHILD_FILE` | Atomic namespace reservation, destination creation and protocol binding. |
| `CREATE_CHILD_CONTAINER` | A shared namespace authority. |
| `RENAME_CONTAINER` | Atomic subtree path updates. |
| `DELETE_CONTAINER` | Atomic emptiness and tombstone checks against concurrent creation. |

`CheckFileInfo` advertises whole-file update only when its mutation capability is registered. Rename, delete, file creation and relative writing remain disabled. `CheckContainerInfo` disables every container mutation flag. Direct host mutations outside these routes must obey the same authority. This integration does not establish Office Online Server interoperability or complete WOPI editing support.

There is no production `IWopiRevisionStore` implementation in this repository. Test providers qualify synthetic and transactional test scenarios; they do not qualify a host's production storage. A production host must test real restart recovery, acknowledgement loss, receipt retention and concurrent external writers using its actual backend.

## Try cellbridge itself

cellbridge's own demo (desktop Office editing against its document library) runs from the WopiHost AppHost with `AppHost:UseCellBridge=true`: a PostgreSQL container, schema init, a seeded `integration-writer` account (`AppHost:CellBridgeTestPassword`) and the library at `https://localhost:7292/library`. Windows desktop Office must trust the ASP.NET Core dev certificate; see [cellbridge's demo guide](https://github.com/PatrickMatthiesen/cellbridge/blob/main/docs/demo-library.md). This lane demonstrates cellbridge, not WopiHost + cellbridge: nothing Docker-distributable speaks Cobalt over WOPI.

The demo's host and tools (`CellBridge.Web`, `CellBridge.Demo`, `CellBridge.Storage.Setup`, `CellBridge.Admin`) are not packaged, so the lane builds them from a source checkout:

```sh
# next to the WopiHost checkout, so the two trees are siblings
git clone https://github.com/PatrickMatthiesen/cellbridge ../cellbridge
dotnet build WOPI.slnx            # IncludeCellBridgeDemo auto-detects ../cellbridge/CellBridge.slnx
```

`IncludeCellBridgeDemo` (root `Directory.Build.props`) can be forced with `-p:IncludeCellBridgeDemo=true|false`; `-p:CellBridgeRepoRoot=<path>` points elsewhere. The checkout must stay **outside** the WopiHost tree, otherwise it inherits WopiHost's central package management and fails to restore. Without it the AppHost compiles without the lane. `.github/workflows/cellbridge-integration.yml` builds the lane weekly against the cellbridge commit the packages were built from (`CELLBRIDGE_PINNED_REF`); move the pin and the package versions together.

## What is still unknown

1. **The WOPI `COBALT` body framing.** `ICobaltProcessor` receives bytes without a Content-Type. cellbridge parses SOAP (what desktop Office posts to `cellstorage.svc`) and raw MS-FSSHTTPB cell requests; `Microsoft.CobaltCore` reads its own `RequestBatch` wire format, which carries lock/co-auth/editors subrequests *and* cell requests in one binary batch. `WopiCobaltFraming` sniffs for the two framings cellbridge understands and the processor throws `NotSupportedException` (logging the first bytes) for anything else. **The first milestone is capturing a real OOS `COBALT` request** — set the sample's logging to `Debug` and hit the endpoint from an OOS/OWA 2013 session — and teaching cellbridge (or this adapter) that framing. This was also the first step the [#321](https://github.com/petrsvihlik/WopiHost/issues/321) analysis called for.
2. **MTOM over WOPI.** If OOS posts `multipart/related`, the boundary lives in the Content-Type header the current `ICobaltProcessor` signature drops; the contract would need to carry it.
3. **Subrequests this adapter still dispatches itself.** `EditorsTable`, `GetDocMetaInfo` and `GetVersions` answer `NotSupported` here. cellbridge `0.1.0-beta.2` added `CellBridgeRequestProcessor`, which executes a parsed `CellStorageRequest` without an `HttpContext`; the SOAP path here should delegate to it instead of switching on subrequest types.
4. **Eviction.** Imported documents stay in memory for the process lifetime; `CobaltProcessor` evicts idle sessions after 60 minutes. beta.2's `IDocumentLifecycleStore.TryDeleteAsync` deletes a document under generation checks, which fits WOPI `DELETE`; dropping an idle document from memory without deleting it is a different operation and still needs a policy here.
5. **Two-desktop co-authoring** is unverified in cellbridge itself (single-desktop Word/Excel/PowerPoint save + reopen is).

## How the two sets of abstractions line up

Side by side, with what the first integration showed about each seam:

| Concern | WopiHost | cellbridge | Fit |
|---|---|---|---|
| Published document bytes | `IWopiWritableFile.OpenReadAsync` / `OpenWriteAsync` — one mutable stream per file, committed on dispose | `IContentStore` — immutable, content-addressed blobs (`WriteAsync(stream) → ContentHandle{Sha256}`), used for the materialized package **and** every graph element payload | Half. The materialized package maps cleanly (this adapter mirrors it on publish). Graph payloads are protocol state WopiHost has no slot for and should not grow one — `WopiHost.Cobalt` keeps CobaltCore's `HostBlobStore` private for the same reason. |
| Protocol state (storage index, knowledge, editors, leases, receipts) | none — `WopiHost.Cobalt` keeps `CobaltFile` sessions and the editors table in process memory | `IDocumentStateStore.TransitionAsync(id, (current, now) => …)` — atomic publish of an immutable `DocumentState` under per-document coordination with authoritative time | No counterpart, and cellbridge's model is the stronger one: a durable, multi-instance co-authoring state store is something WopiHost gains by adopting it rather than wrapping it. |
| Locks | `IWopiLockProvider` — one lock id per file, 30-minute expiry, CAS (`TryUnlockAndRelockAsync`, `RefreshLockAsync(expected)`) | `CoordinationState` — a schema lock shared by co-authors, an exclusive lease, coauthor transitions | Overlapping but disjoint today: WOPI `PutFile` honours `IWopiLockProvider` while FSSHTTP saves honour the lease — true with CobaltCore as well (`CobaltHostLockingStore` answers every lock request with an empty success). A cellbridge exclusive lease that consults `IWopiLockProvider` would be the first time both save paths share one lock domain. The most valuable unification target. |
| Document identity | opaque string ids (`IWopiResource.Identifier`, SHA-256 of a canonical path) | `ResourceId` GUID plus URL `PathKey`; beta.2's `ImportAsync` / `CreateAsync` overloads accept a host-supplied GUID | This adapter still keeps a map. Deriving the GUID from `IWopiResource.Identifier` and passing it in removes the map and survives restarts with a durable state store. |
| Caller identity | `ClaimsPrincipal` from the WOPI access token (`NameIdentifier`, `Name`) | `CellBridgeActor.FromPrincipal` over `cellbridge:subject` / `cellbridge:display-name` claims | Clean. `ActorFor` maps `NameIdentifier` to `wopi:<id>`. |
| Permissions | capability-style: `wopi:fperms` baked into the token at mint time, read through `IWopiPermissionProvider.GetFilePermissionsAsync(principal, file)` (async) | ACL-style: `DocumentSecurity` grants stored on the document, evaluated by `ICellBridgeAccessEvaluator.Evaluate(actor, state)` (sync, inside transactions) | Different philosophies, one seam. The token is WOPI's source of truth, so the evaluator defers to it; today it grants read/write unconditionally because the `COBALT` endpoint already requires the token's update permission. Having `QueryAccess` report read-only to a viewer needs the host's decision to travel with the request — which is what beta.2's `CellBridgeActor.AccessLimit` (a per-request ceiling scoped to one resource) and `ICellBridgeAuthorizationPolicy` (host-owned, synchronous, snapshots resolved outside transactions) provide. Not adopted here yet. |
| Sign-in | WOPI access token + proof keys; Office Online Server never sees a login page | MS-OFBA forms sign-in for desktop Office (`CellBridge.Authentication`) | Not applicable over WOPI; stays out. |

What a real unification needs from cellbridge, in priority order, and what `0.1.0-beta.2` already answers. The adapter was written against an earlier commit and adopts none of these yet; that is the next step.

1. A transport-agnostic request processor (SOAP or binary request model in, response model out, no `HttpContext`), so `EditorsTable`, `GetDocMetaInfo`, `GetVersions` and the Coauth transitions are reachable from a host that owns its routes and sign-in. **beta.2:** `CellBridgeRequestProcessor.ExecuteAsync(CellStorageRequest, …)` returns the response plus the accepted saves.
2. Host-supplied document identity, or a documented deterministic derivation. **beta.2:** `ImportAsync(Guid resourceId, …)` / `CreateAsync(Guid resourceId, …)`.
3. A per-request access decision, so the host's `IWopiPermissionProvider` result drives `QueryAccess` and write checks instead of stored grants. **beta.2:** `CellBridgeActor.AccessLimit` plus `ICellBridgeAuthorizationPolicy`; both are synchronous and expect the host to resolve permissions before the transaction, which matches the conclusion below.
4. A publication hook (revision published → bytes) instead of comparing `ContentVersion` after each call, so the host writes the file through its own storage provider exactly once per save. **beta.2:** `ExternalRevisionPublisher` over `IExternalRevisionDestination.CompareExchangeAsync(request, verifiedContent)`. Its contract asks for more than `IWopiWritableFile.OpenWriteAsync` offers — an atomic compare-exchange on a revision token and a durable, deduplicating receipt — so this is the concrete gap in WopiHost's storage contract the exercise surfaces.
5. Delete/evict on the state store, so idle documents can leave memory the way `CobaltProcessor` evicts sessions. **beta.2:** `IDocumentLifecycleStore.TryDeleteAsync` covers delete; idle eviction remains the adapter's.

What the exercise said about WopiHost's own contracts: `ICobaltProcessor` is the right seam (no `WopiHost.Core` change was needed) but drops the request Content-Type, which an MTOM body would need; `IWopiWritableFile.OpenWriteAsync` has no version precondition, so a host cannot ask for "write only if still at version N" the way cellbridge's publish does; and `IWopiPermissionProvider` is async and principal-based where cellbridge needs a synchronous answer inside a transaction, so an adapter has to resolve permissions up front and hand the result in.

## Distribution

cellbridge publishes its nine libraries to NuGet.org as `0.1.0-beta.*` prereleases (`CellBridge.AspNetCore`, `CellBridge.FssHttp`, `CellBridge.FssHttpB`, `CellBridge.Storage`, `CellBridge.Storage.Abstractions`, `CellBridge.Storage.InMemory`, `CellBridge.Storage.FileSystem`, `CellBridge.Storage.PostgreSql`, `CellBridge.Storage.Conformance`) — the channel WopiHost's own packages use, so `Directory.Packages.props` and Dependabot track them. A container image would suit cellbridge's *standalone* server, but WopiHost needs the engine in-process behind `ICobaltProcessor`, so NuGet is the channel that matters here. Only cellbridge's demo host and tools stay source-only; the AppHost lane pins a commit for those.

## License

[MIT](../../LICENSE.txt). cellbridge is MIT-licensed by Patrick Matthiesen; its vendored Microsoft protocol test code retains its own notice.
