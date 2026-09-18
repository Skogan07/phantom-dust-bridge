namespace PhantomDust.PcBridge.Core;

// A future verified writer must implement this contract. Production ReadOnlyMemory does not.
public interface ITransactionalMemory : IProcessMemory {
    string ContextIdentity {get;}
    bool SafeToWrite {get;}
    void Write(nuint address,byte[] bytes);
}
public sealed record MemoryChange(nuint Address,byte[] Original,byte[] Desired);
public interface IRecoveryJournal { void Begin(string context,IReadOnlyList<MemoryChange> changes); void End(string outcome); }
public sealed class SyncExecutor {
    public string Execute(ITransactionalMemory memory,IRecoveryJournal journal,IReadOnlyList<MemoryChange> changes,PlanResult plan){
        if(!plan.CanApply||!memory.SafeToWrite)throw new InvalidOperationException("Write gates not satisfied.");
        var context=memory.ContextIdentity;
        foreach(var c in changes)if(c.Original.Length!=c.Desired.Length||!memory.Read(c.Address,c.Original.Length).SequenceEqual(c.Original))throw new InvalidOperationException("Stale original state.");
        journal.Begin(context,changes);
        try {
            foreach(var c in changes){if(memory.ContextIdentity!=context||!memory.SafeToWrite)throw new InvalidOperationException("Context changed.");memory.Write(c.Address,c.Desired);}
            foreach(var c in changes)if(memory.ContextIdentity!=context||!memory.SafeToWrite||!memory.Read(c.Address,c.Desired.Length).SequenceEqual(c.Desired))throw new InvalidOperationException("Readback mismatch.");
            journal.End("AppliedAwaitingGameSave");return "AppliedAwaitingGameSave";
        }catch{
            try{
                if(memory.ContextIdentity!=context||!memory.SafeToWrite)throw new InvalidOperationException();
                // Avoid overwriting concurrent third-party changes during recovery.
                foreach(var c in changes){var current=memory.Read(c.Address,c.Original.Length);if(current.Where((b,i)=>b!=c.Original[i]&&b!=c.Desired[i]).Any())throw new InvalidOperationException();}
                foreach(var c in changes.Reverse()){if(memory.ContextIdentity!=context||!memory.SafeToWrite)throw new InvalidOperationException();memory.Write(c.Address,c.Original);}
                foreach(var c in changes)if(!memory.Read(c.Address,c.Original.Length).SequenceEqual(c.Original))throw new InvalidOperationException();
                journal.End("RolledBack");return "RolledBack";
            }catch{journal.End("RecoveryRequired");return "RecoveryRequired";}
        }
    }
}
