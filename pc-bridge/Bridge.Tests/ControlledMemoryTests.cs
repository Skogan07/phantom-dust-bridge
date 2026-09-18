using PhantomDust.PcBridge.Core;
using Xunit;
namespace PhantomDust.PcBridge.Tests;
public class ControlledMemoryTests {
    private sealed class Memory:IControlledMemory {
        public byte[] Bytes=new byte[0x644+848];
        public bool SameContext{get;set;}=true;
        public bool Suspended,Resumed,FailResume,PartialFailure,RollbackFailure,ChangeContext;
        public int Writes;
        public byte[] Read(nuint address,int length)=>Bytes.AsSpan((int)address,length).ToArray();
        public void Write(nuint address,byte[] bytes){Assert.True(Suspended);Writes++;if(Writes==2&&RollbackFailure)throw new IOException();bytes.CopyTo(Bytes,(int)address);if(Writes==1){if(ChangeContext)SameContext=false;if(PartialFailure){Bytes[(int)address+1]=0;throw new IOException();}}}
        public void Suspend()=>Suspended=true;
        public bool Resume(){Resumed=true;Suspended=false;return !FailResume;}
    }
    private static JobState Run(Memory m,Action<string>? journal=null){var desired=new byte[0x644];desired[0x18]=5;desired[0x19]=1;return ControlledMemoryWrite.Run(m,0,new byte[0x644],new byte[848],desired,journal??(_=>{}));}
    [Fact] public void OnlyCardBytesChangeAndResumeAlwaysOccurs(){var m=new Memory();var journal=new List<string>();Assert.Equal(JobState.AppliedAwaitingGameSave,Run(m,journal.Add));Assert.True(m.Resumed);Assert.Equal(1,m.Writes);Assert.Equal(new[]{"Prepared","AppliedAwaitingGameSave"},journal);Assert.Equal(2,m.Bytes.Count(b=>b!=0));}
    [Fact] public void PartialWriteRollsBack(){var m=new Memory{PartialFailure=true};Assert.Equal(JobState.Blocked,Run(m));Assert.Equal(2,m.Writes);Assert.All(m.Bytes,b=>Assert.Equal(0,b));Assert.True(m.Resumed);}
    [Fact] public void RollbackFailureRequiresRecovery(){var m=new Memory{PartialFailure=true,RollbackFailure=true};Assert.Equal(JobState.RecoveryRequired,Run(m));Assert.True(m.Resumed);}
    [Fact] public void ContextChangePreventsRollback(){var m=new Memory{ChangeContext=true};Assert.Equal(JobState.RecoveryRequired,Run(m));Assert.Equal(1,m.Writes);Assert.True(m.Resumed);}
    [Fact] public void ResumeFailureIsNotReportedAsApplied(){var m=new Memory{FailResume=true};Assert.Equal(JobState.RecoveryRequired,Run(m));}
    [Fact] public void StaleInventoryPreventsAllWrites(){var m=new Memory();m.Bytes[0x644]=1;Assert.Equal(JobState.Blocked,Run(m));Assert.Equal(0,m.Writes);Assert.True(m.Resumed);}
    [Fact] public void JournalFailureBeforeWritePreventsMutation(){var m=new Memory();Assert.Throws<IOException>(()=>Run(m,_=>throw new IOException()));Assert.Equal(0,m.Writes);Assert.False(m.Suspended);}
    [Fact] public void NameOrCaseChangesRejected(){var m=new Memory();var desired=new byte[0x644];desired[8]=1;Assert.Throws<InvalidOperationException>(()=>ControlledMemoryWrite.Run(m,0,new byte[0x644],new byte[848],desired,_=>{}));Assert.Equal(0,m.Writes);}
}
