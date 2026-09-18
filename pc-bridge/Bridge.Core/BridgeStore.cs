using Microsoft.Data.Sqlite;

using System.Security.Cryptography;

using System.Text.Json;
using System.Text.Json.Serialization;

namespace PhantomDust.PcBridge.Core;



public sealed record PairedDevice(string Id,string Name,string TokenHash,bool Revoked,bool Paused,long PairedAt=0,long LastSeenAt=0,string? DeviceIdentity=null,long UnpairedAt=0,bool Hidden=false,string? FallbackTokenHash=null);

public sealed record DeviceSummary(string Id,string Name,bool Revoked,bool Paused,long PairedAt,long LastSeenAt,string Presence,long UnpairedAt=0);

public sealed record BridgeNotice(string Id,string ArsenalName,string Message,long CreatedAt,bool Delivered=false);

public sealed record StoredProfileImport(string ImportId,string PairingId,string RequestHash,ProfileImportResult Result);
public sealed record StoredStoreWatch(string PairingId,string ProfileKey,IReadOnlyList<string> SkillIds,long UpdatedAt,SkillStoreResult Result);

public sealed class StoredState {

    public const int CurrentSchemaVersion = 2;
    public int SchemaVersion {get;set;}=CurrentSchemaVersion;

    public string BridgeId {get;set;}=Guid.NewGuid().ToString();

    public string? CertificateSha256 {get;set;}

    public string DisplayName {get;set;}=Environment.MachineName;

    public bool NameConfirmed {get;set;}

    public List<BridgeNotice> Notices {get;set;}=[];

    public bool Paused {get;set;}

    public List<PairedDevice> Devices {get;set;}=[];

    public List<PcLink> Links {get;set;}=[];

    public List<JobView> Jobs {get;set;}=[];
    public HashSet<string> CancelledJourneys {get;set;}=[];

    public List<StoredProfileImport> ProfileImports {get;set;}=[];

    public List<StoredStoreWatch> StoreWatches {get;set;}=[];
    public Dictionary<string,int> SkillStorePrices {get;set;}=[];
    public SkillStoreResult? LastStoreObservation {get;set;}
    public string? LastStoreObservationSignature {get;set;}
    public long StoreSessionGeneration {get;set;}
    public bool StoreSessionNotified {get;set;}

    [JsonExtensionData] public Dictionary<string,JsonElement>? FutureFields {get;set;}

}

// One gate serializes all profile/queue commands; SQLite commits the complete state atomically.

// Network payloads never contain process addresses. Tokens are stored as one-way hashes.

public sealed partial class BridgeStore : IDisposable {

    private static readonly IReadOnlyDictionary<string,int> InitialSkillStorePrices=new Dictionary<string,int>{{"003",150},{"004",400},{"008",100},{"011",50},{"013",50},{"017",50},{"018",100},{"025",50}};

    private readonly object gate=new(); private readonly SqliteConnection db;

    private StoredState state; private string? bootstrapHash; private string? shortCodeHash; private long expires; private long shortCodeExpires; private int attempts; private int shortCodeAttempts; private bool writerBusy;

    public BridgeStore(string path) {

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);

        db=new(new SqliteConnectionStringBuilder { DataSource=path }.ToString()); db.Open();

        using(var c=db.CreateCommand()){c.CommandText="PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; CREATE TABLE IF NOT EXISTS state (id INTEGER PRIMARY KEY CHECK(id=1), json TEXT NOT NULL); CREATE TABLE IF NOT EXISTS notice_receipts (id TEXT PRIMARY KEY NOT NULL);";c.ExecuteNonQuery();}

        using(var c=db.CreateCommand()){c.CommandText="SELECT json FROM state WHERE id=1";state=c.ExecuteScalar() is string json?JsonSerializer.Deserialize<StoredState>(json,Wire.Json)??throw new InvalidDataException("Bridge state is empty."):new();}
        if(state.SchemaVersion>StoredState.CurrentSchemaVersion)throw new InvalidOperationException($"Bridge state schema {state.SchemaVersion} is newer than this Bridge supports; update the Bridge before starting it.");
        state.SchemaVersion=StoredState.CurrentSchemaVersion;
        state.SkillStorePrices??=[];
        foreach(var (skillId,price) in InitialSkillStorePrices)state.SkillStorePrices.TryAdd(skillId,price);
        foreach(var offer in state.LastStoreObservation?.Matches??[])if(offer.Price is >0)state.SkillStorePrices.TryAdd(offer.SkillId,offer.Price.Value);

