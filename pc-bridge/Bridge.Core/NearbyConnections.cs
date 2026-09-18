using System.Security.Cryptography;

namespace PhantomDust.PcBridge.Core;

public sealed record NearbyConnectRequest(string RequestId,string Secret,string DeviceName,string DeviceIdentity);
public sealed record NearbyPoll(string RequestId,string Secret);
public sealed record NearbyConnectResult(string State,PairResponse? Pairing=null);
public sealed record NearbyPending(string RequestId,string DeviceName,long ExpiresAt);
public sealed record ReconnectDecision(string RequestId,bool Accept);

public sealed partial class BridgeStore {
    // Short-lived credentials stay in memory only. A restart requires a new request.
    private sealed record NearbyRequest(NearbyConnectRequest Request,long ExpiresAt,string State="Pending",PairResponse? Pairing=null,string Source="local",string? ExpectedDeviceId=null,string? ExpectedTokenHash=null);
    private readonly Dictionary<string,NearbyRequest> nearbyRequests=[];
    private readonly Dictionary<string,(string Id,long ExpiresAt)> reconnectRequests=[];
    public NearbyConnectResult RequestNearbyConnection(NearbyConnectRequest request,string source="local"){lock(gate){
        if(!Guid.TryParse(request.RequestId,out _)||request.Secret.Length is <43 or >88||string.IsNullOrWhiteSpace(request.DeviceName)||request.DeviceName.Length>100||!Guid.TryParse(request.DeviceIdentity,out _))throw new BridgeException(400,"Invalid connection request.");
        foreach(var id in nearbyRequests.Where(x=>x.Value.ExpiresAt<Wire.Now).Select(x=>x.Key).ToArray())nearbyRequests.Remove(id);
        if(nearbyRequests.TryGetValue(request.RequestId,out var existing)){
            if(existing.Request!=request)throw new BridgeException(409,"Connection request changed.");
            return new(existing.State); // Tokens are returned only through the private polling capability.
        }
        if(nearbyRequests.Count>=64||nearbyRequests.Values.Count(x=>x.Source==source)>=8)throw new BridgeException(429,"Too many connection requests. Try again shortly.");
        nearbyRequests.Add(request.RequestId,new(request,Wire.Now+120_000,Source:source));return new("Pending");
    }}
    public NearbyPending[] PendingConnections(){lock(gate)return nearbyRequests.Values.Where(x=>x.State=="Pending"&&x.ExpiresAt>=Wire.Now).Select(x=>new NearbyPending(x.Request.RequestId,x.Request.DeviceName,x.ExpiresAt)).ToArray();}
    public void ApproveNearbyConnection(string requestId,bool accept)=>Mutate(()=>{
        if(!nearbyRequests.TryGetValue(requestId,out var pending)||pending.ExpiresAt<Wire.Now||pending.State!="Pending")throw new BridgeException(409,"Connection request expired or already answered.");
        if(!accept){nearbyRequests[requestId]=pending with{State="Rejected"};return true;}
        var matches=state.Devices.Where(x=>!x.Revoked&&x.DeviceIdentity==pending.Request.DeviceIdentity).ToArray();
        if(matches.Length>1)throw new BridgeException(409,"Multiple matching devices. Unpair obsolete entries first.");
        var approvedDevice=matches.SingleOrDefault();
        nearbyRequests[requestId]=pending with{State="Approved",ExpiresAt=Wire.Now+120_000,ExpectedDeviceId=approvedDevice?.Id,ExpectedTokenHash=approvedDevice?.TokenHash};
        return true;
    });
    public NearbyConnectResult PollNearbyConnection(NearbyPoll request)=>Mutate(()=>{
        if(!nearbyRequests.TryGetValue(request.RequestId,out var pending)||pending.ExpiresAt<Wire.Now)throw new BridgeException(410,"Connection request expired. Search again.");
        if(!Equal(Planner.Hash(pending.Request.Secret),Planner.Hash(request.Secret)))throw new BridgeException(401,"Invalid connection request credential.");
        if(pending.State=="Approved"&&pending.Pairing is null){
            var matches=state.Devices.Where(x=>!x.Revoked&&x.DeviceIdentity==pending.Request.DeviceIdentity).ToArray();
            if(matches.Length>1)throw new BridgeException(409,"Multiple matching devices. Unpair the obsolete entries first.");
            var existing=matches.SingleOrDefault();if(existing?.Id!=pending.ExpectedDeviceId||existing?.TokenHash!=pending.ExpectedTokenHash)throw new BridgeException(409,"Pairing changed after approval. Request connection again.");var id=existing?.Id??Guid.NewGuid().ToString();var token=Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            if(existing is null)state.Devices.Add(new(id,pending.Request.DeviceName,Planner.Hash(token),false,false,Wire.Now,Wire.Now,pending.Request.DeviceIdentity));
            else state.Devices=state.Devices.Select(x=>x.Id==id?x with{TokenHash=Planner.Hash(token),FallbackTokenHash=x.FallbackTokenHash??x.TokenHash,LastSeenAt=Wire.Now,Paused=true}:x).ToList();
            pending=pending with{Pairing=new(id,token,state.BridgeId),ExpiresAt=Wire.Now+300_000};nearbyRequests[request.RequestId]=pending;
        }
        if(pending.Pairing is {} pair&&!state.Devices.Any(x=>x.Id==pair.PairingId&&!x.Revoked&&Equal(x.TokenHash,Planner.Hash(pair.Token))))throw new BridgeException(401,"Connection approval is no longer valid.");
        return new NearbyConnectResult(pending.State,pending.Pairing);
    });
    public void FinishNearbyConnection(string deviceId,string authorization)=>Mutate(()=>{
        var device=state.Devices.Single(x=>x.Id==deviceId&&!x.Revoked);
        if(!authorization.StartsWith("Bearer ")||!Equal(device.TokenHash,Planner.Hash(authorization[7..])))throw new BridgeException(401,"Confirm using the newly saved phone credential.");
        state.Devices=state.Devices.Select(x=>x.Id==deviceId?x with{FallbackTokenHash=null}:x).ToList();return true;
    });
    public string RequestDeviceReconnect(string deviceId){lock(gate){
        if(!state.Devices.Any(x=>x.Id==deviceId&&!x.Revoked))throw new BridgeException(409,"This device is unpaired. Open Connect PC on the phone to request a new connection.");
        var id=Guid.NewGuid().ToString();reconnectRequests[deviceId]=(id,Wire.Now+120_000);return id;
    }}
    public string? PendingDeviceReconnect(string deviceId){lock(gate)return reconnectRequests.TryGetValue(deviceId,out var request)&&request.ExpiresAt>=Wire.Now?request.Id:null;}
    public void AnswerDeviceReconnect(string deviceId,ReconnectDecision decision){lock(gate){
        if(PendingDeviceReconnect(deviceId)!=decision.RequestId)throw new BridgeException(409,"Reconnect request expired or already answered.");
        reconnectRequests.Remove(deviceId);
        // Confirmation reconnects transport only; it never resumes paused writes.
    }}
}
