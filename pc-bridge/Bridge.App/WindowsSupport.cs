using System.Net;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using PhantomDust.PcBridge.Core;

namespace PhantomDust.PcBridge.App;
internal static class WindowsSupport {
    private const int ErrorInsufficientBuffer = 122;
    private const ushort VtLpwstr = 31;
    private delegate bool EnumWindowsProc(IntPtr window,IntPtr parameter);
    [StructLayout(LayoutKind.Sequential,Pack=4)] private struct PropertyKey { public Guid FormatId;public uint PropertyId;public PropertyKey(Guid formatId,uint propertyId){FormatId=formatId;PropertyId=propertyId;} }
    [StructLayout(LayoutKind.Explicit,Size=24)] private struct PropVariant { [FieldOffset(0)]public ushort Type;[FieldOffset(8)]public IntPtr Pointer; }
    [ComImport,Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore {
        [PreserveSig]int GetCount(out uint count);
        [PreserveSig]int GetAt(uint index,out PropertyKey key);
        [PreserveSig]int GetValue(ref PropertyKey key,out PropVariant value);
        [PreserveSig]int SetValue(ref PropertyKey key,ref PropVariant value);
        [PreserveSig]int Commit();
    }
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window,out uint processId);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window,uint flags);
    [DllImport("user32.dll")] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool EnumChildWindows(IntPtr parent,EnumWindowsProc callback,IntPtr parameter);
    [DllImport("kernel32.dll",SetLastError=true)] private static extern IntPtr OpenProcess(uint access,bool inherit,int processId);
    [DllImport("kernel32.dll")] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode)] private static extern int GetApplicationUserModelId(IntPtr process,ref uint length,IntPtr appId);
    [DllImport("shell32.dll")] private static extern int SHGetPropertyStoreForWindow(IntPtr window,ref Guid interfaceId,[MarshalAs(UnmanagedType.Interface)]out IPropertyStore store);
    [DllImport("ole32.dll")] private static extern int PropVariantClear(ref PropVariant value);
    private static string? ProcessAppId(Process process) {
        var handle=OpenProcess(0x1000,false,process.Id);if(handle==IntPtr.Zero)return null;
        try {
            uint length=0;if(GetApplicationUserModelId(handle,ref length,IntPtr.Zero)!=ErrorInsufficientBuffer||length<2||length>512)return null;
            var buffer=Marshal.AllocHGlobal(checked((int)length*2));
            try{return GetApplicationUserModelId(handle,ref length,buffer)==0?Marshal.PtrToStringUni(buffer):null;}finally{Marshal.FreeHGlobal(buffer);}
        }finally{CloseHandle(handle);}
    }
    private static string? WindowAppId(IntPtr window) {
        var interfaceId=typeof(IPropertyStore).GUID;if(SHGetPropertyStoreForWindow(window,ref interfaceId,out var store)!=0)return null;
        try {
            var key=new PropertyKey(new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"),5);
            if(store.GetValue(ref key,out var value)!=0)return null;
            try{return value.Type==VtLpwstr&&value.Pointer!=IntPtr.Zero?Marshal.PtrToStringUni(value.Pointer):null;}finally{PropVariantClear(ref value);}
        }finally{Marshal.ReleaseComObject(store);}
    }
    public static bool IsForeground(Process process) {
        var foreground=GetForegroundWindow();if(foreground==IntPtr.Zero)return false;
        GetWindowThreadProcessId(foreground,out var foregroundProcess);
        if(foregroundProcess==(uint)process.Id)return true;
        var mainWindow=process.MainWindowHandle;
        if(mainWindow!=IntPtr.Zero&&(foreground==mainWindow||GetAncestor(mainWindow,3)==foreground))return true;
        // Microsoft Store/UWP windows can be hosted by ApplicationFrameHost. Some
        // Windows versions expose the app-owned CoreWindow as a child; newer ones
        // expose only the host and put the exact package identity on its window.
        // Check both layouts and never infer identity from the visible title.
        var found=false;
        EnumChildWindows(foreground,(child,_)=>{GetWindowThreadProcessId(child,out var childProcess);found=childProcess==(uint)process.Id;return !found;},IntPtr.Zero);
        if(found)return true;
        var processAppId=ProcessAppId(process);return processAppId is not null&&string.Equals(processAppId,WindowAppId(foreground),StringComparison.Ordinal);
    }
    public static HashSet<string> PrivateAdapters() {
        var ids=new HashSet<string>(StringComparer.OrdinalIgnoreCase);object? manager=null;
        try {
            manager=Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("DCB00C01-570F-4A9B-8D69-199FDBA5723B"))!);
            foreach(dynamic network in ((dynamic)manager!).GetNetworks(1)) {try{if((int)network.GetCategory()==1)foreach(object connection in network.GetNetworkConnections()){try{ids.Add(((INetworkConnection)connection).GetAdapterId().ToString());}finally{Marshal.ReleaseComObject(connection);}}}finally{Marshal.ReleaseComObject(network);}}
        }catch {ids.Clear();}finally{if(manager is not null)Marshal.ReleaseComObject(manager);}
        return ids;
    }
    public static IPAddress[] Addresses()=>NetworkInterface.GetAllNetworkInterfaces().Where(x=>PrivateAdapters().Contains(x.Id.Trim('{','}')) && x.OperationalStatus==OperationalStatus.Up)
        .SelectMany(x=>x.GetIPProperties().UnicastAddresses).Select(x=>x.Address).Where(x=>x.AddressFamily==System.Net.Sockets.AddressFamily.InterNetwork&&IsPrivate(x)).Distinct().ToArray();
    public static bool IsPrivate(IPAddress address){var b=address.MapToIPv4().GetAddressBytes();return b[0]==10 || b[0]==172&&b[1]>=16&&b[1]<=31 || b[0]==192&&b[1]==168;}
    public static void EnsurePortAvailable(int port){
        using var query=new Process{StartInfo=new ProcessStartInfo{FileName="powershell.exe",UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true}};
        query.StartInfo.ArgumentList.Add("-NoProfile");query.StartInfo.ArgumentList.Add("-NonInteractive");query.StartInfo.ArgumentList.Add("-Command");query.StartInfo.ArgumentList.Add($"Get-NetTCPConnection -State Listen -LocalPort {port} -ErrorAction SilentlyContinue | Select-Object -First 1 OwningProcess,LocalAddress | ConvertTo-Json -Compress");
        if(!query.Start()||!query.WaitForExit(5000))throw new InvalidOperationException($"Port {port} ownership could not be checked; refusing Bridge startup.");
        var output=query.StandardOutput.ReadToEnd().Trim();if(string.IsNullOrWhiteSpace(output))return;
        try{using var json=JsonDocument.Parse(output);var owner=json.RootElement.GetProperty("OwningProcess").GetInt32();var address=json.RootElement.TryGetProperty("LocalAddress",out var a)?a.GetString():"unknown";string path="unknown";try{using var process=Process.GetProcessById(owner);path=process.MainModule?.FileName??process.ProcessName;}catch{}throw new InvalidOperationException($"Port {port} is already owned by process {owner} ({path}, {address}). Stop it explicitly before starting Phantom Dust Bridge.");}catch(JsonException){throw new InvalidOperationException($"Port {port} ownership could not be checked; refusing Bridge startup.");}
    }
    public static X509Certificate2 Certificate(string stableDirectory,string legacyDirectory,bool allowCreate) {
        Directory.CreateDirectory(stableDirectory);
        var path=BridgeIdentityFiles.CertificatePath(stableDirectory);
        // Keep the TLS identity above the live/demo data directories. The queue and
        // simulated profile remain isolated, but switching modes or replacing the
        // bridge executable must not silently create a new certificate.
        if(!File.Exists(path)) {
            // Prefer the live identity when migrating older installs, even if the
            // first post-update launch happens to be the demo executable.
            var legacyCandidates=BridgeIdentityFiles.LegacyCertificatePaths(stableDirectory,legacyDirectory);
            foreach(var legacy in legacyCandidates.Where(File.Exists)) {
                // Validate the old protected blob before adopting it. Copying the
                // protected bytes keeps the certificate and private key unchanged.
                var migratedProtectedPfx=File.ReadAllBytes(legacy);
                _=X509CertificateLoader.LoadPkcs12(ProtectedData.Unprotect(migratedProtectedPfx,null,DataProtectionScope.CurrentUser),null,X509KeyStorageFlags.UserKeySet);
                File.Copy(legacy,path);break;
            }
        }
        if(File.Exists(path))return X509CertificateLoader.LoadPkcs12(ProtectedData.Unprotect(File.ReadAllBytes(path),null,DataProtectionScope.CurrentUser),null,X509KeyStorageFlags.UserKeySet);
        if(!allowCreate)throw new InvalidOperationException("The paired Bridge certificate is missing. Restore %USERPROFILE%\\.phantom-dust-bridge\\certificate.dpapi (or provide --legacy-root on the first start); a paired Bridge will not generate a replacement identity.");
        using var rsa=RSA.Create(3072);var req=new CertificateRequest("CN=Phantom Dust Bridge",rsa,HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1);
        var san=new SubjectAlternativeNameBuilder();san.AddDnsName("localhost");san.AddDnsName(Environment.MachineName);san.AddIpAddress(IPAddress.Loopback);req.CertificateExtensions.Add(san.Build());
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false,false,0,true));
        using var certificate=req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1),DateTimeOffset.UtcNow.AddYears(3));
        var pfx=certificate.Export(X509ContentType.Pfx);var protectedPfx=ProtectedData.Protect(pfx,null,DataProtectionScope.CurrentUser);
        var temporary=path+".new";File.WriteAllBytes(temporary,protectedPfx);File.Move(temporary,path);
        return X509CertificateLoader.LoadPkcs12(pfx,null,X509KeyStorageFlags.UserKeySet);
    }
    public static CatalogueSkill[] Catalogue(){using var d=JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"skills.json")));return d.RootElement.GetProperty("skills").EnumerateArray().Select(x=>new CatalogueSkill(x.GetProperty("id").GetString()!,x.GetProperty("name").GetString()!,x.GetProperty("school").ValueKind==JsonValueKind.Null?null:x.GetProperty("school").GetString(),x.GetProperty("category").GetString()!)).ToArray();}
}
[ComImport,Guid("DCB00005-570F-4A9B-8D69-199FDBA5723B"),InterfaceType(ComInterfaceType.InterfaceIsDual)]
internal interface INetworkConnection {
    [return:MarshalAs(UnmanagedType.Interface)] object GetNetwork();
    bool IsConnectedToInternet {[return:MarshalAs(UnmanagedType.VariantBool)] get;}
    bool IsConnected {[return:MarshalAs(UnmanagedType.VariantBool)] get;}
    int GetConnectivity();
    Guid GetConnectionId();
    Guid GetAdapterId();
    int GetDomainType();
}
internal sealed class GameReader : IPhantomDustProfileReader {
    private const string PackageFamily = "Microsoft.MSEsper_1.3.25854.2_x64__8wekyb3d8bbwe";
    private const int ProfileBufferOffset = 0x004615BC;
    private const int ProfileRecord0Offset = 0x0059B094;
    private const int ProfileRecordStride = 0x388;
    private static string SessionFor(string executable,int pid,DateTime started)=>"process-v1:"+Planner.Hash($"{executable.ToLowerInvariant()}|{pid}|{started.ToUniversalTime().Ticks}");
    private readonly string bridgeId;
    public GameReader(string? bridgeId = null){this.bridgeId=bridgeId ?? "";}
    public Snapshot Read(){
        var processes=Process.GetProcessesByName("PDUWP");
        Snapshot Empty(string reason,string code="ProfileUnreadable",string? action=null)=>new("",null,Wire.Now,processes.Length>0,false,false,false,false,[],new Dictionary<string,int>(),new Dictionary<string,string>{{"mode","live-read-only"},{"verification",reason}},GameForeground:processes.Length==1&&WindowsSupport.IsForeground(processes[0]),ReadinessCode:code,RequiredAction:action??reason);
        try {
            if(processes.Length!=1)return Empty(processes.Length==0?"Open Phantom Dust.":"Close the extra Phantom Dust process before syncing.",processes.Length==0?"GameClosed":"MultipleGames");
            var process=processes[0];var module=process.MainModule??throw new InvalidOperationException("Cannot inspect game module.");
            var path=module.FileName;var expected=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"WindowsApps","Microsoft.MSEsper_1.3.25854.2_x64__8wekyb3d8bbwe","PDUWP.exe");
            if(!path.Equals(expected,StringComparison.OrdinalIgnoreCase))return Empty("Unsupported game package. No arsenals were read.");
            using var memory=new ReadOnlyMemory(process.Id);var baseAddress=(nuint)module.BaseAddress;
            var header=memory.Read(baseAddress,4096);var pe=BitConverter.ToInt32(header,0x3c);
            if(header[0]!=0x4d||header[1]!=0x5a||pe<0||pe>4000||BitConverter.ToUInt32(header,pe)!=0x4550||BitConverter.ToUInt16(header,pe+4)!=0x8664||BitConverter.ToUInt16(header,pe+24)!=0x20b)return Empty("Unsupported executable architecture/header.");
            var pointerBytes=memory.Read(baseAddress+0x003ED6B8,8);var pointer=BitConverter.ToUInt64(pointerBytes);
            if(pointer<0x10000||pointer>0x00007FFFFFFFFFFF)return Empty("Profile not loaded. Open the arsenal list and refresh.");
            // Read the current save-profile buffer and both logical save records.
            // Each is read twice to fail closed if the game is mutating profile state.
            var currentProfile=memory.Read(baseAddress+ProfileBufferOffset,ProfileIdentity.MatchStart+ProfileIdentity.MatchLength);
            var records=new[]{memory.Read(baseAddress+ProfileRecord0Offset,ProfileIdentity.RecordBytes),memory.Read(baseAddress+ProfileRecord0Offset+ProfileRecordStride,ProfileIdentity.RecordBytes)};
            var currentProfileAgain=memory.Read(baseAddress+ProfileBufferOffset,currentProfile.Length);
            var recordsAgain=new[]{memory.Read(baseAddress+ProfileRecord0Offset,ProfileIdentity.RecordBytes),memory.Read(baseAddress+ProfileRecord0Offset+ProfileRecordStride,ProfileIdentity.RecordBytes)};
            if(!currentProfile.SequenceEqual(currentProfileAgain)||!records.Zip(recordsAgain).All(x=>x.First.SequenceEqual(x.Second)))return Empty("Profile identity changed while reading. Refresh in the arsenal list.");
            var identity=ProfileIdentity.Resolve(bridgeId,PackageFamily,pointer!=0,currentProfile,records);
            if(!identity.Verified)return Empty(identity.Reason);
            var bytes=memory.Read((nuint)pointer,ArsenalReader.Length);
            var inventory=memory.Read((nuint)pointer+0x644,848);
            var again=memory.Read((nuint)pointer,ArsenalReader.Length);
            var inventoryAgain=memory.Read((nuint)pointer+0x644,848);
            var currentFinal=memory.Read(baseAddress+ProfileBufferOffset,currentProfile.Length);
            var recordsFinal=new[]{memory.Read(baseAddress+ProfileRecord0Offset,ProfileIdentity.RecordBytes),memory.Read(baseAddress+ProfileRecord0Offset+ProfileRecordStride,ProfileIdentity.RecordBytes)};
            if(!bytes.SequenceEqual(again)||!inventory.SequenceEqual(inventoryAgain)||!pointerBytes.SequenceEqual(memory.Read(baseAddress+0x003ED6B8,8))||!currentProfile.SequenceEqual(currentFinal)||!records.Zip(recordsFinal).All(x=>x.First.SequenceEqual(x.Second))||process.HasExited)return Empty("Game/profile data changed while reading. Refresh in the arsenal list.");
            var map=new SkillIdMap(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"skill-id-map.json")));
            var decks=ArsenalReader.Decode(bytes,map);
            // Live evidence: Bomb purchase 9→10, unrelated counters/cases unchanged,
            // native reload and profile isolation; see docs/SYNC_OVERHAUL_VERIFICATION.md.
            // Fail closed if the supported profile-layout routine differs.
            var inventoryLayoutVerified=memory.Read(baseAddress+0x15BCD0,126).SequenceEqual(Convert.FromHexString("40534883EC20488B81A0330000488BD9488D90440600004C8D802C0A0000488991383A00004883C0044C8981403A0000488981303A000033D2488D81A8330000488981503A0000488D81183B0000488981583A0000E806EAFFFF488D83B83F0000483983A0330000480F45C38B80CC190000890590EC43004883C4205BC3"));
            SnapshotCollection? collection=null;
            try { var ids=Enumerable.Range(1,374).Select(n=>n.ToString("D3")).ToArray(); var totals=ids.ToDictionary(id=>(string)id,id=>(int)inventory[BitConverter.ToUInt16(map.Encode(id))]); var owned=CollectionAccounting.FromTotals(totals,decks,ids); collection=new SnapshotCollection(inventoryLayoutVerified?CollectionStatus.Verified:CollectionStatus.Observed,owned.ToDictionary(x=>x.Key,x=>new CollectionEntry(x.Value.Total,x.Value.Free,x.Value.Allocations.Sum(a=>a.Copies),x.Value.Allocations))); } catch(InvalidDataException) { }
            var foreground=WindowsSupport.IsForeground(process);
            return new(identity.ProfileKey!,Planner.Hash(Convert.ToHexString(bytes)),Wire.Now,true,true,collection?.Status==CollectionStatus.Verified,true,false,decks,new Dictionary<string,int>(),new Dictionary<string,string>{{"mode","live-bounded-writer"},{"verification","Stable save-slot identity; inventory counters use verified total ownership accounting."}},decks.Length>0,false,SessionFor(path,process.Id,process.StartTime.ToUniversalTime()),collection,identity.Slot,identity.DisplayName,"GameSaveSlot",ProfileImportSupported:true,ArsenalNameMaxLength:ArsenalNamePolicy.MaxLength,GameForeground:foreground,ReadinessCode:collection?.Status==CollectionStatus.Verified?"Ready":"InventoryUnverified",RequiredAction:collection?.Status==CollectionStatus.Verified?null:"Skill quantities could not be verified. Update the Bridge and reopen the profile.");
        }catch(Exception e)when(e is InvalidOperationException or InvalidDataException or System.ComponentModel.Win32Exception or UnauthorizedAccessException or IOException){return Empty("Cannot read arsenals: "+e.Message);}
        finally{foreach(var p in processes)p.Dispose();}
    }
    public object Diagnostic() {
        var snapshots=new List<object>();
        foreach(var process in Process.GetProcessesByName("PDUWP"))using(process){
            try {
                var module=process.MainModule??throw new InvalidOperationException("Cannot inspect game module.");
                var path=module.FileName;var package=Path.GetFileName(Path.GetDirectoryName(path));
                // Diagnostic-only exact package gate. Never promotes these candidate bytes to an authoritative snapshot.
                if(package!="Microsoft.MSEsper_1.3.25854.2_x64__8wekyb3d8bbwe"||!path.StartsWith(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"WindowsApps")+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("Unsupported package location/version.");
                using var memory=new ReadOnlyMemory(process.Id);var baseAddress=(nuint)module.BaseAddress;var pointer=BitConverter.ToUInt64(memory.Read(baseAddress+0x003ED6B8,8));
                if(pointer<0x10000||pointer>0x00007FFFFFFFFFFF)throw new InvalidOperationException("Implausible candidate pointer.");
                var currentProfile=memory.Read(baseAddress+ProfileBufferOffset,ProfileIdentity.RecordBytes);var profileRecords=new[]{memory.Read(baseAddress+ProfileRecord0Offset,ProfileIdentity.RecordBytes),memory.Read(baseAddress+ProfileRecord0Offset+ProfileRecordStride,ProfileIdentity.RecordBytes)};
                var identity=ProfileIdentity.Resolve("diagnostic",PackageFamily,true,currentProfile,profileRecords);
                var profileDifferences=profileRecords.Select(record=>Enumerable.Range(ProfileIdentity.MatchStart,ProfileIdentity.RecordBytes-ProfileIdentity.MatchStart).Where(offset=>currentProfile[offset]!=record[offset]).ToArray()).ToArray();
                var arsenals=memory.Read((nuint)pointer,0x644);var inventory=memory.Read((nuint)pointer+0x644,848);
                var map=new SkillIdMap(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"skill-id-map.json")));var decoded=new List<object>();
                for(var slot=0;slot<16;slot++){try{var start=slot*0x64;decoded.Add(new{slot=slot+1,name=System.Text.Encoding.ASCII.GetString(arsenals,start+8,16).TrimEnd('\0'),caseWord=BitConverter.ToUInt16(arsenals,start+0x54),skillIds=map.DecodeArsenal(arsenals.Skip(start+0x18).Take(60).ToArray()),verified=false});}catch(InvalidDataException){decoded.Add(new{slot=slot+1,error="Unknown binary skill or unallocated record",verified=false});}}
                string? executableSha256=null;try{using var file=File.OpenRead(path);executableSha256=Convert.ToHexString(SHA256.HashData(file));}catch(UnauthorizedAccessException){} snapshots.Add(new{package,processStart=process.StartTime.ToUniversalTime(),executableSha256,profileIdentityCandidate=new{identity.Verified,identity.Slot,identity.DisplayName,identity.Reason,differenceOffsets=profileDifferences,currentBytes=Convert.ToHexString(currentProfile),storedRecordBytes=profileRecords.Select(Convert.ToHexString).ToArray()},candidateArsenals=decoded,candidateArsenalBytes=Convert.ToHexString(arsenals),candidateInventoryBytes=Convert.ToHexString(inventory),verified=false});
            }catch(Exception e){snapshots.Add(new{verified=false,error=e.Message});}
        }
        return new{capturedAt=Wire.Now,writeSupported=false,warning="UNVERIFIED bounded source-derived candidates. Never use as authoritative inventory.",processes=snapshots};
    }
}

