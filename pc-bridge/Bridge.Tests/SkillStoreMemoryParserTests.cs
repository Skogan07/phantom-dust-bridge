using PhantomDust.PcBridge.Core;
using Xunit;

namespace PhantomDust.PcBridge.Tests;

public sealed class SkillStoreMemoryParserTests {
    private static readonly SkillIdMap Map=new(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"skill-id-map.json")));
    private static readonly Dictionary<string,CatalogueSkill> Catalogue=new(StringComparer.Ordinal){
        ["001"]=new("001","Attack","psycho","attack"),["026"]=new("026","Defense","psycho","defense"),["035"]=new("035","Erase","psycho","erase"),
        ["042"]=new("042","Status","psycho","status"),["052"]=new("052","Special","psycho","special"),["038"]=new("038","Environment","psycho","environment")};
    private static byte[] Block(string id){var bytes=new byte[SkillStoreMemoryParser.BlockSize];BitConverter.GetBytes(1).CopyTo(bytes,0);Map.Encode(id).CopyTo(bytes,4);return bytes;}
    private static byte[][] Blocks()=>[Block("001"),Block("026"),Block("035"),Block("042"),Block("052"),Block("038")];

    [Fact] public void ValidSixBlockLayoutUsesActiveMarkerAndUnknownPrices(){var result=SkillStoreMemoryParser.Parse(Blocks(),1,"profile",123,Map,Catalogue);var matches=result.Matches!;Assert.Equal(SkillStoreState.SkillStoreOpen,result.State);Assert.Equal(Catalogue.Keys.OrderBy(x=>x),matches.Select(x=>x.SkillId).OrderBy(x=>x));Assert.All(matches,x=>Assert.Null(x.Price));}
    [Fact] public void InactiveMarkerGatesStoreClosed(){var result=SkillStoreMemoryParser.Parse(Blocks(),0,"profile",123,Map,Catalogue);Assert.Equal(SkillStoreState.SkillStoreClosed,result.State);Assert.Empty(result.Matches!);}
    [Fact] public void WrongCategoryAndDuplicateFailClosed(){var wrong=Blocks();Map.Encode("026").CopyTo(wrong[0],4);Assert.Throws<InvalidDataException>(()=>SkillStoreMemoryParser.Parse(wrong,1,"profile",123,Map,Catalogue));var duplicate=Blocks();Map.Encode("001").CopyTo(duplicate[1],4);Assert.Throws<InvalidDataException>(()=>SkillStoreMemoryParser.Parse(duplicate,1,"profile",123,Map,Catalogue));}
    [Fact] public void TruncatedAndInvalidMarkerFailClosed(){var blocks=Blocks();blocks[0]=blocks[0][..100];Assert.Throws<InvalidDataException>(()=>SkillStoreMemoryParser.Parse(blocks,1,"profile",123,Map,Catalogue));Assert.Throws<InvalidDataException>(()=>SkillStoreMemoryParser.Parse(Blocks(),2,"profile",123,Map,Catalogue));}
}
