using PhantomDust.PcBridge.Core;
using Xunit;

namespace PhantomDust.PcBridge.Tests;

public sealed class StoreWatchTests {
    private static string Temp()=>Path.Combine(Path.GetTempPath(),"pd-store-watch-tests",Guid.NewGuid().ToString(),"bridge.db");
    private static PairResponse Pair(BridgeStore store,string name="Phone",string? identity=null){var payload=store.BeginPairing("https://localhost","cert");return store.Pair(new(payload.BootstrapSecret,name,identity));}
    private static SkillStoreResult Open(string profile="profile-a")=>new(profile,SkillStoreState.SkillStoreOpen,1234,"roll-a",[new("017",50,"Power"),new("003",150,"Psycho Knife")]);

    [Fact] public void UnknownPriceAvailabilityStillNotifiesWithoutLearningAPrice(){
        using var store=new BridgeStore(Temp());var phone=Pair(store);store.ReplaceStoreWatch(phone.PairingId,new("profile-a",["222"]));
        store.RecordStoreObservation(new("profile-a",SkillStoreState.SkillStoreOpen,1234,"unknown",[new("222",null,"New Skill")]));
        var notice=Assert.Single(store.ClaimNotices());Assert.Contains("New Skill",notice.Message);
        var result=store.StoreWatch(phone.PairingId,"profile-a");Assert.Equal("222",Assert.Single(result.Matches!).SkillId);Assert.Null(Assert.Single(result.Matches!).Price);Assert.DoesNotContain(result.KnownPrices!,x=>x.SkillId=="222");
    }

    [Fact] public void FullWatchSetIsProfileScopedAndSurvivesRestart(){
        var path=Temp();PairResponse phone;
        using(var first=new BridgeStore(path)){
            phone=Pair(first);var result=first.ReplaceStoreWatch(phone.PairingId,new("profile-a",["017","003"]));
            Assert.Equal(SkillStoreState.Unsupported,result.State);
        }
        using var restarted=new BridgeStore(path);
        restarted.RecordStoreObservation(Open());
        Assert.Equal(["003","017"],restarted.StoreWatch(phone.PairingId,"profile-a").Matches!.Select(x=>x.SkillId).ToArray());
        Assert.Equal(SkillStoreState.Unsupported,restarted.StoreWatch(phone.PairingId,"profile-b").State);
    }

    [Fact] public void StorePricesAreGlobalAndFirstDiscoveryIsPermanent(){
        var path=Temp();PairResponse phone;
        using(var first=new BridgeStore(path)){
            phone=Pair(first);first.ReplaceStoreWatch(phone.PairingId,new("profile-a",[]));
            first.RecordStoreObservation(new("profile-a",SkillStoreState.SkillStoreOpen,1234,"roll-a",[new("222",75,"New Skill")]));
            var known=first.StoreWatch(phone.PairingId,"profile-a").KnownPrices!;
            Assert.Equal(75,known.Single(x=>x.SkillId=="222").Price);
            Assert.Equal(150,known.Single(x=>x.SkillId=="003").Price);
            first.RecordStoreObservation(new("profile-a",SkillStoreState.SkillStoreOpen,2345,"roll-b",[new("222",999,"New Skill")]));
            Assert.Equal(75,first.StoreWatch(phone.PairingId,"profile-a").KnownPrices!.Single(x=>x.SkillId=="222").Price);
        }
        using var restarted=new BridgeStore(path);
        Assert.Equal(75,restarted.StoreWatch(phone.PairingId,"profile-a").KnownPrices!.Single(x=>x.SkillId=="222").Price);
    }

    [Fact] public void InvalidAuraUnknownAndDuplicateIdsFailClosed(){
        using var store=new BridgeStore(Temp());var phone=Pair(store);
        Assert.Throws<BridgeException>(()=>store.ReplaceStoreWatch(phone.PairingId,new("profile",["000"])));
        Assert.Throws<BridgeException>(()=>store.ReplaceStoreWatch(phone.PairingId,new("profile",["375"])));
        Assert.Throws<BridgeException>(()=>store.ReplaceStoreWatch(phone.PairingId,new("profile",["017","017"])));
    }