        state.Jobs=state.Jobs.Select(j=>j.State==JobState.Completed&&j.Changes is not null&&j.Evidence?.SessionKey.StartsWith("process-v1:")!=true?j with{State=JobState.AppliedAwaitingGameSave,Reason="Synced to PC. Save in Phantom Dust to keep these changes."}:j).ToList();
        state.Jobs=state.Jobs.Select(j=>j.State==JobState.Applying?j with{State=JobState.RecoveryRequired,Reason="Interrupted operation; manual recovery required.",Phase="ReviewRequired",PhaseMessage="Review the current PC arsenal before syncing again.",UpdatedAt=Wire.Now}:j).ToList();
        foreach(var group in state.Jobs.GroupBy(j=>(j.PairingId,j.DeckId))){var laterExecution=group.Where(j=>j.State is JobState.Applying or JobState.AppliedAwaitingGameSave or JobState.Completed or JobState.RecoveryRequired).OrderByDescending(j=>j.CreatedAt).FirstOrDefault();if(laterExecution is null)continue;state.Jobs=state.Jobs.Select(j=>j.PairingId==group.Key.PairingId&&j.DeckId==group.Key.DeckId&&j.State==JobState.AppliedAwaitingGameSave&&j.CreatedAt<laterExecution.CreatedAt?j with{State=JobState.Superseded,Reason="Replaced by a newer sync.",Phase="Replaced",PhaseMessage="A newer revision became the active sync.",SupersededByJourneyId=laterExecution.Request.ClientJourneyId,UpdatedAt=laterExecution.CreatedAt}:j).ToList();}
        Save();

    }

    public string BridgeId { get {lock(gate)return state.BridgeId;} }

    public string DisplayName { get {lock(gate)return state.DisplayName;} }

    public bool CanCreateCertificate { get {lock(gate)return string.IsNullOrWhiteSpace(state.CertificateSha256)&&state.Devices.Count==0;} }

    public void ConfirmCertificateIdentity(string sha256) {
        var fingerprint=sha256.Trim().ToUpperInvariant();
        if(fingerprint.Length!=64||fingerprint.Any(c=>!Uri.IsHexDigit(c)))throw new ArgumentException("The Bridge certificate fingerprint is invalid.",nameof(sha256));
        lock(gate) {
            if(string.IsNullOrWhiteSpace(state.CertificateSha256)) {
                state.CertificateSha256=fingerprint;
                try{Save();}catch{state.CertificateSha256=null;throw;}
                return;
            }
            if(!string.Equals(state.CertificateSha256,fingerprint,StringComparison.Ordinal))
                throw new InvalidOperationException("The Bridge security certificate does not match this installation's saved identity. Restore %USERPROFILE%\\.phantom-dust-bridge\\certificate.dpapi or use --legacy-root on the first start for explicit repair; the Bridge will not silently replace a paired PC certificate.");
        }
    }

    public bool NameConfirmed { get {lock(gate)return state.NameConfirmed;} }

    public void RenameBridge(string name)=>Mutate(()=>{var value=name.Trim();if(value.Length is <1 or >80)throw new BridgeException(400,"PC name must be 1–80 characters.");state.DisplayName=value;state.NameConfirmed=true;return true;});

    public DeviceSummary[] HostDevices(){lock(gate)return state.Devices.Where(d=>!d.Hidden).Select(d=>new DeviceSummary(d.Id,d.Name,d.Revoked,d.Paused,d.PairedAt,d.LastSeenAt,d.Revoked?"Unpaired":d.LastSeenAt>0&&Wire.Now-d.LastSeenAt<=120_000?"Connected recently":"Not currently connected",d.UnpairedAt)).ToArray();}
    public void HideDevice(string id)=>Mutate(()=>{var device=state.Devices.SingleOrDefault(d=>d.Id==id)??throw new BridgeException(404,"Device not found.");if(!device.Revoked)throw new BridgeException(409,"Unpair this device before removing it from the list.");state.Devices=state.Devices.Select(d=>d.Id==id?d with{Hidden=true}:d).ToList();return true;});

    public void RenameDevice(string id,string name)=>Mutate(()=>{if(string.IsNullOrWhiteSpace(name)||name.Trim().Length>100)throw new BridgeException(400,"Device name must be 1–100 characters.");state.Devices=state.Devices.Select(d=>d.Id==id?d with{Name=name.Trim()}:d).ToList();return true;});

    private void Notice(string id,string name,string message){
        if(state.Notices.Any(n=>n.Id==id))return;
        using var query=db.CreateCommand();query.CommandText="SELECT 1 FROM notice_receipts WHERE id=$id";query.Parameters.AddWithValue("$id",id);
        if(query.ExecuteScalar() is null)state.Notices.Add(new(id,name,message,Wire.Now));
    }

    public BridgeNotice[] ClaimNotices()=>Mutate(()=>{var pending=state.Notices.Where(n=>!n.Delivered).ToArray();state.Notices=state.Notices.Select(n=>n with{Delivered=true}).ToList();return pending;});

    public void RecordJobNotices()=>Mutate(()=>{
        // Preserve the old "latest completed job per arsenal" rule so an
        // upgrade cannot surface ancient successes that were intentionally
        // outside the prior notification set.
        var latestCompleted=state.Jobs.Where(j=>j.State==JobState.Completed).GroupBy(j=>(j.PairingId,j.DeckId)).Select(g=>g.MaxBy(j=>j.CreatedAt)!.JobId).ToHashSet(StringComparer.Ordinal);
        foreach(var j in state.Jobs.Where(j=>(j.State is JobState.WaitingForGame or JobState.WaitingForFrames or JobState.WaitingForGameReady or JobState.Blocked or JobState.RecoveryRequired)||(j.State==JobState.Completed&&latestCompleted.Contains(j.JobId)))) {
            var milestone=j.State switch {
                JobState.WaitingForFrames or JobState.WaitingForGameReady=>"WaitingForReadiness",
                JobState.WaitingForGame=>"WaitingForGame",
                JobState.Blocked=>"Blocked",
                JobState.RecoveryRequired=>"RecoveryRequired",
                JobState.Completed=>"Completed",
                _=>null
            };
            if(milestone is null)continue;
            var message=j.State switch {
                JobState.WaitingForGame=>"Waiting for Phantom Dust to open and load the linked profile.",
                JobState.WaitingForFrames or JobState.WaitingForGameReady=>j.Reason??"Waiting for Phantom Dust to be focused and ready.",
                JobState.Blocked=>j.Reason??"This sync needs your review before it can continue.",
                JobState.RecoveryRequired=>j.Reason??"Review the current PC arsenal before syncing again.",
                JobState.Completed=>"Synced to PC.",
                _=>""
            };
            // Keep the legacy completion ID so upgrading the Bridge cannot replay
            // success balloons for jobs whose old notice was already delivered.
            var noticeId=j.State==JobState.Completed?$"job:{j.JobId}":$"job:{j.JobId}:milestone:{milestone}";
            Notice(noticeId,j.Request.Deck.Name,$"{j.Request.Deck.Name}: {message}");
        }
        return true;
    });

    private static IReadOnlyList<string> ValidateWatchedSkills(IReadOnlyList<string>? ids){
        if(ids is null||ids.Count>374||ids.Any(id=>id is null||id.Length!=3||!int.TryParse(id,out var number)||number is <1 or >374)||ids.Distinct(StringComparer.Ordinal).Count()!=ids.Count)throw new BridgeException(400,"Watch list contains an invalid or duplicate retail skill ID.");
        return ids.OrderBy(x=>x,StringComparer.Ordinal).ToArray();
    }

    private SkillStoreMatch[] KnownStorePrices()=>state.SkillStorePrices.OrderBy(x=>x.Key,StringComparer.Ordinal).Select(x=>new SkillStoreMatch(x.Key,x.Value)).ToArray();

    private SkillStoreResult ResultFor(string profileKey,IReadOnlyList<string> watched,SkillStoreResult? observation){
        if(observation is null)return new(profileKey,SkillStoreState.Unsupported,Matches:[],KnownPrices:KnownStorePrices());
        if(observation.State is not SkillStoreState.GameClosed and not SkillStoreState.Unsupported&&!string.IsNullOrWhiteSpace(observation.ProfileKey)&&observation.ProfileKey!=profileKey)return new(profileKey,SkillStoreState.ProfileMismatch,observation.ScannedAt,observation.Fingerprint,[],KnownStorePrices());
        var matches=observation.State==SkillStoreState.SkillStoreOpen
            ? (observation.Matches??[]).Where(x=>watched.Contains(x.SkillId,StringComparer.Ordinal)).OrderBy(x=>x.SkillId,StringComparer.Ordinal).ToArray()
            : [];
        return new(profileKey,observation.State,observation.ScannedAt,observation.Fingerprint,matches,KnownStorePrices());
    }

    public SkillStoreResult ReplaceStoreWatch(string device,StoreWatchRequest request)=>Mutate(()=>{
        CheckDevice(device);
        var profileKey=request.ProfileKey?.Trim()??"";if(profileKey.Length is <1 or >256)throw new BridgeException(400,"A verified profile key is required.");
        var skills=ValidateWatchedSkills(request.SkillIds);
        var result=ResultFor(profileKey,skills,state.LastStoreObservation);
        state.StoreWatches.RemoveAll(x=>x.PairingId==device&&x.ProfileKey==profileKey);
        state.StoreWatches.Add(new(device,profileKey,skills,Wire.Now,result));
        if(result.State==SkillStoreState.SkillStoreOpen&&!state.StoreSessionNotified&&result.Matches is {Count:>0}){
            var message="Watched skills available: "+string.Join(", ",result.Matches.Select(x=>x.Name??x.SkillId));
            Notice($"store:{state.StoreSessionGeneration}:{result.ProfileKey}:{result.Fingerprint}","Skill Store",message);state.StoreSessionNotified=true;
        }
        return result;
    });

    public SkillStoreResult StoreWatch(string device,string profileKey){lock(gate){
        CheckDevice(device);var key=profileKey.Trim();if(key.Length is <1 or >256)throw new BridgeException(400,"A verified profile key is required.");
        var stored=state.StoreWatches.FirstOrDefault(x=>x.PairingId==device&&x.ProfileKey==key);
        return stored is null?new(key,SkillStoreState.Unsupported,Matches:[],KnownPrices:KnownStorePrices()):stored.Result with{KnownPrices=KnownStorePrices()};
    }}

    public void RecordStoreObservation(SkillStoreResult observation){
        var matches=(observation.Matches??[]).OrderBy(x=>x.SkillId,StringComparer.Ordinal).ToArray();
        if(matches.Select(x=>x.SkillId).Distinct(StringComparer.Ordinal).Count()!=matches.Length||matches.Any(x=>x.SkillId.Length!=3||!int.TryParse(x.SkillId,out var n)||n is <1 or >374||x.Price is <=0 or >999_999))throw new InvalidDataException("Skill Store observation contains an invalid offer.");
        if(observation.State==SkillStoreState.SkillStoreOpen&&(string.IsNullOrWhiteSpace(observation.ProfileKey)||string.IsNullOrWhiteSpace(observation.Fingerprint)||observation.ScannedAt is null))throw new InvalidDataException("An open Skill Store observation is incomplete.");
        var normalized=observation with{ProfileKey=observation.ProfileKey.Trim(),Matches=matches};
        var signature=JsonSerializer.Serialize(new{normalized.ProfileKey,normalized.State,normalized.Fingerprint,Matches=matches},Wire.Json);
        lock(gate)if(state.LastStoreObservationSignature==signature)return;
        Mutate(()=>{
            foreach(var offer in matches)if(offer.Price is >0)state.SkillStorePrices.TryAdd(offer.SkillId,offer.Price.Value);
            var previousOpen=state.LastStoreObservation?.State==SkillStoreState.SkillStoreOpen;
            var nowOpen=normalized.State==SkillStoreState.SkillStoreOpen;
            var profileChanged=previousOpen&&nowOpen&&state.LastStoreObservation!.ProfileKey!=normalized.ProfileKey;
            if(previousOpen&&(!nowOpen||profileChanged)){state.StoreSessionGeneration++;state.StoreSessionNotified=false;}
            state.LastStoreObservation=normalized;state.LastStoreObservationSignature=signature;
            state.StoreWatches=state.StoreWatches.Select(x=>x with{Result=ResultFor(x.ProfileKey,x.SkillIds,normalized)}).ToList();
            if(nowOpen&&!state.StoreSessionNotified){
                var watched=state.StoreWatches.Where(x=>x.ProfileKey==normalized.ProfileKey).SelectMany(x=>x.SkillIds).ToHashSet(StringComparer.Ordinal);
                var available=matches.Where(x=>watched.Contains(x.SkillId)).DistinctBy(x=>x.SkillId).OrderBy(x=>x.SkillId,StringComparer.Ordinal).ToArray();
                if(available.Length>0){var message="Watched skills available: "+string.Join(", ",available.Select(x=>x.Name??x.SkillId));Notice($"store:{state.StoreSessionGeneration}:{normalized.ProfileKey}:{normalized.Fingerprint}","Skill Store",message);state.StoreSessionNotified=true;}
            }
            return true;
        });
    }

    public object AcknowledgeImport(string device,TransferReceipt r,Snapshot snapshot)=>Mutate<object>(()=>{

        CheckDevice(device);var link=state.Links.FirstOrDefault(l=>l.Active&&l.PairingId==device&&l.ProfileKey==r.ProfileKey&&l.TargetSlot==r.TargetSlot);

        var target=snapshot.Arsenals.FirstOrDefault(a=>a.Slot==r.TargetSlot);

        if(link is null||!snapshot.ProfileVerified||snapshot.ProfileKey!=r.ProfileKey||target?.Fingerprint!=r.Fingerprint)throw new BridgeException(409,"The imported PC version changed; refresh before acknowledging it.");

        Notice($"import:{device}:{r.ReceiptId}",target.Name,$"{target.Name} synced to {state.Devices.Single(d=>d.Id==device).Name}.");return new{acknowledged=true};

    });

    private void Save(){
        // Keep only a bounded display history in the frequently rewritten state.
        // Compact indexed receipts remain durable so old jobs can never alert again.
        var retired=state.Notices.Where(n=>n.Delivered).OrderByDescending(n=>n.CreatedAt).Skip(500).ToArray();
        using var transaction=db.BeginTransaction();
        foreach(var notice in retired){using var receipt=db.CreateCommand();receipt.Transaction=transaction;receipt.CommandText="INSERT OR IGNORE INTO notice_receipts(id) VALUES($id)";receipt.Parameters.AddWithValue("$id",notice.Id);receipt.ExecuteNonQuery();}
        var retiredIds=retired.Select(n=>n.Id).ToHashSet();state.Notices.RemoveAll(n=>retiredIds.Contains(n.Id));
        using var c=db.CreateCommand();c.Transaction=transaction;c.CommandText="INSERT INTO state(id,json) VALUES(1,$json) ON CONFLICT(id) DO UPDATE SET json=excluded.json";c.Parameters.AddWithValue("$json",JsonSerializer.Serialize(state,Wire.Json));c.ExecuteNonQuery();transaction.Commit();
    }

    private T Mutate<T>(Func<T> action){lock(gate){var before=JsonSerializer.Serialize(state,Wire.Json);try{var result=action();Save();return result;}catch{state=JsonSerializer.Deserialize<StoredState>(before,Wire.Json)!;throw;}}}

    public PairPayload BeginPairing(string endpoint,string cert)=>Mutate(()=>{
        var secret=Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var shortCode=RandomNumberGenerator.GetInt32(1000,10000).ToString("D4");
        bootstrapHash=Planner.Hash(secret);shortCodeHash=Planner.Hash(shortCode);expires=Wire.Now+300_000;shortCodeExpires=expires;attempts=0;shortCodeAttempts=0;
        return new PairPayload(1,endpoint,state.BridgeId,cert,secret,expires,shortCode,shortCodeExpires,true);
    });

    public PairResponse Pair(PairRequest request)=>Mutate(()=>{

        var usingShortCode=!string.IsNullOrWhiteSpace(request.ShortCode);
        if(usingShortCode){
            shortCodeAttempts++;
            if(shortCodeAttempts>5||shortCodeHash is null||shortCodeExpires<Wire.Now||!Equal(shortCodeHash,Planner.Hash(request.ShortCode!)))throw new BridgeException(401,shortCodeAttempts>5?"Pairing code attempt limit reached. Generate another on the PC.":"Pairing code invalid, expired, or used. Generate another on the PC.");
        }else{
            attempts++;if(attempts>10||bootstrapHash is null||expires<Wire.Now||request.BootstrapSecret is null||!Equal(bootstrapHash,Planner.Hash(request.BootstrapSecret)))throw new BridgeException(401,"Pairing code invalid, expired, or used. Generate another on the PC.");
        }

        if(string.IsNullOrWhiteSpace(request.DeviceName)||request.DeviceName.Length>100)throw new BridgeException(400,"Device name must be 1–100 characters.");

        var token=Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));var now=Wire.Now;
        var matches=string.IsNullOrWhiteSpace(request.DeviceIdentity)?[]:state.Devices.Where(x=>!x.Revoked&&x.DeviceIdentity==request.DeviceIdentity).ToArray();
        if(matches.Length>1)throw new BridgeException(409,"This phone identity is ambiguous. Revoke its old pairings before pairing again.");
        var existing=matches.SingleOrDefault();var id=existing?.Id??Guid.NewGuid().ToString();
        if(existing is null)state.Devices.Add(new(id,request.DeviceName.Trim(),Planner.Hash(token),false,false,now,now,request.DeviceIdentity));
        else state.Devices=state.Devices.Select(x=>x.Id==id?x with{Name=request.DeviceName.Trim(),TokenHash=Planner.Hash(token),FallbackTokenHash=null,LastSeenAt=now}:x).ToList();
        bootstrapHash=null;shortCodeHash=null;return new PairResponse(id,token,state.BridgeId);

    });

    private static bool Equal(string a,string b)=>CryptographicOperations.FixedTimeEquals(System.Text.Encoding.UTF8.GetBytes(a),System.Text.Encoding.UTF8.GetBytes(b));

    public string Authenticate(string header){lock(gate){

        if(!header.StartsWith("Bearer ",StringComparison.Ordinal))throw new BridgeException(401,"Pair this device first.");

        var hash=Planner.Hash(header[7..]);var device=state.Devices.FirstOrDefault(x=>!x.Revoked&&(Equal(x.TokenHash,hash)||(x.FallbackTokenHash is {} fallback&&Equal(fallback,hash))))??throw new BridgeException(401,"Pairing revoked or invalid.");

        // The phone persists the replacement token before using it. Any authenticated
        // request with that token completes a lost /connect/finish acknowledgement.
        var primaryConfirmed=device.FallbackTokenHash is not null&&Equal(device.TokenHash,hash);
        if(primaryConfirmed||Wire.Now-device.LastSeenAt>=15_000){state.Devices=state.Devices.Select(d=>d.Id==device.Id?d with{LastSeenAt=Wire.Now,FallbackTokenHash=primaryConfirmed?null:d.FallbackTokenHash}:d).ToList();Save();}

        return device.Id;

    }}

    private void CheckDevice(string id){if(!state.Devices.Any(x=>x.Id==id&&!x.Revoked))throw new BridgeException(401,"Pairing revoked.");}

    public bool Paused(string? id=null){lock(gate)return state.Paused || (id is not null && state.Devices.Any(x=>x.Id==id&&(x.Paused||x.Revoked)));}

    public object Pause(string? id,bool paused)=>Mutate<object>(()=>{if(id is null)state.Paused=paused;else {CheckDevice(id);state.Devices=state.Devices.Select(x=>x.Id==id?x with{Paused=paused}:x).ToList();}return new{paused};});

    public object Revoke(string? id)=>Mutate<object>(()=>{state.Devices=state.Devices.Select(x=>(id is null||x.Id==id)&&!x.Revoked?x with{Revoked=true,UnpairedAt=Wire.Now}:x).ToList();state.Links=state.Links.Select(x=>id is null||x.PairingId==id?x with{Active=false}:x).ToList();state.StoreWatches.RemoveAll(x=>id is null||x.PairingId==id);StopWhere(x=>id is null||x.PairingId==id,"Stopped because this PC pairing was removed.");return new{unpaired=true};});

    private static bool CanStop(JobState state)=>state is JobState.Queued or JobState.WaitingForGame or JobState.WaitingForFrames or JobState.WaitingForGameReady or JobState.Blocked;

    private void StopWhere(Func<JobView,bool> predicate,string reason){state.Jobs=state.Jobs.Select(x=>predicate(x)&&CanStop(x.State)?x with{State=JobState.Cancelled,UpdatedAt=Wire.Now,Reason=reason,Phase="Cancelled",PhaseMessage=reason}:x).ToList();}

    private void SupersedeWhere(Func<JobView,bool> predicate,string reason="Replaced by a newer sync."){state.Jobs=state.Jobs.Select(x=>predicate(x)&&CanStop(x.State)?x with{State=JobState.Superseded,UpdatedAt=Wire.Now,Reason=reason,Phase="Replaced",PhaseMessage=reason}:x).ToList();}

    public PcLink Link(string device,LinkRequest r,Snapshot s)=>Mutate(()=>{

        CheckDevice(device);if(!Guid.TryParse(r.LinkId,out _)||!Guid.TryParse(r.DeckId,out _)||r.LinkGeneration<1)throw new BridgeException(400,"Invalid link identity.");

        var target=s.Arsenals.SingleOrDefault(x=>x.Slot==r.TargetSlot);

        if(!(s.ProfileVerified || s.ControlledTestSession) || s.ProfileKey!=r.ProfileKey||target is null||string.IsNullOrEmpty(target.Fingerprint)||r.ConfirmedFingerprint!=target.Fingerprint)throw new BridgeException(409,"Refresh and confirm the current verified PC target.");

        var previous=state.Links.FirstOrDefault(x=>x.LinkId==r.LinkId);
        if(previous is {Active:true} && previous.PairingId==device && previous.DeckId==r.DeckId && previous.ProfileKey==r.ProfileKey && previous.TargetSlot==r.TargetSlot && previous.LinkGeneration==r.LinkGeneration && previous.ExpectedFingerprint==r.ConfirmedFingerprint)return previous;
        if(state.Jobs.Any(x=>x.Request.ProfileKey==r.ProfileKey&&x.TargetSlot==r.TargetSlot&&x.State is JobState.Applying or JobState.AppliedAwaitingGameSave or JobState.RecoveryRequired))throw new BridgeException(409,"This PC case is applying a change or awaiting a verified save.");
        var replacing = r.ReplacesLinkId is not null ? state.Links.FirstOrDefault(x=>x.LinkId==r.ReplacesLinkId && x.Active) : null;
        if (r.ReplacesLinkId is not null) {
            if (replacing is null || replacing.PairingId!=device || replacing.ProfileKey!=r.ProfileKey || replacing.TargetSlot!=r.TargetSlot || r.ReplacesLinkGeneration!=replacing.LinkGeneration)
                throw new BridgeException(409,"The PC ownership transfer is stale. Refresh the target and review it again.");
            if (state.Jobs.Any(x=>x.PairingId==device && x.Request.LinkId==replacing.LinkId && x.State is JobState.Applying or JobState.AppliedAwaitingGameSave or JobState.RecoveryRequired))
                throw new BridgeException(409,"The current PC sync is still applying or needs recovery before this target can be replaced.");
        }
        var previouslyApplied=previous is { Active:true } && state.Jobs.Any(x=>x.PairingId==device&&x.Request.LinkId==r.LinkId&&x.State==JobState.Completed);

        var identical=r.IdenticalDeck is { } proof && proof.DeckId==r.DeckId && proof.Name==target.Name && proof.SkillIds is { Count:30 } && proof.CaseCapacity<=target.CaseCapacity && proof.CaseCapacity is >=1 and <=3 && Planner.CardContentFingerprint(proof.SkillIds)==Planner.CardContentFingerprint(target.SkillIds);
        if(r.IdenticalDeck is not null&&!identical)throw new BridgeException(409,"The matching arsenal changed. Refresh and compare again.");
        if(target.SkillIds.Count!=30 || target.SkillIds.Any(x=>x!="000")&&!previouslyApplied&&!r.OverwriteConfirmed&&!identical)throw new BridgeException(409,"Choose an empty matching case or explicitly confirm overwriting this PC arsenal.");

        if(previous is not null && (previous.PairingId!=device||previous.DeckId!=r.DeckId||previous.ProfileKey!=r.ProfileKey||previous.TargetSlot!=r.TargetSlot||r.LinkGeneration<previous.LinkGeneration||!previous.Active&&r.LinkGeneration<=previous.LinkGeneration))throw new BridgeException(409,"Stale or conflicting link.");

        if(state.Links.Any(x=>x.Active&&x.ProfileKey==r.ProfileKey&&x.TargetSlot==r.TargetSlot&&x.LinkId!=r.LinkId&&x.LinkId!=r.ReplacesLinkId))throw new BridgeException(409,"This PC slot is linked to another deck/device. Unlink it first.");

        if(state.Links.Any(x=>x.Active&&x.PairingId==device&&x.DeckId==r.DeckId&&x.LinkId!=r.LinkId&&x.LinkId!=r.ReplacesLinkId))throw new BridgeException(409,"Unlink the previous target first.");

        var link=new PcLink(r.LinkId,device,r.DeckId,r.ProfileKey,r.TargetSlot,r.LinkGeneration,r.ConfirmedFingerprint,true,s.GameProfileDisplayName);

        if(previous is not null && r.LinkGeneration==previous.LinkGeneration && r.ConfirmedFingerprint!=previous.ExpectedFingerprint)throw new BridgeException(409,"A renewed confirmation needs a new link generation.");

        if(previous is not null && r.LinkGeneration>previous.LinkGeneration)SupersedeWhere(x=>x.Request.LinkId==r.LinkId,"Replaced by a renewed PC link.");
        if(replacing is not null) {
            // The transfer and ownership retirement are one state transaction. Any
            // queued work for the old owner is acknowledged as superseded before
            // the new link becomes visible to the queue endpoint.
            SupersedeWhere(x=>x.Request.LinkId==replacing.LinkId,"Replaced by a reviewed PC target owner.");
            state.Links=state.Links.Select(x=>x.LinkId==replacing.LinkId?x with{Active=false}:x).ToList();
        }

        state.Links.RemoveAll(x=>x.LinkId==r.LinkId);state.Links.Add(link);return link;

    });

    public ProfileImportResult ImportProfile(string device,ProfileImportRequest r,Snapshot s)=>Mutate(()=>{

        CheckDevice(device);

        if(!Guid.TryParse(r.ImportId,out _)||r.Decks is null)throw new BridgeException(400,"Invalid profile import identity.");

        var requestHash=Planner.Hash(JsonSerializer.Serialize(r,Wire.Json));

        var prior=state.ProfileImports.FirstOrDefault(x=>x.ImportId==r.ImportId);

        if(prior is not null){if(prior.PairingId!=device||prior.RequestHash!=requestHash)throw new BridgeException(409,"Profile import identity was already used for different content.");return prior.Result with{AlreadyApplied=true};}

        if(!s.ProfileVerified||s.ControlledTestSession||s.ProfileKey!=r.ProfileKey||string.IsNullOrWhiteSpace(s.GameProfileDisplayName)||s.GameProfileSlot is null)throw new BridgeException(409,"Refresh and load the verified Phantom Dust profile before importing.");

        if(r.Decks.Select(x=>x.TargetSlot).Distinct().Count()!=r.Decks.Count||r.Decks.Select(x=>x.LinkId).Distinct().Count()!=r.Decks.Count||r.Decks.Select(x=>x.DeckId).Distinct().Count()!=r.Decks.Count||r.Decks.Any(x=>!Guid.TryParse(x.LinkId,out _)||!Guid.TryParse(x.DeckId,out _)))throw new BridgeException(400,"Profile import contains duplicate or invalid deck identities.");

        if(!r.Decks.Select(x=>x.TargetSlot).OrderBy(x=>x).SequenceEqual(s.Arsenals.Select(x=>x.Slot).OrderBy(x=>x)))throw new BridgeException(409,"The imported arsenal list changed. Refresh and try again.");

        var links=new List<PcLink>();

        foreach(var item in r.Decks){

            var target=s.Arsenals.Single(x=>x.Slot==item.TargetSlot);

            if(string.IsNullOrWhiteSpace(target.Fingerprint)||string.IsNullOrWhiteSpace(target.ContentFingerprint)||item.ConfirmedFingerprint!=target.Fingerprint||item.BaselineContentFingerprint!=target.ContentFingerprint)throw new BridgeException(409,"An imported PC arsenal changed. Refresh and try again.");

            if(state.Links.Any(x=>x.Active&&(x.ProfileKey==r.ProfileKey&&x.TargetSlot==item.TargetSlot||x.PairingId==device&&x.DeckId==item.DeckId)))throw new BridgeException(409,"A PC arsenal in this profile is already linked. Open the existing synced profile or unlink it first.");

            links.Add(new(item.LinkId,device,item.DeckId,r.ProfileKey,item.TargetSlot,1,target.Fingerprint,true,s.GameProfileDisplayName,target.ContentFingerprint));

        }

        state.Links.AddRange(links);

        var result=new ProfileImportResult(r.ImportId,r.ProfileKey,links);

        state.ProfileImports.Add(new(r.ImportId,device,requestHash,result));

        foreach(var target in s.Arsenals)Notice($"profile-import:{r.ImportId}:{target.Slot}",target.Name,$"{target.Name} imported by {state.Devices.Single(d=>d.Id==device).Name}.");

        return result;

    });

    public object Unlink(string device,string linkId)=>Mutate<object>(()=>{CheckDevice(device);var l=state.Links.FirstOrDefault(x=>x.LinkId==linkId);if(l is not null&&l.PairingId!=device)throw new BridgeException(404,"Link not found.");state.Links=state.Links.Select(x=>x.LinkId==linkId?x with{Active=false}:x).ToList();StopWhere(x=>x.Request.LinkId==linkId,"Stopped because this PC target was unlinked.");return new{unlinked=true};});

    public PcLink[] Links(string device){lock(gate){CheckDevice(device);return state.Links.Where(x=>x.PairingId==device&&x.Active).ToArray();}}

    public JobView Queue(string device,QueueRequest r)=>Mutate(()=>{

        CheckDevice(device);if(r.Deck is null||r.Deck.SkillIds is null||r.Deck.SkillIds.Count!=30||r.Deck.Revision<1||r.Deck.Name?.Length>1000)throw new BridgeException(400,"Invalid deck payload.");
        if(!string.IsNullOrWhiteSpace(r.ClientJourneyId)){
            var retry=state.Jobs.FirstOrDefault(x=>x.PairingId==device&&x.Request.ClientJourneyId==r.ClientJourneyId);
            if(retry is not null){
                if(retry.State==JobState.Cancelled)throw new BridgeException(409,"This request was cancelled.");
                if(JsonSerializer.Serialize(retry.Request,Wire.Json)!=JsonSerializer.Serialize(r,Wire.Json))throw new BridgeException(409,"Retry payload differs from its original revision.");
                return retry;
            }
        }

        var l=state.Links.SingleOrDefault(x=>x.Active&&x.LinkId==r.LinkId&&x.PairingId==device);

        if(l is null||l.DeckId!=r.Deck.DeckId||l.ProfileKey!=r.ProfileKey||l.TargetSlot!=r.TargetSlot||l.LinkGeneration!=r.LinkGeneration||l.ExpectedFingerprint!=r.ConfirmedFingerprint||r.ExpectedFingerprint!=l.ExpectedFingerprint)throw new BridgeException(409,"Link context changed. Refresh the target.");

        var journey=string.IsNullOrWhiteSpace(r.ClientJourneyId)?null:r.ClientJourneyId;
        if(journey is not null&&state.CancelledJourneys.Contains(device+":"+journey))throw new BridgeException(409,"This request was cancelled before delivery.");
        if(journey is not null&&!Guid.TryParse(journey,out _))throw new BridgeException(400,"Invalid sync journey identity.");
        var same=state.Jobs.FirstOrDefault(x=>x.PairingId==device&&(journey is not null&&x.Request.ClientJourneyId==journey||journey is null&&x.DeckId==r.Deck.DeckId&&x.Revision==r.Deck.Revision&&x.Request.LinkId==r.LinkId&&x.Request.LinkGeneration==r.LinkGeneration));

        if(same is not null){if(same.State==JobState.Cancelled)throw new BridgeException(409,"This request was cancelled. Confirm the target again to start a new request.");if(JsonSerializer.Serialize(same.Request,Wire.Json)!=JsonSerializer.Serialize(r,Wire.Json))throw new BridgeException(409,"Retry payload differs from its original revision.");return same;}
        if(state.Jobs.Any(x=>x.Request.ProfileKey==r.ProfileKey&&x.TargetSlot==r.TargetSlot&&x.State is JobState.Applying or JobState.AppliedAwaitingGameSave or JobState.RecoveryRequired))throw new BridgeException(409,"The existing PC change is applying or awaiting a verified save. Keep phone edits for a later update.");

        if(state.Jobs.Any(x=>x.PairingId==device&&x.DeckId==r.Deck.DeckId&&x.Revision>r.Deck.Revision))throw new BridgeException(409,"A newer revision has already arrived.");

        if (r.SupersedesJourneyId is not null) {
            if (!Guid.TryParse(r.SupersedesJourneyId, out _)) throw new BridgeException(400,"Invalid superseded journey identity.");
            var superseded=state.Jobs.FirstOrDefault(x=>x.PairingId==device&&x.Request.ClientJourneyId==r.SupersedesJourneyId);
            if (superseded is not null && (superseded.Request.ProfileKey!=r.ProfileKey || superseded.TargetSlot!=r.TargetSlot || superseded.DeckId!=r.Deck.DeckId))
                throw new BridgeException(409,"The sync amendment refers to an unknown journey.");
            if (superseded?.State is JobState.Applying or JobState.AppliedAwaitingGameSave or JobState.RecoveryRequired)
                throw new BridgeException(409,"The existing sync is already applying or requires recovery and cannot be amended.");
            if (superseded is not null&&!CanStop(superseded.State)) throw new BridgeException(409,"The existing sync cannot be amended in its current state.");
            if(superseded is null)state.CancelledJourneys.Add(device+":"+r.SupersedesJourneyId);
            else state.Jobs=state.Jobs.Select(x=>x.JobId==superseded.JobId?x with{State=JobState.Superseded,SupersededByJourneyId=journey,Reason="Replaced by your reviewed amendment.",UpdatedAt=Wire.Now}:x).ToList();
        }

        if(state.Jobs.Any(x=>x.PairingId==device&&x.DeckId==r.Deck.DeckId&&x.TargetSlot==r.TargetSlot&&x.State==JobState.RecoveryRequired))throw new BridgeException(409,"A previous sync has an uncertain save result. Review the current PC arsenal before sending a newer revision.");

        state.Jobs=state.Jobs.Select(x=>!InventoryStopped(x)&&x.PairingId==device&&x.DeckId==r.Deck.DeckId&&x.State is JobState.Queued or JobState.Blocked or JobState.WaitingForGame or JobState.WaitingForFrames or JobState.WaitingForGameReady?x with{State=JobState.Superseded,Reason="Replaced by a newer sync.",Phase="Replaced",PhaseMessage="A newer revision became the active sync.",SupersededByJourneyId=journey,UpdatedAt=Wire.Now}:x).ToList();

        var j=new JobView(Guid.NewGuid().ToString(),device,r.Deck.DeckId,r.Deck.Revision,r.TargetSlot,JobState.Queued,"Received by PC.",Wire.Now,Wire.Now,r,null,Phase:"ReceivedByPc",PhaseMessage:"The PC received this arsenal.");state.Jobs.Add(j);return j;

    });

    public JobView[] Jobs(string device){lock(gate){CheckDevice(device);return state.Jobs.Where(x=>x.PairingId==device).OrderByDescending(x=>x.CreatedAt).ToArray();}}

    public JobView WaitForGame(string device,string jobId)=>Mutate(()=>{
        CheckDevice(device);var index=state.Jobs.FindIndex(x=>x.JobId==jobId&&x.PairingId==device);
        if(index<0)throw new BridgeException(404,"Job not found.");var job=state.Jobs[index];
        if(job.State==JobState.WaitingForGameReady)return job;
        if(job.State!=JobState.WaitingForFrames)throw new BridgeException(409,"This sync is not waiting for game frames.");
        var link=state.Links.FirstOrDefault(x=>x.Active&&x.PairingId==device&&x.LinkId==job.Request.LinkId&&x.DeckId==job.Request.Deck.DeckId&&x.ProfileKey==job.Request.ProfileKey&&x.TargetSlot==job.TargetSlot&&x.LinkGeneration==job.Request.LinkGeneration&&x.ExpectedFingerprint==job.Request.ConfirmedFingerprint);
        if(link is null)throw new BridgeException(409,"Link context changed. Refresh the target.");
        state.Jobs[index]=job with{State=JobState.WaitingForGameReady,Reason="Waiting for Phantom Dust. This sync will continue automatically when the game is ready.",Phase="WaitingForGameReady",PhaseMessage="Waiting for Phantom Dust. This sync will continue automatically when the game is ready.",UpdatedAt=Wire.Now};return state.Jobs[index];
    });

    public JobView Retry(string device,string jobId)=>Mutate(()=>{
        CheckDevice(device);
        var index=state.Jobs.FindIndex(x=>x.JobId==jobId&&x.PairingId==device);
        if(index<0)throw new BridgeException(404,"Job not found.");
        var job=state.Jobs[index];
        if(job.State is not (JobState.WaitingForFrames or JobState.WaitingForGameReady))throw new BridgeException(409,"This sync is not waiting for game frames and cannot be retried.");
        var link=state.Links.FirstOrDefault(x=>x.Active&&x.PairingId==device&&x.LinkId==job.Request.LinkId&&x.DeckId==job.Request.Deck.DeckId&&x.ProfileKey==job.Request.ProfileKey&&x.TargetSlot==job.TargetSlot&&x.LinkGeneration==job.Request.LinkGeneration&&x.ExpectedFingerprint==job.Request.ConfirmedFingerprint);
        if(link is null)throw new BridgeException(409,"Link context changed. Refresh the target.");
        state.Jobs[index]=job with{State=JobState.Queued,Reason="Retry queued after the game window was restored.",Phase="ReceivedByPc",PhaseMessage="Retry queued. Phantom Dust must be visible and processing frames.",UpdatedAt=Wire.Now};
        return state.Jobs[index];
    });

    // Used only by the local tray UI so it can report completed work from every paired phone.

    // This method is not exposed by the HTTP API.

    public JobView[] HostJobs(){lock(gate)return state.Jobs.OrderByDescending(x=>x.CreatedAt).ToArray();}

    private JobView VerifyUnsafe(string device,string jobId,Snapshot snapshot) {

        CheckDevice(device);

        var index=state.Jobs.FindIndex(x=>x.JobId==jobId&&x.PairingId==device);

        if(index<0)throw new BridgeException(404,"Job not found.");

        var job=state.Jobs[index];

        if(job.State!=JobState.AppliedAwaitingGameSave)return job;

        var result=SaveVerification.Evaluate(job,snapshot);

        if(result.Verified)state.Jobs[index]=job with{State=JobState.Completed,Reason=result.Reason,Phase="Synced",PhaseMessage="Saved and verified in Phantom Dust.",UpdatedAt=Wire.Now};

        else state.Jobs[index]=job with{Reason=result.Reason,UpdatedAt=Wire.Now};

        return state.Jobs[index];

    }

    public JobView VerifySave(string device,string jobId,Snapshot snapshot)=>Mutate(()=>VerifyUnsafe(device,jobId,snapshot));

    public JobView ResolveRecovery(string device,string jobId,RecoveryResolutionRequest request,Snapshot snapshot)=>Mutate(()=>{
        CheckDevice(device);var index=state.Jobs.FindIndex(x=>x.JobId==jobId&&x.PairingId==device);
        if(index<0)throw new BridgeException(404,"Job not found.");var job=state.Jobs[index];
        if(job.State!=JobState.RecoveryRequired)throw new BridgeException(409,"This job no longer needs recovery review.");
        var target=snapshot.Arsenals.SingleOrDefault(x=>x.Slot==request.TargetSlot);
        if(!snapshot.ProfileVerified||snapshot.ProfileKey!=request.ProfileKey||job.Request.ProfileKey!=request.ProfileKey||job.TargetSlot!=request.TargetSlot||target?.Fingerprint!=request.ReviewedFingerprint)throw new BridgeException(409,"Reload the profile and review the current PC Arsenal again.");
        state.Jobs[index]=job with{State=JobState.Superseded,Reason="Recovery reviewed against the current PC Arsenal.",Phase="Replaced",PhaseMessage="The uncertain sync was reviewed.",UpdatedAt=Wire.Now};return state.Jobs[index];
    });

    public object Cancel(string device,string id)=>Mutate<object>(()=>{CheckDevice(device);if(!state.Jobs.Any(x=>x.JobId==id&&x.PairingId==device))throw new BridgeException(404,"Job not found.");StopWhere(x=>x.JobId==id,"Cancelled by you before the PC arsenal was updated.");var actual=state.Jobs.Single(x=>x.JobId==id);return new{cancelled=actual.State==JobState.Cancelled,state=actual.State,reason=actual.Reason,phase=actual.Phase,phaseMessage=actual.PhaseMessage,updatedAt=actual.UpdatedAt};});
    // A tombstone closes the lost-ack race: cancellation may arrive before a timed-out queue request.
    public object CancelJourney(string device,string journey)=>Mutate<object>(()=>{
        CheckDevice(device);if(!Guid.TryParse(journey,out _))throw new BridgeException(400,"Invalid journey identity.");
        var job=state.Jobs.FirstOrDefault(x=>x.PairingId==device&&x.Request.ClientJourneyId==journey);
        if(job is not null&&!CanStop(job.State)&&job.State!=JobState.Cancelled)return new{cancelled=false,state=job.State,reason=job.Reason};
        state.CancelledJourneys.Add(device+":"+journey);
        StopWhere(x=>x.PairingId==device&&x.Request.ClientJourneyId==journey,"Cancelled before the PC arsenal was updated.");
        return new{cancelled=true,state=JobState.Cancelled,reason="Cancellation confirmed by PC."};
    });

    private static bool InventoryStopped(JobView job)=>job.State==JobState.Blocked&&job.Plan?.Blockers.Any(x=>x.Code is "MissingSkills" or "InventoryInvalid" or "ConflictPcChanged" or "ProfileBindingChanged" or "LinkContextChanged")==true;

    public void Reconcile(Snapshot snapshot,Planner planner)=>Mutate(()=>{

        foreach(var awaiting in state.Jobs.Where(j=>j.State==JobState.AppliedAwaitingGameSave).ToArray())VerifyUnsafe(awaiting.PairingId,awaiting.JobId,snapshot);

        for(var i=0;i<state.Jobs.Count;i++) {var j=state.Jobs[i];
            // WaitingForGameReady is deliberately promoted only by an explicit, positive
            // readiness signal from the non-writing frame probe. Never infer readiness
            // from the timer tick or from a profile snapshot alone.
            if(j.State==JobState.WaitingForGameReady||InventoryStopped(j))continue;
            if(j.State is not (JobState.Queued or JobState.WaitingForGame or JobState.Blocked)||Paused(j.PairingId))continue;

            var plan=planner.Plan(new(j.Request.ProfileKey,j.TargetSlot,j.Request.ExpectedFingerprint,j.Request.Deck),snapshot);

            var bound=state.Links.FirstOrDefault(x=>x.Active&&x.LinkId==j.Request.LinkId);

            if(bound?.BoundDisplayName is not null&&snapshot.ProfileKey==bound.ProfileKey&&snapshot.GameProfileDisplayName is not null&&!string.Equals(bound.BoundDisplayName,snapshot.GameProfileDisplayName,StringComparison.Ordinal))

                plan=plan with{Blockers=plan.Blockers.Append(new PlanBlocker("ProfileBindingChanged","The linked game profile name changed. Unlink and bind the save slot again." )).ToArray(),CanApply=false};

            // This shipped host has no production writer. Never turn a valid plan into a success.

            var next=snapshot.GameRunning?JobState.Blocked:JobState.WaitingForGame;

            var reason=string.Join(" ",plan.Blockers.Select(x=>x.Message));var phase=next==JobState.WaitingForGame?"WaitingForGame":"NeedsAction";
            state.Jobs[i]=j with{State=next,Plan=plan,Reason=reason,Phase=phase,PhaseMessage=reason,UpdatedAt=j.State!=next||j.Reason!=reason?Wire.Now:j.UpdatedAt};

        }return true;

    });

    // Only explicitly armed local test hosts call this. Never roll back Applying in the

    // database after entering a writer: an uncertain result must survive as recovery.

    public void ExecuteOneTestJob(IPhantomDustProfileReader reader,Planner planner,Func<QueueRequest,JobState> apply){

        lock(gate){

            if(state.Jobs.Any(j=>j.State==JobState.RecoveryRequired))return;

            var snapshot=reader.Read();

            var index=state.Jobs.FindIndex(j=>(j.State is JobState.Queued or JobState.Blocked or JobState.WaitingForGame) && !InventoryStopped(j) && !Paused(j.PairingId) && j.Request.ProfileKey==snapshot.ProfileKey);

            if(index<0)return;

            var job=state.Jobs[index];

            var plan=planner.Plan(new(job.Request.ProfileKey,job.TargetSlot,job.Request.ExpectedFingerprint,job.Request.Deck),snapshot);

            if(!snapshot.ControlledTestSession||!plan.CanApply){state.Jobs[index]=job with{State=JobState.Blocked,Plan=plan,Reason=string.Join(" ",plan.Blockers.Select(b=>b.Message)),UpdatedAt=Wire.Now};Save();return;}

            var target=snapshot.Arsenals.FirstOrDefault(x=>x.Slot==job.TargetSlot);

            var expected=target is null?"":Planner.TargetFingerprint(target.Slot,job.Request.Deck.Name,target.CaseCapacity,job.Request.Deck.SkillIds);

            var evidence=target is null?null:new AppliedEvidence(snapshot.SessionKey??"",snapshot.ProfileKey,expected,target.CaseCapacity,job.Request.Deck.SkillIds,snapshot.Collection,snapshot.ProfileVerified,snapshot.ControlledTestSession,job.Request.Deck.Name);

            state.Jobs[index]=job with{State=JobState.Applying,Plan=plan,Evidence=evidence,Reason="Applying one disposable-profile test overwrite.",UpdatedAt=Wire.Now};

            Save(); // FULL synchronous commit before any process mutation.

            JobState outcome;

            try{outcome=apply(job.Request);if(outcome is not (JobState.AppliedAwaitingGameSave or JobState.Blocked or JobState.WaitingForFrames or JobState.RecoveryRequired))outcome=JobState.RecoveryRequired;}

            catch{outcome=JobState.RecoveryRequired;}

            state.Jobs[index]=state.Jobs[index] with{State=outcome,Reason=outcome==JobState.AppliedAwaitingGameSave?"Applied in game. Save and reload in Phantom Dust to verify persistence.":outcome==JobState.Blocked?"Test context changed; nothing applied. Refresh and re-arm locally.":"Interrupted or uncertain write. Inspect the recovery journal; do not retry.",UpdatedAt=Wire.Now};

            Save();

        }

    }

    private static bool SavedContentMatches(JobView job,Snapshot snapshot){var target=snapshot.Arsenals.FirstOrDefault(x=>x.Slot==job.TargetSlot);return target is not null&&target.Name==job.Request.Deck.Name&&target.CaseCapacity>=job.Request.Deck.CaseCapacity&&Planner.CardContentFingerprint(target.SkillIds)==Planner.CardContentFingerprint(job.Request.Deck.SkillIds);}

    private void ReconcileOrderOnlyRecoveryUnsafe(Snapshot snapshot){
        foreach(var job in state.Jobs.Where(j=>j.State==JobState.RecoveryRequired&&j.Reason?.Contains("Phantom Dust saved",StringComparison.OrdinalIgnoreCase)==true&&j.Reason.Contains("changed during",StringComparison.OrdinalIgnoreCase)).ToArray()){
            if(!snapshot.ProfileVerified||snapshot.ProfileKey!=job.Request.ProfileKey||!SavedContentMatches(job,snapshot))continue;
            var targetCase=job.Changes?.After.CaseCapacity??job.Evidence?.CaseCapacity;
            var targetForCase=snapshot.Arsenals.FirstOrDefault(x=>x.Slot==job.TargetSlot);
            if(targetCase is null||targetForCase?.CaseCapacity!=targetCase.Value)continue;
            var link=state.Links.FirstOrDefault(x=>x.Active&&x.PairingId==job.PairingId&&x.LinkId==job.Request.LinkId&&x.DeckId==job.Request.Deck.DeckId&&x.ProfileKey==job.Request.ProfileKey&&x.TargetSlot==job.TargetSlot&&x.LinkGeneration==job.Request.LinkGeneration&&x.ExpectedFingerprint==job.Request.ConfirmedFingerprint);
            if(link is null)continue;
            var index=state.Jobs.FindIndex(j=>j.JobId==job.JobId);var target=snapshot.Arsenals.Single(x=>x.Slot==job.TargetSlot);
            state.Jobs[index]=job with{State=JobState.Completed,Reason="Saved in Phantom Dust. Phantom Dust reordered the skill slots without changing the arsenal.",Phase="Synced",PhaseMessage="Saved and verified in Phantom Dust.",UpdatedAt=Wire.Now};
            if(link is not null)state.Links=state.Links.Select(x=>x.LinkId==link.LinkId?x with{ExpectedFingerprint=target.Fingerprint??link.ExpectedFingerprint,BaselineContentFingerprint=Planner.CardContentFingerprint(target.SkillIds)}:x).ToList();
        }
    }

    private void Progress(string jobId,string phase,string message){lock(gate){var index=state.Jobs.FindIndex(j=>j.JobId==jobId);if(index<0||state.Jobs[index].State!=JobState.Applying)return;state.Jobs[index]=state.Jobs[index] with{Phase=phase,PhaseMessage=message,Reason=message,UpdatedAt=Wire.Now};Save();}}

    private static bool IsFocused(Snapshot snapshot)=>snapshot.GameForeground is not false;

    private static bool ProbeReady(Func<bool>? readinessProbe){
        if(readinessProbe is null)return false;
        try{return readinessProbe();}catch{return false;}
    }

    // Claim and persist the job under the store lock, then release it while the
    // game performs its bounded write/save. Status and queue HTTP calls therefore
    // remain responsive, while writerBusy still guarantees one game writer.
    public void ExecuteOneNormalJob(IPhantomDustProfileReader reader,Planner planner,Func<QueueRequest,Snapshot,WriteResult> apply)=>ExecuteOneNormalJob(reader,planner,(request,snapshot,_)=>apply(request,snapshot),null);
    public void ExecuteOneNormalJob(IPhantomDustProfileReader reader,Planner planner,Func<QueueRequest,Snapshot,Action<string,string>,WriteResult> apply,Func<bool>? readinessProbe=null){
        JobView? claimed=null;Snapshot? claimedSnapshot=null;
        lock(gate){
            if(writerBusy)return;
            var snapshot=reader.Read();ReconcileOrderOnlyRecoveryUnsafe(snapshot);foreach(var awaiting in state.Jobs.Where(j=>j.State==JobState.AppliedAwaitingGameSave).ToArray())VerifyUnsafe(awaiting.PairingId,awaiting.JobId,snapshot);
            if(snapshot.ControlledTestSession)return; // Normal journeys never enter a temporary test session.
            var waiting=state.Jobs.FirstOrDefault(j=>(j.State is JobState.WaitingForFrames or JobState.WaitingForGameReady)&&!Paused(j.PairingId)&&j.Request.ProfileKey==snapshot.ProfileKey);
            if(waiting is not null){
                var waitingFocused=IsFocused(snapshot);var waitingReady=waitingFocused&&ProbeReady(readinessProbe);
                if(readinessProbe is null||!waitingFocused||!waitingReady){
                    var reason=!waitingFocused?"Focus Phantom Dust on your monitor. Sync will start automatically.":"Keep Phantom Dust focused; waiting for the game to respond.";
                    var waitingIndex=state.Jobs.FindIndex(j=>j.JobId==waiting.JobId);state.Jobs[waitingIndex]=waiting with{State=waiting.State==JobState.WaitingForFrames&&!waitingFocused?JobState.WaitingForGameReady:waiting.State,Reason=reason,Phase=waiting.Phase=="WaitingForGame"?"WaitingForGame":"WaitingForGameReady",PhaseMessage=reason,UpdatedAt=Wire.Now};Save();return;
                }
                var resumedIndex=state.Jobs.FindIndex(j=>j.JobId==waiting.JobId);state.Jobs[resumedIndex]=waiting with{State=JobState.Queued,Reason="Phantom Dust is ready; retry queued.",Phase="ReceivedByPc",PhaseMessage="Phantom Dust is ready. Sync will resume.",UpdatedAt=Wire.Now};Save();
            }
            var index=state.Jobs.FindIndex(j=>(j.State is JobState.Queued or JobState.Blocked or JobState.WaitingForGame)&&!InventoryStopped(j)&&!Paused(j.PairingId)&&j.Request.ProfileKey==snapshot.ProfileKey&&!state.Jobs.Any(r=>r.State is JobState.RecoveryRequired or JobState.AppliedAwaitingGameSave&&r.Request.ProfileKey==j.Request.ProfileKey&&r.TargetSlot==j.TargetSlot));
            if(index<0){Save();return;}var job=state.Jobs[index];var plan=planner.Plan(new(job.Request.ProfileKey,job.TargetSlot,job.Request.ExpectedFingerprint,job.Request.Deck),snapshot);
            var bound=state.Links.FirstOrDefault(x=>x.Active&&x.LinkId==job.Request.LinkId&&x.PairingId==job.PairingId&&x.ProfileKey==job.Request.ProfileKey&&x.TargetSlot==job.TargetSlot&&x.LinkGeneration==job.Request.LinkGeneration);
            if(bound is null){state.Jobs[index]=job with{State=JobState.Blocked,Plan=plan with{CanApply=false,Blockers=plan.Blockers.Append(new PlanBlocker("LinkContextChanged","The PC link changed. Review the destination before syncing.")).ToArray()},Reason="Link context changed. Refresh and confirm the target.",Phase="NeedsAction",PhaseMessage="Refresh and confirm the linked PC arsenal.",UpdatedAt=Wire.Now};Save();return;}
            if(bound.BoundDisplayName is not null&&snapshot.GameProfileDisplayName is not null&&!string.Equals(bound.BoundDisplayName,snapshot.GameProfileDisplayName,StringComparison.Ordinal)){state.Jobs[index]=job with{State=JobState.Blocked,Plan=plan with{CanApply=false,Blockers=plan.Blockers.Append(new PlanBlocker("ProfileBindingChanged","The linked game profile name changed. Refresh and bind the save slot again.")).ToArray()},Reason="The linked game profile changed. Refresh and bind the save slot again.",Phase="NeedsAction",PhaseMessage="Load and confirm the linked Phantom Dust profile.",UpdatedAt=Wire.Now};Save();return;}
            if(!plan.CanApply){var reason=string.Join(" ",plan.Blockers.Select(b=>b.Message));state.Jobs[index]=job with{State=snapshot.GameRunning?JobState.Blocked:JobState.WaitingForGame,Plan=plan,Reason=reason,Phase=snapshot.GameRunning?"NeedsAction":"WaitingForGame",PhaseMessage=reason,UpdatedAt=Wire.Now};Save();return;}
            var claimFocused=IsFocused(snapshot);var claimReady=claimFocused&&(readinessProbe is null||ProbeReady(readinessProbe));
            if(!claimFocused||!claimReady){var reason=!claimFocused?"Focus Phantom Dust on your monitor. Sync will start automatically.":"Keep Phantom Dust focused; waiting for the game to respond.";state.Jobs[index]=job with{State=JobState.WaitingForGameReady,Plan=plan,Reason=reason,Phase="WaitingForGame",PhaseMessage=reason,UpdatedAt=Wire.Now};Save();return;}
            var target=snapshot.Arsenals.FirstOrDefault(x=>x.Slot==job.TargetSlot);var expected=target is null?"":Planner.TargetFingerprint(target.Slot,job.Request.Deck.Name,target.CaseCapacity,job.Request.Deck.SkillIds);
            var evidence=target is null?null:new AppliedEvidence(snapshot.SessionKey??"",snapshot.ProfileKey,expected,target.CaseCapacity,job.Request.Deck.SkillIds,snapshot.Collection,snapshot.ProfileVerified,false,job.Request.Deck.Name);
            claimed=job with{State=JobState.Applying,Plan=plan,Evidence=evidence,Changes=target is null?null:new ArsenalChange(new(target.Name,target.SkillIds.ToArray(),target.CaseCapacity),new(job.Request.Deck.Name,job.Request.Deck.SkillIds.ToArray(),target.CaseCapacity)),Reason="Updating the PC arsenal.",Phase="UpdatingPcArsenal",PhaseMessage="Updating the PC arsenal.",UpdatedAt=Wire.Now};state.Jobs[index]=claimed;claimedSnapshot=snapshot;writerBusy=true;Save();
        }
        WriteResult outcome;Snapshot? observed=null;
        try{outcome=apply(claimed!.Request,claimedSnapshot!,(phase,message)=>Progress(claimed.JobId,phase,message));if(outcome.State is JobState.Completed or JobState.AppliedAwaitingGameSave)observed=reader.Read();}
        catch(Exception e){outcome=new(JobState.RecoveryRequired,Reason:"Write outcome is uncertain: "+e.Message);}
        lock(gate){
            writerBusy=false;var index=state.Jobs.FindIndex(j=>j.JobId==claimed!.JobId);if(index<0)return;var current=state.Jobs[index];
            var phase=outcome.State switch{JobState.Completed=>"Synced",JobState.AppliedAwaitingGameSave=>"VerifyingSave",JobState.Blocked=>"NeedsAction",JobState.WaitingForFrames=>"WaitingForFrames",JobState.RecoveryRequired=>"ReviewRequired",_=>current.Phase};
            var message=outcome.State switch{JobState.Completed=>"Saved and verified in Phantom Dust.",JobState.AppliedAwaitingGameSave=>"Waiting for Phantom Dust to confirm the save.",JobState.Blocked=>outcome.Reason??"Fix the PC issue and syncing will resume.",JobState.WaitingForFrames=>outcome.Reason??"Phantom Dust is paused or minimized. Restore the game window, then retry. No PC changes were kept.",JobState.RecoveryRequired=>outcome.Reason??"Review the current PC arsenal before syncing again.",_=>outcome.Reason??current.PhaseMessage};
            state.Jobs[index]=current with{State=outcome.State,Changes=outcome.Changes??current.Changes,Reason=outcome.Reason??message,Phase=phase,PhaseMessage=message,UpdatedAt=Wire.Now};
            if(outcome.State is JobState.AppliedAwaitingGameSave or JobState.Completed&&outcome.Changes is {After: not null}){
                var link=state.Links.FirstOrDefault(x=>x.Active&&x.LinkId==claimed.Request.LinkId);var target=observed is not null&&SavedContentMatches(claimed,observed)?observed.Arsenals.FirstOrDefault(x=>x.Slot==claimed.TargetSlot):null;
                if(link is not null)state.Links=state.Links.Select(x=>x.LinkId==link.LinkId?x with{ExpectedFingerprint=target?.Fingerprint??Planner.TargetFingerprint(claimed.TargetSlot,outcome.Changes.After.Name,outcome.Changes.After.CaseCapacity,outcome.Changes.After.SkillIds),BaselineContentFingerprint=Planner.CardContentFingerprint(target?.SkillIds??outcome.Changes.After.SkillIds)}:x).ToList();
            }
            Save();
        }
    }

    public void Dispose()=>db.Dispose();

}



