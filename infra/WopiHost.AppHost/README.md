# WopiHost.AppHost

The [.NET Aspire](https://learn.microsoft.com/dotnet/aspire/) orchestrator for local development. One
`dotnet run` here boots the whole topology — the WOPI backends, the sample frontends, and the real WOPI
client containers — and opens the Aspire dashboard.

```bash
dotnet run --project infra/WopiHost.AppHost
```

## What it starts

Each editor runs in its own **lane** — a dedicated WOPI backend + sample frontend — so the host can be
exercised against two independent real clients side by side. The resource names are suffixed by lane:

| Lane | Backend | Frontend | Client container |
|---|---|---|---|
| Collabora (default) | `wopihost-collabora` | `wopihost-web-collabora` | `collabora` (`collabora/code`) |
| ONLYOFFICE (default) | `wopihost-onlyoffice` | `wopihost-web-onlyoffice` | `onlyoffice` (`onlyoffice/documentserver`) |

Plus `wopihost-validator` (the WOPI protocol validator) and, when Redis locks are on, a `wopi-locks`
Redis container shared by both backends. (With Collabora disabled there's no in-Docker client, so the
default lane falls back to the plain `wopihost` / `wopihost-web` names.)

## Opting out of Docker clients

The orchestrated dev loop defaults to **both** Collabora and ONLYOFFICE, plus Redis — all Docker-backed.
That's the realistic multi-client setup, but the images are large (ONLYOFFICE alone is ~4.3 GB and bundles
its own Postgres/RabbitMQ). **If you don't want to pull or run an image, turn its flag off.** Each flag is
a code default, so any of the usual configuration sources overrides it — pick whichever is most convenient:

| To disable… | Set |
|---|---|
| ONLYOFFICE (skip the ~4.3 GB image) | `AppHost:UseOnlyOffice` → `false` |
| Collabora (skip the ~1 GB image) | `AppHost:UseCollabora` → `false` |
| Redis locks (use the in-memory provider) | `AppHost:UseRedisLocks` → `false` |

Turn **both** editor flags off to run with no Docker at all (the backends + frontends still start; the
frontends just have no live editor to embed).

Three ways to set them, in increasing precedence:

```bash
# 1. Local config file (not committed — see note below)
#    infra/WopiHost.AppHost/appsettings.Development.json
{ "AppHost": { "UseOnlyOffice": false } }

# 2. Environment variable (double underscore = config separator)
$env:AppHost__UseOnlyOffice = 'false'   # PowerShell
AppHost__UseOnlyOffice=false            # bash

# 3. Command line
dotnet run --project infra/WopiHost.AppHost -- --AppHost:UseOnlyOffice=false
```

> **Don't commit `appsettings.Development.json` with these flags flipped.** The defaults live in code
> precisely so a local override stays local — committing an opt-out file would impose your choice on
> everyone. (A `launchSettings.json` env var would also override the code default, but it sits *above*
> `appsettings.*` in precedence and is committed, so it's the wrong place for a personal preference.)

## All AppHost flags

| Flag | Default | Adds |
|---|---|---|
| `AppHost:UseCollabora` | `true` | `collabora/code` container + the Collabora lane. |
| `AppHost:UseOnlyOffice` | `true` | `onlyoffice/documentserver` container + the ONLYOFFICE lane. |
| `AppHost:OnlyOfficeProofValidation` | `false` | Runs the ONLYOFFICE lane's backend with WOPI proof validation on. ONLYOFFICE signs its callbacks and the validator accepts them (the nightly E2E ONLYOFFICE suite runs with this flag on); the dev-loop default just mirrors the Collabora lane's proof-off posture. |
| `AppHost:UseRedisLocks` | `true` | `wopi-locks` Redis container; both backends use the distributed lock provider against it. |
| `AppHost:UseAzureStorage` | `false` | Azurite emulator + `BlobStorage` connection string forwarded to the backends. |
| `AppHost:IncludeOidcSample` | `false` | The `WopiHost.Web.Oidc` frontend (requires IdP setup — see its README). |
| `AppHost:UseCellBridge` | `false` | The [CellBridge](https://github.com/PatrickMatthiesen/cellbridge) beta6 source demo lane. Adds PostgreSQL, schema setup, a seeded `integration-writer` account and `CellBridge.WebDemo` at `https://localhost:7292/library`. WebDemo hosts both the library and protocol endpoints in one process. Requires `IncludeCellBridgeDemo=true` at build time; see below. |
| `AppHost:CellBridgeTestPassword` | `WopiHost-CellBridge-Demo-2026!` | Password of the seeded `integration-writer` account in the cellbridge lane (12+ chars with upper, lower, digit and symbol). |

## CellBridge source demo

The demo requires the CellBridge beta6 source checkout, which contains
`src/CellBridge.WebDemo`, `tools/CellBridge.Storage.Setup` and `tools/CellBridge.Admin`.
`CellBridgeRepoRoot` defaults to a sibling `../cellbridge` checkout.
`IncludeCellBridgeDemo` is auto-enabled when that checkout contains `CellBridge.slnx`;
it can also be set explicitly. Keep the checkout outside this repository so it uses its
own package configuration. For a checkout in another location:

```bash
# Build the optional source lane, then enable it at runtime.
dotnet build infra/WopiHost.AppHost -p:IncludeCellBridgeDemo=true -p:CellBridgeRepoRoot=/path/to/cellbridge
```

Start the AppHost with `AppHost:UseCellBridge=true` using the same build properties.
Setting `IncludeCellBridgeDemo=false` compiles the AppHost without the lane, even when
`AppHost:UseCellBridge=true` is configured.

Open `https://localhost:7292/library` and sign in as `integration-writer` with
`AppHost:CellBridgeTestPassword`. The PostgreSQL volume `wopihost-cellbridge-storage`
preserves the database across runs. The seed command rejects an existing test account
whose password or permissions differ, so changing the configured password does not
reset an existing account. Authentication uses CellBridge's local key-protection
certificate under the current user's local application-data directory.

This is CellBridge's standalone demo, separate from the WopiHost CellBridge adapter.
Desktop Office needs a reachable, trusted HTTPS origin. Building or starting this lane
does not verify desktop editing or Office Online interoperability. Consult the beta6
checkout's `docs/demo-library.md` and `docs/interoperability.md` for its client coverage.

## End-to-end tests

Each lane has a nightly E2E suite that boots this AppHost via `Aspire.Hosting.Testing` and drives the
editor with Playwright. Both live in [`test/WopiHost.E2ETests`](../../test/WopiHost.E2ETests) (one
project, suites selected by a `Client` trait):

- [`e2e-collabora.yml`](../../.github/workflows/e2e-collabora.yml) → `--filter-trait "Client=Collabora"`
- [`e2e-onlyoffice.yml`](../../.github/workflows/e2e-onlyoffice.yml) → `--filter-trait "Client=OnlyOffice"`

Neither gates per-PR CI (they're `[Trait("Category", "E2E")]`, filtered out by the
`TestingPlatformCommandLineArguments` property in
[`Directory.Build.props`](../../Directory.Build.props)); they run on a nightly cron +
`workflow_dispatch`.
