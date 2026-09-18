using System.Reflection;
using PhantomDust.PcBridge.Core;
using Xunit;

namespace PhantomDust.PcBridge.Tests;

public sealed class ShortCodePairingTests {
    private static string Temp()=>Path.Combine(Path.GetTempPath(),"pd-bridge-tests",Guid.NewGuid().ToString(),"bridge.db");

    [Fact]
    public void ShortCodePairsAndIsOneTime() {
        using var store=new BridgeStore(Temp());
        var payload=store.BeginPairing("https://192.0.2.10:17431","cert");
        Assert.Matches("^[0-9]{4}$",payload.ShortCode!);
        var pair=store.Pair(new(null,"Phone",ShortCode:payload.ShortCode));
        Assert.Equal(pair.PairingId,store.Authenticate("Bearer "+pair.Token));
        Assert.Throws<BridgeException>(()=>store.Pair(new(null,"Phone",ShortCode:payload.ShortCode)));
    }

    [Fact]
    public void ShortCodeLocksAfterFiveFailedAttempts() {
        using var store=new BridgeStore(Temp());
        var payload=store.BeginPairing("https://192.0.2.10:17431","cert");
        var wrong=payload.ShortCode=="0000"?"9999":"0000";
        for(var i=0;i<5;i++)Assert.Throws<BridgeException>(()=>store.Pair(new(null,"Phone",ShortCode:wrong)));
        var error=Assert.Throws<BridgeException>(()=>store.Pair(new(null,"Phone",ShortCode:payload.ShortCode)));
        Assert.Contains("attempt limit",error.Message,StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ShortCodeExpiryIsRejected() {
        using var store=new BridgeStore(Temp());
        var payload=store.BeginPairing("https://192.0.2.10:17431","cert");
        typeof(BridgeStore).GetField("shortCodeExpires",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(store,Wire.Now-1);
        var error=Assert.Throws<BridgeException>(()=>store.Pair(new(null,"Phone",ShortCode:payload.ShortCode)));
        Assert.Contains("expired",error.Message,StringComparison.OrdinalIgnoreCase);
    }
}
