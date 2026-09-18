using PhantomDust.PcBridge.Core;
using Xunit;

namespace PhantomDust.PcBridge.Tests;

public class SaveVerificationTests {
    private static readonly string[] Cards=["005",..Enumerable.Repeat("000",29)];

    private static SnapshotCollection Collection(IReadOnlyList<SnapshotDeck>? decks=null,int changedTotal=3,bool omitAllocation=false) {
        decks??=[];
        var skills=Enumerable.Range(1,374).Select(n=>n.ToString("D3")).ToDictionary(id=>id,id=>{
            var allocations=decks.Select(d=>new SkillAllocation(d.Slot,d.SkillIds.Count(skill=>skill==id))).Where(a=>a.Copies>0).ToArray();
            if(omitAllocation&&id=="005")allocations=[];
            var assigned=allocations.Sum(a=>a.Copies);
            var total=id=="006"?changedTotal:3;
            return new CollectionEntry(total,total-assigned,assigned,allocations);
        });
        return new(CollectionStatus.Verified,skills);
    }

    private static (JobView Job,Snapshot Snapshot) ValidPair(bool controlledEvidence=false) {
        var deck=new SnapshotDeck(2,"PC Arsenal",1,Cards,Planner.TargetFingerprint(2,"PC Arsenal",1,Cards));
        var request=new QueueRequest("verified-profile",2,"before",new DeckSpec(Guid.NewGuid().ToString(),7,"Phone",Cards,1,"retail_2017_full"),"before",Guid.NewGuid().ToString(),1);
        var evidence=new AppliedEvidence("session-before","verified-profile",deck.Fingerprint!,1,Cards,Collection(),!controlledEvidence,controlledEvidence);
        var job=new JobView(Guid.NewGuid().ToString(),Guid.NewGuid().ToString(),request.Deck.DeckId,7,2,JobState.AppliedAwaitingGameSave,null,Wire.Now,Wire.Now,request,null,evidence);
        var snapshot=new Snapshot("verified-profile","profile",Wire.Now,true,true,true,false,true,[deck],new Dictionary<string,int>(),SessionKey:"session-after",Collection:Collection([deck]));
        return(job,snapshot);
    }

    [Fact] public void ExactVerifiedReloadCompletes() {
        var (job,snapshot)=ValidPair();
        var result=SaveVerification.Evaluate(job,snapshot);
        Assert.True(result.Verified);
        Assert.Equal("Save verified on PC.",result.Reason);
    }

    [Fact] public void SameSessionCannotComplete() {
        var (job,snapshot)=ValidPair();
        Assert.False(SaveVerification.Evaluate(job,snapshot with{SessionKey="session-before"}).Verified);
    }

    [Fact] public void ObservedProfileCommitCompletesWithoutProcessRestart() {
        var (job,snapshot)=ValidPair();var ids=job.Request.Deck.SkillIds;
        var target=snapshot.Arsenals[0] with{Name=job.Request.Deck.Name,SkillIds=ids,Fingerprint=Planner.TargetFingerprint(2,job.Request.Deck.Name,1,ids)};
        var evidence=job.Evidence! with{SessionKey="process-v1:same",TargetFingerprint=target.Fingerprint!,SkillIds=ids,Name=job.Request.Deck.Name};
        var committed=job with{Evidence=evidence,Changes=new(new("Before",Cards,1),new(job.Request.Deck.Name,ids,1)),Reason="Phantom Dust committed the profile save. Reload the profile to verify persistence."};
        var result=SaveVerification.Evaluate(committed,snapshot with{SessionKey="process-v1:same",Arsenals=[target],Collection=Collection([target])});
        Assert.True(result.Verified);Assert.Equal("Saved in Phantom Dust.",result.Reason);
    }

    [Fact] public void ControlledWriteCanNeverComplete() {
        var (job,snapshot)=ValidPair(controlledEvidence:true);
        Assert.False(SaveVerification.Evaluate(job,snapshot).Verified);
    }

    [Fact] public void MissingPcAllocationCannotComplete() {
        var (job,snapshot)=ValidPair();
        Assert.False(SaveVerification.Evaluate(job,snapshot with{Collection=Collection(snapshot.Arsenals,omitAllocation:true)}).Verified);
    }

    [Fact] public void IncompleteCollectionCannotComplete() {
        var (job,snapshot)=ValidPair();
        var incomplete=new SnapshotCollection(CollectionStatus.Verified,snapshot.Collection!.Skills.Where(x=>x.Key!="374").ToDictionary());
        Assert.False(SaveVerification.Evaluate(job,snapshot with{Collection=incomplete}).Verified);
    }

    [Fact] public void ChangedCollectionTotalCannotComplete() {
        var (job,snapshot)=ValidPair();
        Assert.False(SaveVerification.Evaluate(job,snapshot with{Collection=Collection(snapshot.Arsenals,changedTotal:4)}).Verified);
    }

    [Fact] public void WrongProfileOrCardsCannotComplete() {
        var (job,snapshot)=ValidPair();
        Assert.False(SaveVerification.Evaluate(job,snapshot with{ProfileKey="other"}).Verified);
        var changed=snapshot.Arsenals[0] with{SkillIds=["006",..Enumerable.Repeat("000",29)]};
        Assert.False(SaveVerification.Evaluate(job,snapshot with{Arsenals=[changed]}).Verified);
    }
}