// Normal mode writer. It re-discovers the one supported process and pointer for
// every job, then writes only the selected record fields while the process is
// suspended. The target bytes and profile identity are rechecked immediately
// before mutation so a stale phone confirmation cannot hit a new save.
internal sealed class GameWriter(IPhantomDustProfileReader reader) {
    private const int PointerOffset=0x003ED6B8;
    private const int ProfileManagerOffset=0x004BEC10;
    private readonly SkillIdMap map=new(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"skill-id-map.json")));
    private static bool SameArsenal(string name,int caseCapacity,IReadOnlyList<string> ids,SnapshotDeck? target)=>target is not null&&target.Name==name&&target.CaseCapacity>=caseCapacity&&Planner.CardContentFingerprint(target.SkillIds)==Planner.CardContentFingerprint(ids);
    public bool ProbeFrameReady(){var processes=Process.GetProcessesByName("PDUWP");try{if(processes.Length!=1)return false;var process=processes[0];var module=process.MainModule;if(module is null||!module.FileName.Equals(SupportedExecutablePath,StringComparison.OrdinalIgnoreCase))return false;var result=NativeGameSaveRequester.ProbeFrame(process,(nuint)module.BaseAddress);return result==NativeFrameProbeResult.Ready;}catch{return false;}finally{foreach(var process in processes)process.Dispose();}}
    private static string SupportedExecutablePath=>Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"WindowsApps","Microsoft.MSEsper_1.3.25854.2_x64__8wekyb3d8bbwe","PDUWP.exe");
    public WriteResult Apply(QueueRequest request,Snapshot expected,Action<string,string>? progress=null){
        var processes=Process.GetProcessesByName("PDUWP");
        try{
            if(processes.Length!=1)return new(JobState.Blocked,Reason:"Open Phantom Dust and load your profile.");
            var process=processes[0];var module=process.MainModule??throw new InvalidOperationException();
            if(!module.FileName.Equals(SupportedExecutablePath,StringComparison.OrdinalIgnoreCase))return new(JobState.Blocked,Reason:"This version of Phantom Dust is not supported.");
            using var memory=new WritableMemory(process.Id);var moduleBase=(nuint)module.BaseAddress;var address=moduleBase+PointerOffset;var pointer=BitConverter.ToUInt64(memory.Read(address,8));var profileManager=BitConverter.ToUInt64(memory.Read(moduleBase+ProfileManagerOffset,8));
            if(pointer<0x10000||pointer>0x00007FFFFFFFFFFF)return new(JobState.Blocked,Reason:"Load your Phantom Dust profile.");
            if(profileManager<0x10000||pointer!=profileManager+0x3FB8)return new(JobState.Blocked,Reason:"The loaded Phantom Dust profile layout is not supported.");
            var root=(nuint)pointer;var original=memory.Read(root,ArsenalReader.Length);var inventory=memory.Read(root+0x644,848);
            var beforeWrite=true;
            static bool SameCollection(SnapshotCollection? a,SnapshotCollection? b){
                if(a is null||b is null)return a is null&&b is null;
                if(a.Status!=b.Status||a.Skills.Count!=b.Skills.Count)return false;
                foreach(var (id,left) in a.Skills){if(!b.Skills.TryGetValue(id,out var right)||left.Total!=right.Total||left.Free!=right.Free||left.Assigned!=right.Assigned||!left.Allocations.SequenceEqual(right.Allocations))return false;}
                return true;
            }
            bool SameContext(){
                if(process.HasExited||BitConverter.ToUInt64(memory.Read(address,8))!=pointer)return false;
                var current=reader.Read();
                if(!current.ProfileVerified||current.ProfileKey!=expected.ProfileKey||current.SessionKey!=expected.SessionKey||current.GameProfileDisplayName!=expected.GameProfileDisplayName)return false;
                // The planner snapshot is the reservation boundary. Re-read all
                // ownership evidence immediately before mutation so a purchase,
                // arsenal edit, or profile refresh cannot race the write.
                if(beforeWrite && !SameCollection(expected.Collection,current.Collection))return false;
                if(beforeWrite && expected.InventoryVerified && (!current.InventoryVerified||!expected.FreeInventory.OrderBy(x=>x.Key).SequenceEqual(current.FreeInventory.OrderBy(x=>x.Key))))return false;
                return true;
            }
            progress?.Invoke("UpdatingPcArsenal","Updating the PC arsenal.");
            var result=ArsenalMemoryWriter.Apply(memory,root,original,inventory,request,map,SameContext);
            beforeWrite=false;
            if(result.State!=JobState.AppliedAwaitingGameSave)return result;
            try{
            var diskBefore=GameSaveCommitProbe.Capture();
            progress?.Invoke("SavingInGame","Saving in Phantom Dust.");
            var saveRequest=NativeGameSaveRequester.Request(process,moduleBase,(nuint)profileManager,()=>{
                if(!SameContext())return false;var current=reader.Read();var target=current.Arsenals.FirstOrDefault(x=>x.Slot==request.TargetSlot);
                return SameArsenal(request.Deck.Name,request.Deck.CaseCapacity,request.Deck.SkillIds,target);
            });
            progress?.Invoke("VerifyingSave","Verifying the Phantom Dust save.");
            if(saveRequest==NativeSaveRequestResult.Requested&&GameSaveCommitProbe.WaitForChange(diskBefore,30000)){
                var committed=reader.Read();var target=committed.Arsenals.FirstOrDefault(x=>x.Slot==request.TargetSlot);
                if(committed.ProfileVerified&&committed.ProfileKey==expected.ProfileKey&&committed.SessionKey==expected.SessionKey&&
                   SameArsenal(request.Deck.Name,request.Deck.CaseCapacity,request.Deck.SkillIds,target))
                    return result with{State=JobState.Completed,Reason="Phantom Dust committed the profile save."};
                return new(JobState.RecoveryRequired,result.Changes,"Phantom Dust saved, but the Arsenal changed during the save. Reload the profile and choose which version to keep.");
            }
            if(saveRequest==NativeSaveRequestResult.Requested)return new(JobState.RecoveryRequired,result.Changes,"Phantom Dust returned from the save request, but profile.dat did not change. Reload the profile and choose which version to keep.");
            if(saveRequest==NativeSaveRequestResult.Uncertain)return new(JobState.RecoveryRequired,result.Changes,"The game save request may have started but could not be confirmed. Restart Phantom Dust and review this Arsenal.");
            var rollbackMessage=saveRequest switch {
                NativeSaveRequestResult.QueueRejected=>"The game did not accept the save request (it may be busy), and no PC changes were kept. Retry when the game is ready.",
                NativeSaveRequestResult.SaveSuppressed=>"The game is not currently accepting profile saves, and no PC changes were kept. Open My Arsenals, focus the game, and retry.",
                _=>"Phantom Dust is paused or minimized. Restore the game window, then retry. No PC changes were kept."
            };
            // Restore the exact fields written above so a later retry starts from
            // the confirmed PC fingerprint.
            var start=(request.TargetSlot-1)*0x64;var intended=original.ToArray();Array.Clear(intended,start+8,16);System.Text.Encoding.ASCII.GetBytes(request.Deck.Name).CopyTo(intended,start+8);request.Deck.SkillIds.SelectMany(map.Encode).ToArray().CopyTo(intended,start+0x18);
            var suspended=false;WriteResult rollbackResult=new(JobState.RecoveryRequired,result.Changes,"The save request could not be confirmed; the PC write outcome is uncertain. Review the current PC arsenal.");try{
                memory.Suspend();suspended=true;
                if(SameContext()&&memory.Read(root,ArsenalReader.Length).SequenceEqual(intended)&&memory.Read(root+0x644,848).SequenceEqual(inventory)){
                    memory.Write(root+(nuint)(start+8),original.AsSpan(start+8,16).ToArray());memory.Write(root+(nuint)(start+0x18),original.AsSpan(start+0x18,60).ToArray());
                    if(memory.Read(root,ArsenalReader.Length).SequenceEqual(original))rollbackResult=new(JobState.WaitingForFrames,result.Changes,rollbackMessage);
                    else rollbackResult=new(JobState.RecoveryRequired,result.Changes,"The save request could not be confirmed. Restart Phantom Dust and review this Arsenal.");
                }else{
                    rollbackResult=new(JobState.RecoveryRequired,result.Changes,"The save request could not be confirmed. Restart Phantom Dust and review this Arsenal.");
                }
            }catch(Exception e){rollbackResult=new(JobState.RecoveryRequired,result.Changes,$"The PC arsenal could not be restored safely ({e.Message}). Restart Phantom Dust and review this Arsenal.");}
            finally{if(suspended){try{if(!memory.Resume())rollbackResult=new(JobState.RecoveryRequired,result.Changes,"Phantom Dust could not resume. Restart the game and review this Arsenal.");}catch(Exception e){rollbackResult=new(JobState.RecoveryRequired,result.Changes,$"Phantom Dust could not resume safely ({e.Message}). Restart the game and review this Arsenal.");}}}
            return rollbackResult;
            }catch(Exception e){return new(JobState.RecoveryRequired,result.Changes,$"The PC write outcome is uncertain ({e.Message}). Restart Phantom Dust and review this Arsenal.");}
        }catch(Exception e)when(e is InvalidDataException or IOException or InvalidOperationException or System.ComponentModel.Win32Exception){return new(JobState.Blocked,Reason:"Couldn’t read Phantom Dust. Open the arsenal list and try again.");}
        finally{foreach(var p in processes)p.Dispose();}
    }
    private sealed class WritableMemory(int id):IArsenalMemory,IDisposable {
        private readonly IntPtr handle=OpenProcess(0x1838,false,id);
        public byte[] Read(nuint a,int n){var b=new byte[n];if(handle==IntPtr.Zero||!ReadProcessMemory(handle,a,b,(nuint)n,out var r)||r!=(nuint)n)throw new IOException("Incomplete game memory read.");return b;}
        public void Write(nuint a,byte[] b){if(!WriteProcessMemory(handle,a,b,(nuint)b.Length,out var w)||w!=(nuint)b.Length)throw new IOException("Incomplete game memory write.");}
        public void Suspend(){if(NtSuspendProcess(handle)!=0)throw new IOException("Cannot suspend Phantom Dust.");}
        public bool Resume()=>NtResumeProcess(handle)==0;
        public void Dispose()=>CloseHandle(handle);
        [DllImport("kernel32.dll")]static extern IntPtr OpenProcess(uint a,bool i,int p);[DllImport("kernel32.dll")]static extern bool ReadProcessMemory(IntPtr h,nuint a,byte[] b,nuint n,out nuint r);[DllImport("kernel32.dll")]static extern bool WriteProcessMemory(IntPtr h,nuint a,byte[] b,nuint n,out nuint w);[DllImport("kernel32.dll")]static extern bool CloseHandle(IntPtr h);[DllImport("ntdll.dll")]static extern int NtSuspendProcess(IntPtr h);[DllImport("ntdll.dll")]static extern int NtResumeProcess(IntPtr h);
    }
}
internal static class GameSaveCommitProbe {
    private static string Root=>Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Packages","Microsoft.MSEsper_8wekyb3d8bbwe","SystemAppData","wgs");
    public static HashSet<string> Capture(){try{return Directory.EnumerateFiles(Root,"*",SearchOption.AllDirectories).Where(path=>new FileInfo(path).Length==20544).Select(path=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))).ToHashSet(StringComparer.Ordinal);}catch{return [];}}
    public static bool WaitForChange(HashSet<string> before,int timeoutMs){if(before.Count==0)return false;var deadline=Environment.TickCount64+timeoutMs;do{Thread.Sleep(100);var current=Capture();if(current.Count>0&&!current.SetEquals(before))return true;}while(Environment.TickCount64<deadline);return false;}
}
internal sealed class ReadOnlyMemory : IProcessMemory, IDisposable {
    private readonly IntPtr handle;
    internal IntPtr Handle=>handle;
    public ReadOnlyMemory(int id){handle=OpenProcess(0x0410,false,id);if(handle==IntPtr.Zero)throw new InvalidOperationException("Cannot open process for reading.");}
    public byte[] Read(nuint address,int length){if(length is <1 or >4096)throw new ArgumentOutOfRangeException(nameof(length));var data=new byte[length];if(!ReadProcessMemory(handle,address,data,(nuint)length,out var read)||read!=(nuint)length)throw new InvalidOperationException("Incomplete or inaccessible memory read.");return data;}
    public byte[] ReadLarge(nuint address,int length){if(length is <1 or >64*1024*1024)throw new ArgumentOutOfRangeException(nameof(length));var data=new byte[length];for(var offset=0;offset<length;offset+=4096){var size=Math.Min(4096,length-offset);var chunk=new byte[size];if(!ReadProcessMemory(handle,address+(nuint)offset,chunk,(nuint)size,out var read)||read!=(nuint)size)throw new InvalidOperationException("Incomplete or inaccessible memory read.");Buffer.BlockCopy(chunk,0,data,offset,size);}return data;}
    public void Dispose()=>CloseHandle(handle);
    [DllImport("kernel32.dll",SetLastError=true)] private static extern IntPtr OpenProcess(uint access,bool inherit,int id);
    [DllImport("kernel32.dll",SetLastError=true)] private static extern bool ReadProcessMemory(IntPtr h,nuint address,byte[] buffer,nuint size,out nuint read);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);
}
internal sealed class DemoReader(CatalogueSkill[] skills) : IPhantomDustProfileReader {
    public Snapshot Read(){var decks=Enumerable.Range(1,3).Select(slot=>{var ids=Enumerable.Repeat("000",30).ToArray();if(slot==2){ids[0]="001";ids[1]="001";}var name=$"DEMO Arsenal {slot}";return new SnapshotDeck(slot,name,slot,ids,Planner.TargetFingerprint(slot,name,slot,ids),Planner.CardContentFingerprint(ids));}).ToArray();return new("demo-profile",Planner.Hash(string.Join(',',decks.Select(x=>x.Fingerprint))),Wire.Now,true,true,true,false,true,decks,skills.Where(x=>x.Id!="000").ToDictionary(x=>x.Id,x=>x.Id=="001"?1:3),new Dictionary<string,string>{{"mode","demo"},{"warning","Simulated collection. Never accesses Phantom Dust."}},ProfileImportSupported:true);}
}

