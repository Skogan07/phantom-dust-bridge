using System.Security.Cryptography;
using System.Text;
namespace PhantomDust.PcBridge.Core;
public sealed record CatalogueSkill(string Id, string Name, string? School, string Category, int MaxCopiesStandard = 3);
public sealed class Planner(IEnumerable<CatalogueSkill> catalogue) {
    private readonly Dictionary<string,CatalogueSkill> skills = catalogue.ToDictionary(x=>x.Id);
    public PlanResult Plan(PlanRequest request, Snapshot snapshot) {
        var blocks = new List<PlanBlocker>();
        void Block(string code,string message) => blocks.Add(new(code,message));
        var d=request.Deck;
        if(d is null || d.SkillIds is null) throw new BridgeException(400,"Deck and skillIds are required.");
        if(!ArsenalNamePolicy.IsValid(d.Name)) Block("InvalidName",ArsenalNamePolicy.Message);
        if(!Guid.TryParse(d.DeckId,out _) || d.Revision < 1) Block("InvalidDeckIdentity","Invalid deck identity or revision.");
        if(d.SkillIds.Count!=30) Block("SlotCount","Exactly 30 ordered cards are required.");
        if(d.RulesetId!="retail_2017_full") Block("UnsupportedRuleset","Only the full retail 2017 ruleset is supported.");
        if(d.CaseCapacity is <1 or >3) Block("InvalidCase","Case capacity must be 1, 2, or 3 schools.");
        if(d.SkillIds.Any(id=>id is null || !skills.ContainsKey(id))) Block("UnknownSkill","Unknown or modded skill IDs cannot be synchronized.");
        var wanted=d.SkillIds.Where(x=>x is not null && x!="000").GroupBy(x=>x).ToDictionary(x=>x.Key,x=>x.Count());
        if(wanted.Any(x=>x.Value>3)) Block("CopyLimit","Numbered skills are limited to three copies.");
        if(d.SkillIds.Where(x=>x is not null && x!="000" && skills.ContainsKey(x)).Select(x=>skills[x].School).Where(x=>x is not null).Distinct().Count()>d.CaseCapacity)
            Block("SchoolLimit","The deck uses more schools than its case allows.");
        if(!snapshot.GameRunning) Block("WaitingForGame","Waiting for Phantom Dust.");
        if(!(snapshot.ProfileVerified || snapshot.ControlledTestSession) || string.IsNullOrWhiteSpace(snapshot.ProfileKey)) Block("ProfileUnverified","Cannot verify the loaded Phantom Dust profile.");
        if(request.ProfileKey!=snapshot.ProfileKey) Block("ProfileMismatch","Different Phantom Dust profile detected.");
        var target=snapshot.Arsenals.SingleOrDefault(x=>x.Slot==request.TargetSlot);
        if(target is null) Block("NoTarget","Choose an existing compatible PC arsenal.");
        else {
            if(request.RequireEmptyTarget && target.SkillIds.Any(x=>x!="000")) Block("EmptyCaseRequired","Choose an empty matching PC case (Aura only) before the first sync.");
            if(target.CaseCapacity<d.CaseCapacity || target.CaseCapacity is <1 or >3) Block("CaseMismatch",$"This deck needs an existing case that supports at least {d.CaseCapacity} schools.");
            if(target.SkillIds.Count!=30 || target.SkillIds.Any(x=>!skills.ContainsKey(x)) || string.IsNullOrEmpty(target.Fingerprint)) Block("TargetUnverified","The target arsenal cannot be decoded safely.");
            if(request.ExpectedFingerprint is not null && request.ExpectedFingerprint!=target.Fingerprint) Block("ConflictPcChanged","PC version changed. Review differences before confirming replacement.");
        }
        var missing=new List<MissingSkill>(); var remaining=new Dictionary<string,int>();
        // Production GameReader deliberately reports Observed ownership until a
        // separately authorized verification proves that the counters are totals.
        // Only a Verified collection may gate a real write. The disposable test
        // session is the sole explicit exception and carries its own grant.
        var verifiedCollection=snapshot.Collection is {Status:CollectionStatus.Verified};
        // Legacy protocol fixtures may carry the older boolean without a
        // collection object. Keep those explicit snapshots compatible; a live
        // production snapshot always includes Observed collection evidence and
        // therefore still fails closed until it is promoted to Verified.
        var trustedInventory=verifiedCollection || (snapshot.Collection is null && snapshot.InventoryVerified);
        if((snapshot.ProfileVerified || snapshot.ControlledTestSession) && request.ProfileKey==snapshot.ProfileKey && !trustedInventory)
            Block("InventoryUnverified","PC skill quantities could not be verified. Update the Bridge and reopen the linked profile.");
        if(trustedInventory && target is not null && (snapshot.ProfileVerified || snapshot.ControlledTestSession) && request.ProfileKey==snapshot.ProfileKey) {
            var freeSource=verifiedCollection
                ? snapshot.Collection!.Skills.ToDictionary(x=>x.Key,x=>x.Value.Free)
                : snapshot.FreeInventory;
            var collectionValid=!verifiedCollection || ValidCollection(snapshot.Collection!,snapshot.Arsenals,skills.Keys);
            if(!collectionValid || freeSource.Count!=skills.Count-1 || freeSource.Any(x=>!skills.ContainsKey(x.Key)||x.Key=="000"||x.Value is <0 or >255) || wanted.Keys.Any(x=>!freeSource.ContainsKey(x))) Block("InventoryInvalid","The inventory snapshot is incomplete or contains invalid quantities.");
            else {
                var old=target.SkillIds.Where(x=>x!="000").GroupBy(x=>x).ToDictionary(x=>x.Key,x=>x.Count());
                foreach(var id in freeSource.Keys.Union(old.Keys).Union(wanted.Keys)) {
                    var available=freeSource.GetValueOrDefault(id)+old.GetValueOrDefault(id); var need=wanted.GetValueOrDefault(id); remaining[id]=available-need;
                    if(need>available) missing.Add(new(id,need,available,need-available));
                    if(remaining[id]>255) Block("InventoryOverflow","Replacement would exceed an inventory storage bound.");
                }
                if(missing.Count>0) Block("MissingSkills","You do not currently have enough skills on PC.");
            }
        }
        // Inventory guidance is advisory for deck building; only CanApply is a
        // release gate. Other structural blockers still make the plan invalid.
        var canBuild=blocks.All(x=>x.Code is "InventoryUnverified" or "WritesUnavailable");
        if(!snapshot.WriteSupported) Block("WritesUnavailable","The PC bridge cannot write this game session.");
        return new(canBuild,canBuild && trustedInventory && missing.Count==0 && blocks.Count==0,missing.OrderBy(x=>x.SkillId).ToArray(),blocks,snapshot.Fingerprint,target?.Fingerprint,remaining,snapshot.CapturedAt);
    }
    private static bool ValidCollection(SnapshotCollection collection,IReadOnlyList<SnapshotDeck> arsenals,IEnumerable<string> catalogueIds){
        var ids=catalogueIds.Where(x=>x!="000").ToHashSet(StringComparer.Ordinal);
        if(arsenals.Select(x=>x.Slot).Distinct().Count()!=arsenals.Count||arsenals.Any(x=>x.Slot is <1 or >16||x.SkillIds.Count!=30))return false;
        if(collection.Skills.Count!=ids.Count||collection.Skills.Keys.Any(x=>!ids.Contains(x)))return false;
        foreach(var id in ids){if(!collection.Skills.TryGetValue(id,out var entry)||entry.Total is <0 or >255||entry.Free is <0 or >255||entry.Assigned is <0 or >255||entry.Free+entry.Assigned!=entry.Total)return false;
            var actual=arsenals.Select(x=>new SkillAllocation(x.Slot,x.SkillIds.Count(y=>y==id))).Where(x=>x.Copies>0).OrderBy(x=>x.ArsenalSlot).ToArray();
            if(entry.Assigned!=actual.Sum(x=>x.Copies)||!entry.Allocations.OrderBy(x=>x.ArsenalSlot).SequenceEqual(actual))return false;}
        return true;
    }
    public static string Hash(string text)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    // v2 baseline identity ignores slot order but retains every Aura (000)
    // entry in the multiset. The version marker prevents legacy ordered hashes
    // from being reinterpreted during migration.
    public static string CardContentFingerprint(IEnumerable<string> ids)=>"v2:"+Hash("v2\n"+string.Join(',',ids.OrderBy(x=>x,StringComparer.Ordinal)));
    public static string TargetFingerprint(int slot,string name,int capacity,IEnumerable<string> ids)=>Hash($"{slot}\n{name}\n{capacity}\n{string.Join(',',ids)}");
}
public sealed class DisabledProfileWriter : IPhantomDustProfileWriter { public bool WriteSupported=>false; }

