using PhantomDust.PcBridge.Core;
using Xunit;
namespace PhantomDust.PcBridge.Tests;
public class ControlledQueueTests {
    private sealed class Reader(Snapshot snapshot):IPhantomDustProfileReader { public Snapshot Read()=>snapshot; }
    private static readonly Planner Planner=new([new("000","Aura",null,"aura"),new("005","Excalibur","psycho","attack")]);
    private static Snapshot Snapshot()=>new("test-session","all",Wire.Now,true,false,true,true,false,[new(1,"Arsenal00",2,Enumerable.Repeat("000",30).ToArray(),"target")],new Dictionary<string,int>{{"005",3}},ControlledTestSession:true);
    private static (BridgeStore Store,string Device,QueueRequest Request) Setup(){
        var store=new BridgeStore(Path.Combine(Path.GetTempPath(),"pd-tests",Guid.NewGuid().ToString(),"bridge.db"));
        var pair=store.Pair(new(store.BeginPairing("https://localhost","pin").BootstrapSecret,"phone"));
        var deck=new DeckSpec(Guid.NewGuid().ToString(),1,"Phone",["005",..Enumerable.Repeat("000",29)],2,"retail_2017_full");
        var link=store.Link(pair.PairingId,new(Guid.NewGuid().ToString(),deck.DeckId,"test-session",1,1,"target"),Snapshot());
        var request=new QueueRequest("test-session",1,"target",deck,"target",link.LinkId,1);
        store.Queue(pair.PairingId,request);return(store,pair.PairingId,request);
    }
    [Fact] public void TestGateDoesNotClaimStableIdentity(){var s=Snapshot();Assert.False(s.ProfileVerified);Assert.False(s.SafeStateVerified);Assert.True(Planner.Plan(new(s.ProfileKey,1,"target",SetupDeck()),s).CanApply);Assert.False(Planner.Plan(new(s.ProfileKey,1,"target",SetupDeck()),s with{ControlledTestSession=false}).CanApply);}
    private static DeckSpec SetupDeck()=>new(Guid.NewGuid().ToString(),1,"Phone",Enumerable.Repeat("000",30).ToArray(),1,"retail_2017_full");
    [Fact] public void ApplyingIsRecordedBeforeWriterAndCannotBecomeSaved(){var (store,device,_)=Setup();using(store){store.ExecuteOneTestJob(new Reader(Snapshot()),Planner,_=>{Assert.Equal(JobState.Applying,Assert.Single(store.Jobs(device)).State);return JobState.AppliedAwaitingGameSave;});Assert.Equal(JobState.AppliedAwaitingGameSave,Assert.Single(store.Jobs(device)).State);store.Cancel(device,Assert.Single(store.Jobs(device)).JobId);Assert.Equal(JobState.AppliedAwaitingGameSave,Assert.Single(store.Jobs(device)).State);}}
    [Theory] [InlineData(true)] [InlineData(false)] public void PauseOrCancelPreventsExecution(bool pause){var (store,device,_)=Setup();using(store){if(pause)store.Pause(device,true);else store.Cancel(device,Assert.Single(store.Jobs(device)).JobId);store.ExecuteOneTestJob(new Reader(Snapshot()),Planner,_=>throw new Xunit.Sdk.XunitException("Writer must not run"));Assert.NotEqual(JobState.RecoveryRequired,Assert.Single(store.Jobs(device)).State);}}
    [Fact] public void ExceptionsRequireRecoveryAndNeverRetry(){var (store,device,_)=Setup();using(store){var calls=0;store.ExecuteOneTestJob(new Reader(Snapshot()),Planner,_=>{calls++;throw new IOException();});store.ExecuteOneTestJob(new Reader(Snapshot()),Planner,_=>{calls++;return JobState.AppliedAwaitingGameSave;});Assert.Equal(1,calls);Assert.Equal(JobState.RecoveryRequired,Assert.Single(store.Jobs(device)).State);}}
    [Fact] public void NewSessionCannotExecuteOldJob(){var (store,device,_)=Setup();using(store){store.ExecuteOneTestJob(new Reader(Snapshot() with{ProfileKey="other-session"}),Planner,_=>throw new Xunit.Sdk.XunitException("Wrong session"));Assert.Equal(JobState.Queued,Assert.Single(store.Jobs(device)).State);}}
    [Fact] public void ShortagesBlockWithoutEnteringWriter(){var (store,device,_)=Setup();using(store){store.ExecuteOneTestJob(new Reader(Snapshot() with{FreeInventory=new Dictionary<string,int>{{"005",0}}}),Planner,_=>throw new Xunit.Sdk.XunitException("Shortage"));Assert.Equal(JobState.Blocked,Assert.Single(store.Jobs(device)).State);Assert.Single(Assert.Single(store.Jobs(device)).Plan!.MissingSkills);}}
}