public sealed record SaveVerificationResult(bool Verified,string Reason);



public static class SaveVerification {

    private static readonly string[] RetailSkillIds=Enumerable.Range(1,374).Select(n=>n.ToString("D3")).ToArray();



    public static SaveVerificationResult Evaluate(JobView job,Snapshot snapshot) {

        var e=job.Evidence;

        if(e is null)return Fail("Save verification unavailable: this job has no pre-write evidence.");

        if(!e.ProfileVerified||e.ControlledTestSession)return Fail("Save verification unavailable: the original write did not use a verified PC profile.");

        if(job.Changes is not null&&(!e.SessionKey.StartsWith("process-v1:")||snapshot.SessionKey?.StartsWith("process-v1:")!=true))return Fail("This older transfer has no game-restart record. Its contents are synced, but saving has not been confirmed.");

        if(!snapshot.ProfileVerified||snapshot.ControlledTestSession)return Fail("Save verification failed: the current PC profile is not verified.");

        if(string.IsNullOrWhiteSpace(e.ProfileKey)||e.ProfileKey!=job.Request.ProfileKey||snapshot.ProfileKey!=e.ProfileKey)return Fail("Save verification failed: a different PC profile is loaded.");

        var target=snapshot.Arsenals.FirstOrDefault(x=>x.Slot==job.TargetSlot);

        if(target is null)return Fail("Save verification failed: the target arsenal was not found.");

        if(target.CaseCapacity!=e.CaseCapacity||e.SkillIds.Count!=30||Planner.CardContentFingerprint(target.SkillIds)!=Planner.CardContentFingerprint(e.SkillIds)||(job.Changes is null&&target.Fingerprint!=e.TargetFingerprint)||e.Name is not null&&target.Name!=e.Name)return Fail("Save verification failed: the saved arsenal does not contain the same skills as the phone revision.");

        // New Bridges observe the opaque Windows Gaming Services profile payload
        // changing after Phantom Dust's own prepare-and-save calls return. That is
        // stronger persistence evidence than RAM readback and does not require a
        // full process restart merely to advance the queue state.
        if(job.Changes is not null&&job.Reason=="Phantom Dust committed the profile save. Reload the profile to verify persistence.")return new(true,"Saved in Phantom Dust.");

        if(string.IsNullOrWhiteSpace(e.SessionKey)||string.IsNullOrWhiteSpace(snapshot.SessionKey)||snapshot.SessionKey==e.SessionKey)return Fail("Save verification failed: the game has not been closed and reopened.");

        // Normal writes deliberately do not claim verified inventory ownership.
        // Exact target readback after a new game session is sufficient for the
        // write completion state; collection accounting remains an optional
        // stronger check when both snapshots provide it.
        if(job.Changes is not null&&(e.Collection?.Status!=CollectionStatus.Verified||snapshot.Collection?.Status!=CollectionStatus.Verified))return new(true,"Saved in Phantom Dust.");

        if(e.Collection is null)return new(true,"Save verified on PC.");

        if(e.Collection is not {Status:CollectionStatus.Verified} before||!ValidCollection(before,null))return Fail("Save verification unavailable: the original collection was not fully verified.");

        if(snapshot.Collection is not {Status:CollectionStatus.Verified} after||!ValidCollection(after,snapshot.Arsenals))return Fail("Save verification failed: the PC collection is incomplete or inconsistent.");

        if(!SameTotals(before,after))return Fail("Save verification failed: collection totals changed unexpectedly.");

        return new(true,"Save verified on PC.");

    }



