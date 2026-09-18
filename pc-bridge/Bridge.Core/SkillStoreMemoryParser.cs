using System.Buffers.Binary;
namespace PhantomDust.PcBridge.Core;

/// Validates the six fixed retail Skill Store availability blocks. The game
/// stores IDs as the mapped little-endian ushort values; prices are not part of
/// this memory contract and are therefore deliberately unknown.
public static class SkillStoreMemoryParser {
    public const int BlockSize=0x7D4;
    private static readonly string[] Categories=["attack","defense","erase","status","special","environment"];

    public static SkillStoreResult Parse(IReadOnlyList<byte[]> blocks,byte activeMarker,string profileKey,long scannedAt,SkillIdMap map,IReadOnlyDictionary<string,CatalogueSkill> catalogue){
        if(blocks.Count!=Categories.Length||blocks.Any(x=>x.Length!=BlockSize))throw new InvalidDataException("Skill Store memory layout is truncated.");
        var seen=new HashSet<string>(StringComparer.Ordinal);var matches=new List<SkillStoreMatch>();
        for(var categoryIndex=0;categoryIndex<Categories.Length;categoryIndex++){
            var bytes=blocks[categoryIndex];var count=BinaryPrimitives.ReadInt32LittleEndian(bytes);
            var maximum=catalogue.Values.Count(x=>x.Category==Categories[categoryIndex]);
            if(count<1||count>1000||count>maximum)throw new InvalidDataException("Skill Store category count is invalid.");
            for(var i=0;i<count;i++){
                var offset=4+i*2;if(offset+1>=bytes.Length)throw new InvalidDataException("Skill Store category is truncated.");
                var id=map.Decode(bytes[offset],bytes[offset+1]);
                if(id=="000"||!catalogue.TryGetValue(id,out var skill)||skill.Category!=Categories[categoryIndex]||!seen.Add(id))throw new InvalidDataException("Skill Store contains an invalid, duplicate, or miscategorized skill.");
                matches.Add(new(id,null,skill.Name));
            }
        }
        if(activeMarker is not 0 and not 1)throw new InvalidDataException("Skill Store active marker is invalid.");
        var ids=matches.Select(x=>x.SkillId).OrderBy(x=>x,StringComparer.Ordinal).ToArray();
        var state=activeMarker==1?SkillStoreState.SkillStoreOpen:SkillStoreState.SkillStoreClosed;
        return new(profileKey,state,scannedAt,Planner.Hash(string.Join(',',ids)),state==SkillStoreState.SkillStoreOpen?matches.OrderBy(x=>x.SkillId,StringComparer.Ordinal).ToArray():[]);
    }
}
