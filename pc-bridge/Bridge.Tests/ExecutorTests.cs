using PhantomDust.PcBridge.Core;
using Xunit;
namespace PhantomDust.PcBridge.Tests;
public class ExecutorTests {
    sealed class Memory : ITransactionalMemory {
        public string ContextIdentity{get;set;}="first"; public bool SafeToWrite{get;set;}=true;public byte[] Bytes=[1,2,3];public bool Fail; public bool ChangeContext;
        public byte[] Read(nuint a,int n)=>Bytes.Skip((int)a).Take(n).ToArray();
        public void Write(nuint a,byte[] bytes){Bytes[(int)a]=bytes[0];if(ChangeContext){ChangeContext=false;ContextIdentity="new";throw new IOException();}if(Fail){Fail=false;throw new IOException();}Array.Copy(bytes,0,Bytes,(int)a,bytes.Length);}
    }
    sealed class Journal:IRecoveryJournal{public string State="";public void Begin(string c,IReadOnlyList<MemoryChange> changes)=>State="Applying";public void End(string outcome)=>State=outcome;}
    static PlanResult Plan=>new(true,true,[],[],"s","t",new Dictionary<string,int>());
    [Fact] public void ReadbackSuccess(){var m=new Memory();var j=new Journal();Assert.Equal("AppliedAwaitingGameSave",new SyncExecutor().Execute(m,j,[new(0,[1,2],[4,5])],Plan));Assert.Equal(new byte[]{4,5,3},m.Bytes);}
    [Fact] public void PartialWriteRollsBack(){var m=new Memory{Fail=true};var j=new Journal();Assert.Equal("RolledBack",new SyncExecutor().Execute(m,j,[new(0,[1,2],[4,5])],Plan));Assert.Equal(new byte[]{1,2,3},m.Bytes);}
    [Fact] public void ProcessOrProfileChangeNeverReplaysOldPointers(){var m=new Memory{ChangeContext=true};var j=new Journal();Assert.Equal("RecoveryRequired",new SyncExecutor().Execute(m,j,[new(0,[1,2],[4,5])],Plan));}
    [Fact] public void StaleStateAndDisabledGateNeverWrite(){var m=new Memory();var j=new Journal();Assert.Throws<InvalidOperationException>(()=>new SyncExecutor().Execute(m,j,[new(0,[9],[4])],Plan));Assert.Throws<InvalidOperationException>(()=>new SyncExecutor().Execute(m,j,[],Plan with{CanApply=false}));Assert.Empty(j.State);}
}
