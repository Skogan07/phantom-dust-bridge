namespace PhantomDust.PcBridge.App;

internal static class BridgeWindowLifecycle {
    internal static bool ShouldHide(bool userClosing, bool explicitExit) => userClosing && !explicitExit;
}
