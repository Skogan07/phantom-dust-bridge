using PhantomDust.PcBridge.Core;
using Xunit;
namespace PhantomDust.PcBridge.Tests;
public class ArsenalMemoryWriterTests {
    static SkillIdMap Map=>new(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"skill-id-map.json")));
    sealed class Memory:IArsenalMemory {
        public byte[] Bytes=new byte[0x644+848];public bool Resumed;public bool ResumeWorks=true;public int Writes;
        public byte[] Read(nuint address,int length)=>Bytes.AsSpan((int)address,length).ToArray();
        public void Write(nuint address,byte[] value){Writes++;value.CopyTo(Bytes,(int)address);}
        public void Suspend(){}
        public bool Resume(){Resumed=true;return ResumeWorks;}
    }
    static Memory Fixture(){var m=new Memory();for(var slot=0;slot<16;slot++){var start=slot*0x64;System.Text.Encoding.ASCII.GetBytes("Arsenal"+slot).CopyTo(m.Bytes,start+8);m.Bytes[start+0x54]=3;for(var i=0;i<30;i++)Map.Encode("000").CopyTo(m.Bytes,start+0x18+i*2);}return m;}
    static QueueRequest Request(Memory m,int slot){var target=ArsenalReader.Decode(m.Read(0,ArsenalReader.Length),Map).Single(x=>x.Slot==slot);return new("profile",slot,target.Fingerprint,new(Guid.NewGuid().ToString(),1,"NewName",["004",..Enumerable.Repeat("000",29)],2,"retail_2017_full"),target.Fingerprint!,Guid.NewGuid().ToString(),1);}
    [Theory][InlineData(1)][InlineData(8)][InlineData(16)] public void ChangesOnlyChosenNameAndSkillsAndResumes(int slot){var m=Fixture();var before=m.Bytes.ToArray();var r=Request(m,slot);var result=ArsenalMemoryWriter.Apply(m,0,m.Read(0,ArsenalReader.Length),m.Read(0x644,848),r,Map,()=>true);Assert.Equal(JobState.AppliedAwaitingGameSave,result.State);Assert.True(m.Resumed);var target=ArsenalReader.Decode(m.Read(0,ArsenalReader.Length),Map).Single(x=>x.Slot==slot);Assert.Equal("NewName",target.Name);Assert.Equal(r.Deck.SkillIds,target.SkillIds);var start=(slot-1)*0x64;Assert.All(Enumerable.Range(0,before.Length).Where(i=>i<start+8||i>=start+0x54),i=>Assert.Equal(before[i],m.Bytes[i]));Assert.Equal(3,target.CaseCapacity);}
    [Fact]public void ChangedProfileNeverWrites(){var m=Fixture();var result=ArsenalMemoryWriter.Apply(m,0,m.Read(0,ArsenalReader.Length),m.Read(0x644,848),Request(m,1),Map,()=>false);Assert.Equal(JobState.Blocked,result.State);Assert.Equal(0,m.Writes);Assert.True(m.Resumed);}
    [Fact]public void ResumeFailureIsNotSuccessOrRetryable(){var m=Fixture();m.ResumeWorks=false;var result=ArsenalMemoryWriter.Apply(m,0,m.Read(0,ArsenalReader.Length),m.Read(0x644,848),Request(m,1),Map,()=>true);Assert.Equal(JobState.RecoveryRequired,result.State);}
    [Theory][InlineData("")][InlineData("               ")][InlineData("SixteenCharsHere")][InlineData("Has-Dash")][InlineData("Café")][InlineData("!!!")]
    public void InvalidNameDoesNotWriteOrTruncate(string name){var m=Fixture();var r=Request(m,1);r=r with{Deck=r.Deck with{Name=name}};var result=ArsenalMemoryWriter.Apply(m,0,m.Read(0,ArsenalReader.Length),m.Read(0x644,848),r,Map,()=>true);Assert.Equal(JobState.Blocked,result.State);Assert.Equal(0,m.Writes);}
    [Fact]public void SpacedNameDoesNotWriteSpacesAsInvalidCharacters(){var m=Fixture();var r=Request(m,1);r=r with{Deck=r.Deck with{Name="U Turn"}};var result=ArsenalMemoryWriter.Apply(m,0,m.Read(0,ArsenalReader.Length),m.Read(0x644,848),r,Map,()=>true);Assert.Equal(JobState.AppliedAwaitingGameSave,result.State);}
    [Fact]public void FifteenAlphanumericCharactersAreAccepted(){var m=Fixture();var r=Request(m,1);r=r with{Deck=r.Deck with{Name="Abcdefghijk1234"}};var result=ArsenalMemoryWriter.Apply(m,0,m.Read(0,ArsenalReader.Length),m.Read(0x644,848),r,Map,()=>true);Assert.Equal(JobState.AppliedAwaitingGameSave,result.State);}
    [Fact]public void SaveVerificationAcceptsObservedInventoryAndGameAuraCompaction(){
        var m=Fixture();var r=Request(m,1);var ids=new[]{"000","004"}.Concat(Enumerable.Repeat("000",28)).ToArray();r=r with{Deck=r.Deck with{SkillIds=ids}};
        var observed=new SnapshotCollection(CollectionStatus.Observed,new Dictionary<string,CollectionEntry>());
        var evidence=new AppliedEvidence("process-v1:before","profile",Planner.TargetFingerprint(1,r.Deck.Name,3,ids),3,ids,observed,true,false,r.Deck.Name);
        var change=new ArsenalChange(new("Old",ids,3),new(r.Deck.Name,ids,3));
        var job=new JobView("job","pair",r.Deck.DeckId,1,1,JobState.AppliedAwaitingGameSave,null,0,0,r,null,evidence,change);
        var compacted=ids.Where(x=>x!="000").Concat(ids.Where(x=>x=="000")).ToArray();
        var target=new SnapshotDeck(1,r.Deck.Name,3,compacted,Planner.TargetFingerprint(1,r.Deck.Name,3,compacted));
        var snapshot=new Snapshot("profile",null,0,true,true,false,true,false,[target],new Dictionary<string,int>(),SessionKey:"process-v1:after",Collection:observed);
        Assert.True(SaveVerification.Evaluate(job,snapshot).Verified);
        Assert.False(SaveVerification.Evaluate(job,snapshot with{SessionKey="process-v1:before"}).Verified);
        Assert.False(SaveVerification.Evaluate(job,snapshot with{Arsenals=[target with{Name="Different"}]}).Verified);
    }
}