    [Fact] public void OpenStoreFiltersPerProfileAndNotifiesOnlyOncePerConfirmedSession(){
        using var store=new BridgeStore(Temp());var first=Pair(store,"One");var second=Pair(store,"Two");
        store.ReplaceStoreWatch(first.PairingId,new("profile-a",["017"]));
        store.ReplaceStoreWatch(second.PairingId,new("profile-b",["003"]));
        store.RecordStoreObservation(Open());
        var available=store.StoreWatch(first.PairingId,"profile-a");
        Assert.Equal(SkillStoreState.SkillStoreOpen,available.State);Assert.Equal("017",Assert.Single(available.Matches!).SkillId);Assert.Equal(50,Assert.Single(available.Matches!).Price);
        Assert.Equal(SkillStoreState.ProfileMismatch,store.StoreWatch(second.PairingId,"profile-b").State);
        var notice=Assert.Single(store.ClaimNotices());Assert.Contains("Power",notice.Message);Assert.DoesNotContain("credits",notice.Message);
        store.RecordStoreObservation(Open() with{ScannedAt=9999});Assert.Empty(store.ClaimNotices());
        store.RecordStoreObservation(new("profile-a",SkillStoreState.SkillStoreClosed,2000,Matches:[]));
        store.RecordStoreObservation(Open() with{ScannedAt=3000});Assert.Single(store.ClaimNotices());
    }

    [Fact] public void AddingAMatchingWatchWhileStoreIsAlreadyOpenAlertsThatSession(){
        using var store=new BridgeStore(Temp());var phone=Pair(store);
        store.ReplaceStoreWatch(phone.PairingId,new("profile-a",[]));store.RecordStoreObservation(Open());Assert.Empty(store.ClaimNotices());
        var result=store.ReplaceStoreWatch(phone.PairingId,new("profile-a",["017"]));
        Assert.Equal("017",Assert.Single(result.Matches!).SkillId);Assert.Single(store.ClaimNotices());
        store.ReplaceStoreWatch(phone.PairingId,new("profile-a",["017","003"]));Assert.Empty(store.ClaimNotices());
    }

    [Fact] public void DirectOpenStoreProfileChangeStartsANewNotificationSession(){
        using var store=new BridgeStore(Temp());var phone=Pair(store);
        store.ReplaceStoreWatch(phone.PairingId,new("profile-a",["017"]));store.ReplaceStoreWatch(phone.PairingId,new("profile-b",["003"]));
        store.RecordStoreObservation(Open("profile-a"));Assert.Single(store.ClaimNotices());
        store.RecordStoreObservation(new("profile-b",SkillStoreState.SkillStoreOpen,2000,"roll-b",[new("003",150,"Psycho Knife")]));
        var notice=Assert.Single(store.ClaimNotices());Assert.Contains("Psycho Knife",notice.Message);Assert.Equal(SkillStoreState.ProfileMismatch,store.StoreWatch(phone.PairingId,"profile-a").State);Assert.Equal(SkillStoreState.SkillStoreOpen,store.StoreWatch(phone.PairingId,"profile-b").State);
    }

    [Fact] public void RevokingPairingRemovesItsPersistedSubscription(){
        var path=Temp();string identity="stable-phone";PairResponse first;
        using(var store=new BridgeStore(path)){first=Pair(store,identity:identity);store.ReplaceStoreWatch(first.PairingId,new("profile-a",["017"]));store.Revoke(first.PairingId);}
        using var restarted=new BridgeStore(path);var repaired=Pair(restarted,identity:identity);Assert.NotEqual(first.PairingId,repaired.PairingId);
        Assert.Equal(SkillStoreState.Unsupported,restarted.StoreWatch(repaired.PairingId,"profile-a").State);
    }
}
