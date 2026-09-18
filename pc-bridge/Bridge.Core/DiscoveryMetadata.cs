namespace PhantomDust.PcBridge.Core;

public static class DiscoveryMetadata {
    public static BridgeDiscovery Create(string bridgeId,string displayName,string endpoint,string certificateSha256,bool shortCodeAvailable=true)
        => new(bridgeId,displayName,endpoint,certificateSha256,shortCodeAvailable);

    // Keep TXT values short, stable, and free of pairing secrets.
    public static IReadOnlyDictionary<string,string> Txt(BridgeDiscovery discovery)=>new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase){
        ["bridgeId"]=discovery.BridgeId,["name"]=discovery.DisplayName,["certSha256"]=discovery.CertificateSha256,
        ["connection"]="2",["shortCode"]=discovery.ShortCodeAvailable?"1":"0",["api"]=$"v{discovery.ApiVersion}"
    };
}
