using System.Text;
using PhantomDust.PcBridge.Core;
using Xunit;
namespace PhantomDust.PcBridge.Tests;
public class ArsenalReaderTests {
    static SkillIdMap Map=>new(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"skill-id-map.json")));
    static byte[] Table(){var data=new byte[ArsenalReader.Length];Encoding.ASCII.GetBytes("Roots").CopyTo(data,8);data[0x54]=1;for(var i=0;i<30;i++)Map.Encode(i==0?"004":"000").CopyTo(data,0x18+i*2);return data;}
    [Fact] public void ReadsOrderedCardsAndPreservesSlotGaps(){var data=Table();Array.Copy(data,0,data,200,100);Array.Clear(data,0,100);var deck=Assert.Single(ArsenalReader.Decode(data,Map));Assert.Equal(3,deck.Slot);Assert.Equal("Roots",deck.Name);Assert.Equal(1,deck.CaseCapacity);Assert.Equal("004",deck.SkillIds[0]);Assert.Equal(29,deck.SkillIds.Count(x=>x=="000"));Assert.NotEmpty(deck.Fingerprint!);Assert.Equal(Planner.CardContentFingerprint(deck.SkillIds),deck.ContentFingerprint);}
    [Fact] public void RejectsUnknownCards(){var data=Table();data[0x18]=0xaa;data[0x19]=0xaa;Assert.Throws<InvalidDataException>(()=>ArsenalReader.Decode(data,Map));}
    [Theory] [InlineData(0)] [InlineData(4)] [InlineData(255)] public void RejectsInvalidCases(int capacity){var data=Table();data[0x54]=(byte)capacity;Assert.Throws<InvalidDataException>(()=>ArsenalReader.Decode(data,Map));}
    [Fact] public void RejectsPartialRead(){Assert.Throws<InvalidDataException>(()=>ArsenalReader.Decode(new byte[ArsenalReader.Length-1],Map));}
    [Fact] public void RejectsUnsupportedNameEncoding(){var data=Table();data[8]=0xff;Assert.Throws<InvalidDataException>(()=>ArsenalReader.Decode(data,Map));}
    [Fact] public void EmptyTableDoesNotInventCases(){Assert.Empty(ArsenalReader.Decode(new byte[ArsenalReader.Length],Map));}
    [Fact] public void ImportCapabilityDoesNotGrantWriteIdentity(){var snapshot=new Snapshot("",null,Wire.Now,true,false,false,false,false,ArsenalReader.Decode(Table(),Map),new Dictionary<string,int>(),ArsenalImportSupported:true);var planner=new Planner([new("000","Aura",null,"aura"),new("004","Blade","psycho","attack")]);var target=snapshot.Arsenals[0];var plan=planner.Plan(new("",1,target.Fingerprint,new(Guid.NewGuid().ToString(),1,"Phone",target.SkillIds,1,"retail_2017_full")),snapshot);Assert.False(plan.CanApply);Assert.False(plan.CanBuild);Assert.Contains(plan.Blockers,x=>x.Code=="ProfileUnverified");}
}
