namespace PhantomDust.PcBridge.Core;

public sealed record SkillAllocation(int ArsenalSlot,int Copies);
public sealed record OwnedSkill(int Total,int Free,IReadOnlyList<SkillAllocation> Allocations);
public static class CollectionAccounting {
    // For builds whose stored counters have been verified as total ownership.
    // Writing a deck changes assignments; these ownership counters must stay unchanged.
    public static IReadOnlyDictionary<string,OwnedSkill> FromTotals(IReadOnlyDictionary<string,int> totals,IReadOnlyList<SnapshotDeck> arsenals,IReadOnlyCollection<string> numberedIds){
        var expected=numberedIds.ToHashSet(StringComparer.Ordinal);
        if(expected.Contains("000")||expected.Count!=numberedIds.Count||totals.Count!=expected.Count||totals.Any(x=>!expected.Contains(x.Key)||x.Value is <0 or >255))throw new InvalidDataException("Ownership snapshot is incomplete or invalid.");
        if(arsenals.Select(x=>x.Slot).Distinct().Count()!=arsenals.Count||arsenals.Any(x=>x.SkillIds.Count!=30||x.SkillIds.Any(id=>id!="000"&&!expected.Contains(id))))throw new InvalidDataException("Arsenal assignments are incomplete or invalid.");
        var result=new Dictionary<string,OwnedSkill>();
        foreach(var id in expected){
            var allocations=arsenals.Select(x=>new SkillAllocation(x.Slot,x.SkillIds.Count(s=>s==id))).Where(x=>x.Copies>0).ToArray();
            var free=totals[id]-allocations.Sum(x=>x.Copies);
            if(free<0)throw new InvalidDataException("Assigned copies exceed total ownership; do not infer availability.");
            result.Add(id,new(totals[id],free,allocations));
        }
        return result;
    }
}
