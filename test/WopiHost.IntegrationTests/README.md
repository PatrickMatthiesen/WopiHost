# WopiHost.IntegrationTests

End-to-end tests that exercise [`WopiHost.Web.Oidc`](../../sample/WopiHost.Web.Oidc/README.md) and the WOPI backend (`sample/WopiHost`) together, including a real OIDC server when Docker is available.

## What's covered

| Test class | What it verifies | Docker required |
|---|---|---|
| `OidcRolePermissionMapperTests` | OIDC role → `WopiFilePermissions` mapping across claim shapes | No |
| `WopiAccessTokenMinterTests` | WOPI access-token format, claims, signing-key round-trip | No |
| `OidcStartupTests` | Anonymous request redirects to the configured authority; discovery document is reachable; health endpoint is anonymous | **Yes** |
| `WopiTokenRoundTripTests` | Authenticated user → minted token contains OIDC identity → backend `CheckFileInfo` reflects that identity | No (uses `TestAuthHandler`) |

## What's not covered (and why)

- **Real Entra/Auth0/Okta tenants.** Only the OIDC handshake against a Navikt `mock-oauth2-server` is automated. The sample's [README](../../sample/WopiHost.Web.Oidc/README.md) walks through pointing the same code at a real IdP — that is a manual smoke test, not an automated one.
- **MSAL.js / MSAL.NET client flows.** The sample is pure server-side OIDC; no Microsoft-Identity-Web dependency.
- **Conditional Access, guest users, B2B.** Real-tenant only.

## Running

```bash
# Full suite (requires Docker for the OIDC startup tests)
# --project requires the .csproj file path, not the directory
dotnet test --project test/WopiHost.IntegrationTests/WopiHost.IntegrationTests.csproj

# Skip Docker-backed tests (e.g. on a machine without Docker Desktop / Colima)
dotnet test --project test/WopiHost.IntegrationTests/WopiHost.IntegrationTests.csproj --filter-not-class "*OidcStartupTests"
```

The Docker-backed tests use xUnit v3's built-in `Assert.SkipUnless(...)` to mark themselves as **skipped** (not failed) when Docker is unavailable, so the build stays green.

## How the test factories fit together

```
┌─────────────────────────────┐         ┌──────────────────────────┐
│ MockOidcServerFixture       │◀────────│ OidcWebAppFactory        │
│ (Testcontainers, Docker)    │ OIDC    │ (real OIDC handshake)    │
│ ghcr.io/navikt/             │ flow    │ Used by OidcStartupTests │
│ mock-oauth2-server          │         └──────────────────────────┘
└─────────────────────────────┘
                                          ┌──────────────────────────┐
                                          │ TestAuthOidcWebAppFactory│
                                          │ (TestAuthHandler stand-in│
                                          │  for OIDC; no Docker)    │
                                          │ Used by WopiTokenRoundTrip│
                                          └─────────────┬────────────┘
                                                        │
                                                        │ minted WOPI
                                                        │ access token
                                                        ▼
                                          ┌──────────────────────────┐
                                          │ WopiBackendFactory       │
                                          │ (sample/WopiHost; proof  │
                                          │  validation stubbed)     │
                                          └──────────────────────────┘
```

Both factories wire the same WOPI signing secret, so a token minted by the frontend factory validates on the backend factory.

### Coordinated whole-file saves against CellBridge source

The new whole-file API is available through an explicit source build until a package includes it:

```sh
dotnet build test/WopiHost.IntegrationTests/WopiHost.IntegrationTests.csproj -p:CellBridgeSourceRoot=/absolute/path/to/CellBridge
dotnet artifacts/bin/WopiHost.IntegrationTests/debug/WopiHost.IntegrationTests.dll -class "*CoordinatedPutFileHttpTests"
```

The suite sends authenticated HTTP requests through the sample backend with the real coordinated
processor. A test-only SQLite provider commits destination bytes, revision tokens and delivery
receipts in one on-disk transaction. Direct stream writes throw. Protocol storage defaults to the
explicitly enabled volatile test mode. Set `WOPI_TEST_POSTGRES` to a disposable PostgreSQL connection
string to run the same cases with durable protocol state and content. The tests create and drop a
unique schema per case, so the database account must have schema creation permission.

These tests prove the adapter contract with transactional test storage. They do not qualify the
bundled FileSystem or Azure providers as `IWopiRevisionStore` implementations and do not prove
Office Online Server COBALT interoperability.
