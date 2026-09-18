using PhantomDust.PcBridge.Core;
using Xunit;

namespace PhantomDust.PcBridge.Tests;

public sealed class VerifiedCollectionPlannerTests {
    private static readonly Planner Planner = new([
        new("000", "Aura", null, "aura"),
        new("001", "Wave", "psycho", "attack")
    ]);

    private static DeckSpec Deck(int copies = 3) => new(
        Guid.NewGuid().ToString(), 1, "Phone", Enumerable.Repeat("001", copies)
            .Concat(Enumerable.Repeat("000", 30 - copies)).ToArray(), 1, "retail_2017_full");

    private static Snapshot Snapshot(CollectionStatus status, int free) {
        var target = Enumerable.Repeat("001", 1).Concat(Enumerable.Repeat("000", 29)).ToArray();
        var entry = new CollectionEntry(1 + free, free, 1,
            [new SkillAllocation(1, 1)]);
        return new("profile", "snapshot", Wire.Now, true, true, false, true, true,
            [new SnapshotDeck(1, "PC", 1, target, "target")],
            new Dictionary<string, int>(), Collection: new SnapshotCollection(status,
                new Dictionary<string, CollectionEntry> { ["001"] = entry }));
    }

    [Fact]
    public void VerifiedCollectionAddsTargetCopiesAndReportsShortage() {
        var plan = Planner.Plan(new("profile", 1, "target", Deck(), false), Snapshot(CollectionStatus.Verified, 0));
        Assert.False(plan.CanApply);
        Assert.Equal(new MissingSkill("001", 3, 1, 2), Assert.Single(plan.MissingSkills));
    }

    [Fact]
    public void ObservedCollectionNeverBecomesAWriteGate() {
        var plan = Planner.Plan(new("profile", 1, "target", Deck(1), false), Snapshot(CollectionStatus.Observed, 3));
        Assert.True(plan.CanBuild);
        Assert.False(plan.CanApply);
        Assert.Empty(plan.MissingSkills);
        Assert.Contains(plan.Blockers, x => x.Code == "InventoryUnverified");
    }

    [Fact] public void MissingCollectionCannotBypassVerification() {
        var plan=Planner.Plan(new("profile",1,"target",Deck(1)),Snapshot(CollectionStatus.Verified,3) with{Collection=null,InventoryVerified=false});
        Assert.False(plan.CanApply);Assert.Empty(plan.MissingSkills);Assert.Contains(plan.Blockers,x=>x.Code=="InventoryUnverified");
    }
    [Fact] public void ObservedCollectionOverridesLegacyVerifiedFlag() {
        var plan=Planner.Plan(new("profile",1,"target",Deck(1)),Snapshot(CollectionStatus.Observed,3) with{InventoryVerified=true});
        Assert.False(plan.CanApply);Assert.Empty(plan.MissingSkills);
    }
    [Fact] public void InconsistentAllocationsFailClosed() {
        var snapshot=Snapshot(CollectionStatus.Verified,3);
        snapshot=snapshot with{Collection=new(CollectionStatus.Verified,new Dictionary<string,CollectionEntry>{{"001",new(4,3,1,[new(2,1)])}})};
        var plan=Planner.Plan(new("profile",1,"target",Deck(1)),snapshot);
        Assert.False(plan.CanApply);Assert.Contains(plan.Blockers,x=>x.Code=="InventoryInvalid");Assert.Empty(plan.MissingSkills);
    }
    [Fact] public void SuccessfulPlanRetainsCountCheckTime() {
        var snapshot=Snapshot(CollectionStatus.Verified,3);var plan=Planner.Plan(new("profile",1,"target",Deck()),snapshot);
        Assert.True(plan.CanApply);Assert.Equal(snapshot.CapturedAt,plan.CheckedAt);
    }
    private sealed class Reader(Snapshot value):IPhantomDustProfileReader { public Snapshot Read()=>value; }
    [Fact] public void ShortageRequiresNewAttemptAndHistorySurvivesRefresh() {
        using var store=new BridgeStore(Path.Combine(Path.GetTempPath(),"pd-counts-tests",Guid.NewGuid().ToString(),"bridge.db"));
        var pairing=store.BeginPairing("https://localhost","cert");var phone=store.Pair(new(pairing.BootstrapSecret,"phone"));
        var deck=Deck();var poor=Snapshot(CollectionStatus.Verified,0);
        var link=store.Link(phone.PairingId,new(Guid.NewGuid().ToString(),deck.DeckId,"profile",1,1,"target",true),poor);
        var request=new QueueRequest("profile",1,"target",deck,"target",link.LinkId,1,Guid.NewGuid().ToString());
        var original=store.Queue(phone.PairingId,request);var writes=0;
        store.ExecuteOneNormalJob(new Reader(poor),Planner,(_,_)=>{writes++;return new(JobState.Completed);});
        Assert.Equal(0,writes);var blocked=Assert.Single(store.Jobs(phone.PairingId));Assert.Equal(2,Assert.Single(blocked.Plan!.MissingSkills).Missing);
        var rich=Snapshot(CollectionStatus.Verified,3);
        store.Reconcile(rich,Planner);store.ExecuteOneNormalJob(new Reader(rich),Planner,(_,_)=>{writes++;return new(JobState.Completed);});
        Assert.Equal(0,writes);Assert.Equal(blocked,Assert.Single(store.Jobs(phone.PairingId)));
        store.Queue(phone.PairingId,request with{ClientJourneyId=Guid.NewGuid().ToString()});
        store.ExecuteOneNormalJob(new Reader(rich),Planner,(_,_)=>{writes++;return new(JobState.Completed);});
        Assert.Equal(1,writes);var history=store.Jobs(phone.PairingId).Single(x=>x.JobId==original.JobId);
        Assert.Equal(JobState.Blocked,history.State);Assert.Equal(2,Assert.Single(history.Plan!.MissingSkills).Missing);
    }
}
