using System.Text;
namespace PhantomDust.PcBridge.Core;

// A read-only copy is useful without claiming inventory ownership or a stable writable profile.
public static class ArsenalReader {
    public const int Length = 16 * 0x64;
    public static SnapshotDeck[] Decode(byte[] records, SkillIdMap map) {
        if(records.Length != Length) throw new InvalidDataException("Incomplete arsenal table.");
        var result = new List<SnapshotDeck>();
        for(var slot=0;slot<16;slot++) {
            var start=slot*0x64;
            var nameBytes=records.AsSpan(start+8,16);
            var terminator=nameBytes.IndexOf((byte)0);
            if(terminator>=0)nameBytes=nameBytes[..terminator];
            var capacity=BitConverter.ToUInt16(records,start+0x54);
            if(nameBytes.IsEmpty && capacity==0)continue;
            if(nameBytes.IsEmpty || capacity is <1 or >3 || nameBytes.ToArray().Any(b=>b<32 || b>126))
                throw new InvalidDataException($"Arsenal {slot+1} has an unsupported name or case; refresh in the arsenal menu.");
            var name=Encoding.ASCII.GetString(nameBytes);
            var ids=map.DecodeArsenal(records.AsSpan(start+0x18,60).ToArray());
            result.Add(new(slot+1,name,capacity,ids,Planner.TargetFingerprint(slot+1,name,capacity,ids),Planner.CardContentFingerprint(ids)));
        }
        return result.ToArray();
    }
}
