using CellBridge.FssHttp;

namespace WopiHost.CellBridge;

internal static class CellBridgeSoapAdmission
{
    // The beta.6 parser validates the other envelope structure. These checks happen
    // only in its request processor, so route rejected envelopes there before import.
    public static bool RequiresRejection(CellStorageRequest request) =>
        request.Version != 2 || request.MinorVersion > ushort.MaxValue ||
        request.Requests.Any(file => file.SubRequests.Any(operation =>
            operation.DependsOn is { } dependency && dependency == operation.SubRequestToken));
}
