namespace PhantomDust.PcBridge.Core;

public interface IControlledMemory : IProcessMemory {
    bool SameContext { get; }
    void Write(nuint address,byte[] bytes);
    void Suspend();
    bool Resume();
}

// One bounded card-range experiment; has no production game-state detection.
public static class ControlledMemoryWrite {
    public static JobState Run(IControlledMemory memory,nuint root,byte[] original,byte[] inventory,byte[] intended,Action<string> journal){
        if(original.Length!=0x644||intended.Length!=original.Length||inventory.Length!=848||original.Where((b,i)=>(i<0x18||i>=0x18+60)&&b!=intended[i]).Any())throw new InvalidOperationException("Only slot one's 60 card bytes may change.");
        var suspended=false;var wrote=false;var result=JobState.Blocked;
        try{
            journal("Prepared");
            memory.Suspend();suspended=true;
            if(!memory.SameContext||!memory.Read(root,original.Length).SequenceEqual(original)||!memory.Read(root+0x644,inventory.Length).SequenceEqual(inventory))throw new InvalidOperationException("Test context changed.");
            wrote=true;memory.Write(root+0x18,intended.AsSpan(0x18,60).ToArray());
            if(!memory.SameContext||!memory.Read(root,original.Length).SequenceEqual(intended)||!memory.Read(root+0x644,inventory.Length).SequenceEqual(inventory))throw new InvalidOperationException("Readback mismatch.");
            result=JobState.AppliedAwaitingGameSave;
        }catch{
            result=wrote?JobState.RecoveryRequired:JobState.Blocked;
            if(wrote&&suspended)try{
                var current=memory.Read(root,original.Length);
                if(!memory.SameContext||!memory.Read(root+0x644,inventory.Length).SequenceEqual(inventory)||current.Where((b,i)=>b!=original[i]&&b!=intended[i]).Any())throw new InvalidOperationException("Rollback context changed.");
                memory.Write(root+0x18,original.AsSpan(0x18,60).ToArray());
                if(!memory.SameContext||!memory.Read(root,original.Length).SequenceEqual(original)||!memory.Read(root+0x644,inventory.Length).SequenceEqual(inventory))throw new InvalidOperationException("Rollback failed.");
                result=JobState.Blocked;
            }catch{result=JobState.RecoveryRequired;}
        }finally{
            if(suspended&&!memory.Resume())result=JobState.RecoveryRequired;
            // Journal failure propagates to the durable queue's recovery state.
            journal(result.ToString());
        }
        return result;
    }
}