    private static SaveVerificationResult Fail(string reason)=>new(false,reason);

    private static bool SameTotals(SnapshotCollection a,SnapshotCollection b)=>RetailSkillIds.All(id=>a.Skills[id].Total==b.Skills[id].Total);

    private static bool ValidCollection(SnapshotCollection c,IReadOnlyList<SnapshotDeck>? snapshot) {

        if(c.Skills.Keys.OrderBy(x=>x).SequenceEqual(RetailSkillIds) is false)return false;

        foreach(var (id,entry) in c.Skills) {

            if(entry.Total<0||entry.Free<0||entry.Assigned<0||entry.Free+entry.Assigned!=entry.Total||entry.Allocations.Any(a=>a.Copies<=0)||entry.Allocations.Select(a=>a.ArsenalSlot).Distinct().Count()!=entry.Allocations.Count||entry.Assigned!=entry.Allocations.Sum(a=>a.Copies))return false;

            if(snapshot is null)continue;

            var actual=snapshot.Select(d=>new SkillAllocation(d.Slot,d.SkillIds.Count(skill=>skill==id))).Where(a=>a.Copies>0).OrderBy(a=>a.ArsenalSlot).ToArray();

            var claimed=entry.Allocations.OrderBy(a=>a.ArsenalSlot).ToArray();

            if(!actual.SequenceEqual(claimed))return false;

        }

        return snapshot is null||snapshot.SelectMany(d=>d.SkillIds).All(id=>id=="000"||c.Skills.ContainsKey(id));

    }

}
