namespace PhantomDust.PcBridge.Core;

public interface IArsenalMemory : IProcessMemory {
    void Write(nuint address,byte[] bytes);
    void Suspend();
    bool Resume();
}

public static class ArsenalMemoryWriter {
    public static WriteResult Apply(IArsenalMemory memory,nuint root,byte[] original,byte[] inventory,QueueRequest request,SkillIdMap map,Func<bool> contextMatches){
        if(request.TargetSlot is <1 or >16||!ArsenalNamePolicy.IsValid(request.Deck.Name))return new(JobState.Blocked,Reason:ArsenalNamePolicy.Message);
        var target=ArsenalReader.Decode(original,map).SingleOrDefault(x=>x.Slot==request.TargetSlot);
        if(target is null||target.Fingerprint!=request.ExpectedFingerprint||target.Fingerprint!=request.ConfirmedFingerprint)return new(JobState.Blocked,Reason:"The PC arsenal changed. Choose which version to keep.");
        if(request.Deck.SkillIds.Count!=30||target.CaseCapacity<request.Deck.CaseCapacity)return new(JobState.Blocked,Reason:"Choose a compatible PC case for this arsenal.");
        var start=(request.TargetSlot-1)*0x64;var intended=original.ToArray();
        Array.Clear(intended,start+8,16);System.Text.Encoding.ASCII.GetBytes(request.Deck.Name).CopyTo(intended,start+8);
        request.Deck.SkillIds.SelectMany(map.Encode).ToArray().CopyTo(intended,start+0x18);
        var changes=new ArsenalChange(new(target.Name,target.SkillIds.ToArray(),target.CaseCapacity),new(request.Deck.Name,request.Deck.SkillIds.ToArray(),target.CaseCapacity));
        var suspended=false;var touched=false;WriteResult result;
        try{
            memory.Suspend();suspended=true;
            if(!contextMatches()||!memory.Read(root,original.Length).SequenceEqual(original)||!memory.Read(root+0x644,inventory.Length).SequenceEqual(inventory))result=new(JobState.Blocked,Reason:"The game changed before syncing. Try again.");
            else{
                touched=true;memory.Write(root+(nuint)(start+8),intended.AsSpan(start+8,16).ToArray());memory.Write(root+(nuint)(start+0x18),intended.AsSpan(start+0x18,60).ToArray());
                if(!memory.Read(root,original.Length).SequenceEqual(intended)||!memory.Read(root+0x644,inventory.Length).SequenceEqual(inventory))throw new IOException("Readback failed.");
                result=new(JobState.AppliedAwaitingGameSave,changes,"Synced to PC. Save in Phantom Dust to keep these changes.");
            }
        }catch(Exception){
            if(!touched)result=new(JobState.Blocked,Reason:"Couldn’t update Phantom Dust. Try again.");
            else try{
                var bytes=memory.Read(root,original.Length);
                if(bytes.Where((b,i)=>b!=original[i]&&b!=intended[i]).Any())throw new IOException();
                memory.Write(root+(nuint)(start+8),original.AsSpan(start+8,16).ToArray());memory.Write(root+(nuint)(start+0x18),original.AsSpan(start+0x18,60).ToArray());
                if(!memory.Read(root,original.Length).SequenceEqual(original))throw new IOException();
                result=new(JobState.Blocked,Reason:"Couldn’t sync. The original PC arsenal was restored.");
            }catch{result=new(JobState.RecoveryRequired,changes,"Check this arsenal in Phantom Dust before trying again.");}
        }
        if(suspended){try{if(!memory.Resume())return new(JobState.RecoveryRequired,changes,"Phantom Dust could not resume. Restart the game and check this arsenal.");}catch{return new(JobState.RecoveryRequired,changes,"Restart Phantom Dust and check this arsenal before syncing again.");}}
        return result;
    }
}
