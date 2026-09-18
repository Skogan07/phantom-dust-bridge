using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using PhantomDust.PcBridge.Core;

namespace PhantomDust.PcBridge.App;

// Operator-only final-profile test writer. This is never selected by
// normal bridge startup and ends when the captured process/profile context changes.
internal sealed class DisposableTestSession : IPhantomDustProfileReader {
    private readonly object gate=new();
    private readonly int pid;
    private readonly DateTime started;
    private readonly nuint pointerAddress,root;
    private byte[] original;
    private readonly byte[] inventory;
    private readonly SkillIdMap map;
    private readonly Planner planner;
    private readonly string session="disposable-test-"+Guid.NewGuid(),journal;
    private readonly string sessionKey=Convert.ToBase64String(RandomNumberGenerator.GetBytes(24));
    private bool ended;
    private static readonly string[] VerifiedIds=["004","005","121","123","125","139"];

    public DisposableTestSession(string grantJournal,string directory,Planner planner){
        this.planner=planner;
        map=new(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"skill-id-map.json")));
        var grant=JsonNode.Parse(File.ReadAllText(grantJournal))!;
        var outcome=grant["outcome"]?.GetValue<string>();
        if(outcome is not ("AppliedToLiveMemory_SaveNotVerified" or "CapturedForFinalControlledTest"))throw new InvalidOperationException("Expected controlled test evidence.");
        inventory=Convert.FromHexString(grant["intendedInventory"]!.GetValue<string>());
        if(inventory.Length!=848||VerifiedIds.Any(id=>inventory[BitConverter.ToUInt16(map.Encode(id))]!=3))throw new InvalidOperationException("Test inventory baseline is invalid.");
        if(outcome=="CapturedForFinalControlledTest"){
            if(grant["profileName"]?.GetValue<string>()!="final")throw new InvalidOperationException("This controlled test is restricted to the final profile.");
            if(Math.Abs(Wire.Now-grant["capturedAt"]!.GetValue<long>())>5*60*1000)throw new InvalidOperationException("Controlled test evidence is stale. Capture it again from the arsenal list.");
        }
        var processes=Process.GetProcessesByName("PDUWP");
        try{
            if(processes.Length!=1)throw new InvalidOperationException("Load the disposable profile before arming.");
            var process=processes[0];pid=process.Id;started=process.StartTime.ToUniversalTime();
            var expected=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"WindowsApps","Microsoft.MSEsper_1.3.25854.2_x64__8wekyb3d8bbwe","PDUWP.exe");
            if(!string.Equals(process.MainModule!.FileName,expected,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("Unsupported test build.");
            pointerAddress=(nuint)process.MainModule.BaseAddress+0x003ED6B8;
            using var memory=new ReadOnlyMemory(pid);root=(nuint)BitConverter.ToUInt64(memory.Read(pointerAddress,8));
            if(root<0x10000||(ulong)root>0x00007FFFFFFFFFFF)throw new InvalidOperationException("Invalid profile pointer.");
            original=memory.Read(root,0x644);
            var decks=ArsenalReader.Decode(original[..ArsenalReader.Length],map);
            string[] expectedCurrent=outcome=="CapturedForFinalControlledTest"?["005","005","125","139","329","359",..Enumerable.Repeat("000",24)]:["005",..Enumerable.Repeat("000",29)];
            if(decks.Length!=1||decks[0].Slot!=1||decks[0].Name!="Arsenal00"||decks[0].CaseCapacity!=2||!decks[0].SkillIds.SequenceEqual(expectedCurrent))throw new InvalidOperationException("The disposable final Arsenal00 no longer matches the controlled fixture.");
            if(outcome=="CapturedForFinalControlledTest"){
                var live=new GameReader("controlled-final-check").Read();
                if(!live.ProfileVerified||live.GameProfileDisplayName!="final"||live.GameProfileSlot!=1)throw new InvalidOperationException("The verified final game profile is not loaded.");
                if(!original.SequenceEqual(Convert.FromHexString(grant["originalArsenal"]!.GetValue<string>())))throw new InvalidOperationException("The captured arsenal changed before the test was armed.");
                if(started!=DateTime.Parse(grant["processStart"]!.GetValue<string>()).ToUniversalTime())throw new InvalidOperationException("The game process changed after evidence capture.");
            }
            if(!memory.Read(root+0x644,848).SequenceEqual(inventory)||!memory.Read(root,0x644).SequenceEqual(original))throw new InvalidOperationException("Test inventory/profile no longer matches the recorded grant.");
        }finally{foreach(var p in processes)p.Dispose();}
        journal=Path.Combine(directory,session+".json");
        Persist("Armed",original);
    }
    private bool ContextMatches(IProcessMemory memory){
        using var p=Process.GetProcessById(pid);
        return !p.HasExited&&p.StartTime.ToUniversalTime()==started&&(nuint)BitConverter.ToUInt64(memory.Read(pointerAddress,8))==root&&memory.Read(root,0x644).SequenceEqual(original)&&memory.Read(root+0x644,848).SequenceEqual(inventory);
    }
    public Snapshot Read(){lock(gate){
        if(!ended)try{using var memory=new ReadOnlyMemory(pid);if(ContextMatches(memory))return Snapshot();ended=true;}catch{ended=true;}
        var read=new GameReader().Read();return read with{Diagnostics=new Dictionary<string,string>{{"mode","disposable-test-ended"},{"verification","Test session ended or profile changed. Normal writes remain disabled."}}};
    }}
    private Snapshot Snapshot(){
        var decks=ArsenalReader.Decode(original[..ArsenalReader.Length],map);
        var ids=Enumerable.Range(1,374).Select(n=>n.ToString("D3")).ToArray();
        // The explicitly unsafe final-profile mode intentionally bypasses
        // ownership availability. The deck validator still requires known retail
        // skill IDs, 30 slots, and no more than three copies of a numbered skill.
        var free=ids.ToDictionary(id=>id,_=>3);
        return new(session,Planner.Hash(Convert.ToHexString(original)),Wire.Now,true,false,true,true,false,decks,free,new Dictionary<string,string>{{"mode","final-test"},{"verification","Repeated writes are enabled for the explicitly selected final test profile. Ownership and save completion are not verified."}},true,true,sessionKey,null,1,"final","ExplicitFinalTest",ProfileImportSupported:false);
    }
    private void Persist(string outcome,byte[] intended){
        var bytes=JsonSerializer.SerializeToUtf8Bytes(new{outcome,session,pid,started,root=(ulong)root,originalArsenal=Convert.ToHexString(original),intendedArsenal=Convert.ToHexString(intended),inventory=Convert.ToHexString(inventory),updatedAt=Wire.Now},Wire.Json);
        using var stream=new FileStream(journal,FileMode.Create,FileAccess.Write,FileShare.Read,4096,FileOptions.WriteThrough);stream.Write(bytes);stream.Flush(true);
    }
    public JobState Apply(QueueRequest request){lock(gate){
        if(ended)return JobState.Blocked;
        var plan=planner.Plan(new(request.ProfileKey,request.TargetSlot,request.ExpectedFingerprint,request.Deck),Snapshot());
        if(!plan.CanApply||request.TargetSlot!=1||request.ConfirmedFingerprint!=plan.TargetFingerprint)return JobState.Blocked;
        var intended=original.ToArray();request.Deck.SkillIds.SelectMany(map.Encode).ToArray().CopyTo(intended,0x18);
        using var memory=new TestMemory(pid,started,pointerAddress,root);
        var result=ControlledMemoryWrite.Run(memory,root,original,inventory,intended,outcome=>Persist(outcome,intended));
        if(result==JobState.AppliedAwaitingGameSave){original=intended;Persist("Armed",original);}
        return result;
    }}
    private sealed class TestMemory : IControlledMemory,IDisposable {
        private readonly IntPtr handle;
        private readonly int pid; private readonly DateTime started; private readonly nuint pointerAddress,root;
        public bool SameContext { get { using var p=Process.GetProcessById(pid); return !p.HasExited&&p.StartTime.ToUniversalTime()==started&&(nuint)BitConverter.ToUInt64(Read(pointerAddress,8))==root; } }
        public TestMemory(int id,DateTime start,nuint pointer,nuint target){pid=id;started=start;pointerAddress=pointer;root=target;handle=OpenProcess(0x1838,false,id);if(handle==IntPtr.Zero)throw new InvalidOperationException("Cannot open test process.");}
        public byte[] Read(nuint address,int length){var data=new byte[length];if(!ReadProcessMemory(handle,address,data,(nuint)length,out var count)||count!=(nuint)length)throw new InvalidOperationException("Incomplete memory read.");return data;}
        public void Write(nuint address,byte[] bytes){if(bytes.Length!=60||!WriteProcessMemory(handle,address,bytes,60,out var count)||count!=60)throw new InvalidOperationException("Incomplete memory write.");}
        public void Suspend(){if(NtSuspendProcess(handle)!=0)throw new InvalidOperationException("Cannot suspend test process.");}
        public bool Resume()=>NtResumeProcess(handle)==0;
        public void Dispose()=>CloseHandle(handle);
        [DllImport("kernel32.dll")]private static extern IntPtr OpenProcess(uint access,bool inherit,int id);
        [DllImport("kernel32.dll")]private static extern bool ReadProcessMemory(IntPtr h,nuint address,byte[] data,nuint size,out nuint read);
        [DllImport("kernel32.dll")]private static extern bool WriteProcessMemory(IntPtr h,nuint address,byte[] data,nuint size,out nuint written);
        [DllImport("kernel32.dll")]private static extern bool CloseHandle(IntPtr h);
        [DllImport("ntdll.dll")]private static extern int NtSuspendProcess(IntPtr h);
        [DllImport("ntdll.dll")]private static extern int NtResumeProcess(IntPtr h);
    }
}


