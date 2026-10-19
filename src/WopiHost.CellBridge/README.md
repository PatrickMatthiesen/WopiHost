# WopiHost.CellBridge

WopiHost.CellBridge runs the open-source [CellBridge](https://github.com/PatrickMatthiesen/cellbridge) document editing engine inside WopiHost. Your application keeps its WOPI endpoints, authentication, permissions and file storage.

The adapter is experimental. It has not been tested with Office Online Server. See [limitations](#limitations) before using it with Office clients.

## Build and test

Run from the WopiHost repository root:

```sh
dotnet build WOPI.slnx
dotnet test --project test/WopiHost.CellBridge.Tests/WopiHost.CellBridge.Tests.csproj
```

Package versions are managed in `Directory.Packages.props`.

## Single-process setup

Use this setup for local experiments. It keeps editing state in memory and writes saved files through your existing WOPI storage provider. Editing state is lost when the application restarts.

```csharp
builder.Services.AddCellBridgeProcessor(builder.Configuration);
```

In the sample host, select CellBridge in configuration instead:

```json
{
  "Sample": { "CoauthoringProvider": "CellBridge" },
  "Wopi": {
    "CellBridge": {
      "SerializationProfile": "SharePoint13_11",
      "WebOrigin": ""
    }
  }
}
```

Register only one editing backend. For persistent editing state and shared WOPI and CellBridge locks, use the coordinated setup below.

## Coordinated setup

This setup uses the same document storage to manage WOPI locks and CellBridge editing. It also retains accepted saves so they can be delivered to your file store after a restart.

Provide a durable CellBridge `StorageProvider`, a document permission policy and a file store implementing `IWopiRevisionStore`:

```csharp
// Keep your existing AddWopi, authentication and storage registrations.
builder.Services.AddCellBridge<MyDocumentPolicy>(protocolStorage);
builder.Services.AddCoordinatedCellBridgeProcessor<MyFileStore>(
    builder.Configuration);
```

`MyDocumentPolicy` implements `ICellBridgeAuthorizationPolicy` and checks your application's current document permissions when CellBridge changes a document. Both this policy and WOPI's `IWopiPermissionProvider` must allow a write. A read-only WOPI request cannot save changes.

`MyFileStore` implements `IWopiRevisionStore`. Its saves must update the file bytes, version and a record of the completed save in a single transaction. That record lets a retry return the original result without writing the file again. Other applications writing to the same file store must also update its version. WopiHost's download provider must read these same bytes and versions.

You must supply this file-store implementation. The bundled FileSystem and Azure providers do not implement `IWopiRevisionStore`.

Configure a stable resource namespace and your public server address:

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

Choose a namespace GUID for your application and keep it unchanged across restarts and servers. Requests need an authenticated user with a stable ID.

An active WOPI lock prevents CellBridge writes, and an active CellBridge editing lock prevents WOPI from acquiring a conflicting lock. If another application changes a file before a save reaches your file store, the save reports a conflict and preserves the other application's file.

Durable storage is required by default. `AllowVolatileProtocolState=true` permits in-memory storage for disposable tests, but saves waiting for delivery cannot survive a restart in that mode.

### Enable whole-file saves

Whole-file saving supports Word `.docx`, Excel `.xlsx` and PowerPoint `.pptx` files. Build the adapter with a CellBridge checkout that provides `SaveWholeFileAsync`:

```sh
dotnet restore src/WopiHost.CellBridge/WopiHost.CellBridge.csproj --configfile nuget.config \
  -p:CellBridgeSourceRoot=/absolute/path/to/CellBridge
dotnet build src/WopiHost.CellBridge/WopiHost.CellBridge.csproj --no-restore \
  -p:CellBridgeSourceRoot=/absolute/path/to/CellBridge
```

Run these commands from the WopiHost root and replace the path with your CellBridge checkout. Keep that checkout outside the WopiHost directory.

The bundled CellBridge beta.4 packages support coordinated editing and locks but do not include this whole-file save API. With a package-only build, whole-file saves return HTTP 501 and are not advertised to clients.

Upgrade all servers sharing CellBridge storage together. Stop servers using older CellBridge versions before enabling whole-file saves.

Saving an existing nonempty file requires the user's matching WOPI lock. An empty file can be initialized without a lock if no other lock is active. The server reports success only after the saved file is available from your file store.

| Response | Meaning |
| --- | --- |
| 200 | The file store confirmed the save. |
| 400 | The upload is not a valid supported Office file. |
| 409 | The lock or stored file conflicts with the save. Resolve the conflict before saving again. |
| 413 | The upload exceeds a configured size or storage limit. |
| 501 | The operation or file format is unavailable in this setup. |
| 503 | The save could not be confirmed. It may still be waiting for delivery to the file store. |

Custom clients can send `X-WopiHost-OperationId` with a nonempty GUID, such as `f403ae33-8e79-4dd5-b45e-597d4716c7e7`, to identify a save. When retrying, keep the same ID, user, bytes and lock token. The server returns the accepted result and resumes any unfinished delivery. Reusing an ID for a different request returns 409. This header is optional for ordinary WOPI clients.

### Unsupported operations

These operations return HTTP 501 in coordinated mode:

| Operation | WOPI request |
| --- | --- |
| Copy or create a relative file | `PUT_RELATIVE` |
| Rename or delete a file | `RENAME_FILE`, `DELETE` |
| Create a file or folder | `CREATE_CHILD_FILE`, `CREATE_CHILD_CONTAINER` |
| Rename or delete a folder | `RENAME_CONTAINER`, `DELETE_CONTAINER` |

`CheckFileInfo` and `CheckContainerInfo` advertise only the available operations. Whole-file saves for other file formats also return 501.

## Try the CellBridge desktop demo

The WopiHost AppHost can start CellBridge's separate document library for testing desktop Office editing. Clone CellBridge next to WopiHost:

```sh
git clone https://github.com/PatrickMatthiesen/cellbridge ../cellbridge
dotnet build WOPI.slnx
```

Enable `AppHost:UseCellBridge=true` and set `AppHost:CellBridgeTestPassword` for the demo's `integration-writer` account. The library runs at `https://localhost:7292/library`. Follow the [CellBridge demo guide](https://github.com/PatrickMatthiesen/cellbridge/blob/main/docs/demo-library.md) for Office setup and certificate trust.

Use `-p:CellBridgeRepoRoot=/path/to/CellBridge` for a different checkout location. `-p:IncludeCellBridgeDemo=false` excludes the demo from the build.

This demo connects desktop Office directly to CellBridge. It does not test Office Online Server through WopiHost.

## Limitations

- Office Online Server's COBALT request format has not been verified with this adapter. Unsupported request formats are rejected.
- MTOM requests require a Content-Type header that `ICobaltProcessor` does not currently pass to the adapter.
- Two-desktop coauthoring remains unverified in CellBridge.
- The single-process setup retains imported documents in memory until the application exits.

## License

[MIT](../../LICENSE.txt). CellBridge is MIT-licensed by Patrick Matthiesen. Its vendored Microsoft protocol test code retains its own notice.
