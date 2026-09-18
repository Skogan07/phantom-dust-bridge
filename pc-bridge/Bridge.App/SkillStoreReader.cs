using System.Diagnostics;
using System.Runtime.InteropServices;
using PhantomDust.PcBridge.Core;

namespace PhantomDust.PcBridge.App;

// Reader for the released retail PDUWP build. It never writes game memory and
// promotes a result only when exactly one stable, catalogue-valid layout exists.
internal sealed class LiveSkillStoreReader : IPhantomDustSkillStoreReader {
    private const string Package="Microsoft.MSEsper_1.3.25854.2_x64__8wekyb3d8bbwe";
    private const string Executable="PDUWP.exe";
    private const int MarkerOffset=0xB4;
    private const nuint RetailAllocationOffset=0x624E78;
    private readonly IPhantomDustProfileReader profileReader;private readonly SkillIdMap map;private readonly IReadOnlyDictionary<string,CatalogueSkill> catalogue;private readonly string expectedPath;
    private (int Pid,long Start,nuint Address)? cachedCandidate;
    public LiveSkillStoreReader(IPhantomDustProfileReader profileReader,string cataloguePath,string mapPath){this.profileReader=profileReader;map=new SkillIdMap(File.ReadAllText(mapPath));using var document=System.Text.Json.JsonDocument.Parse(File.ReadAllText(cataloguePath));catalogue=document.RootElement.GetProperty("skills").EnumerateArray().Select(x=>new CatalogueSkill(x.GetProperty("id").GetString()!,x.GetProperty("name").GetString()!,x.GetProperty("school").ValueKind==System.Text.Json.JsonValueKind.Null?null:x.GetProperty("school").GetString(),x.GetProperty("category").GetString()!)).ToDictionary(x=>x.Id,StringComparer.Ordinal);expectedPath=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"WindowsApps",Package,Executable);}
    public bool Supported=>true;
    public SkillStoreResult Read(){
        var processes=Process.GetProcessesByName("PDUWP");
        try {
            if(processes.Length==0)return new("",SkillStoreState.GameClosed,Matches:[]);
            if(processes.Length!=1)return new("",SkillStoreState.Unsupported,Matches:[]);
            using var process=processes[0];var module=process.MainModule;
            if(module is null||!module.FileName.Equals(expectedPath,StringComparison.OrdinalIgnoreCase))return new("",SkillStoreState.Unsupported,Matches:[]);
            var profile=profileReader.Read();if(!profile.GameRunning)return new(profile.ProfileKey,SkillStoreState.GameClosed,Matches:[]);
            if(!profile.ProfileVerified||string.IsNullOrWhiteSpace(profile.ProfileKey))return new("",SkillStoreState.Unsupported,Matches:[]);
            using var memory=new ReadOnlyMemory(process.Id);
            var session=(process.Id,process.StartTime.ToUniversalTime().Ticks);
            var candidates=new List<nuint>();
            if(cachedCandidate is { } stale&& (stale.Pid!=session.Id||stale.Start!=session.Ticks))cachedCandidate=null;
            if(cachedCandidate is { } cached&&cached.Pid==session.Id&&cached.Start==session.Ticks){try{_ = SkillStoreMemoryParser.Parse(ReadBlocks(memory,cached.Address),0,"candidate",0,map,catalogue);candidates.Add(cached.Address);}catch(Exception e)when(e is InvalidDataException or InvalidOperationException or IOException){cachedCandidate=null;}}
            if(candidates.Count==0){candidates=FindCandidates(process,memory,map,catalogue);if(candidates.Count==1)cachedCandidate=(session.Id,session.Ticks,candidates[0]);}
            if(candidates.Count!=1)return new(profile.ProfileKey,SkillStoreState.Unsupported,Matches:[]);
            var address=candidates[0];var first=ReadBlocks(memory,address);var marker=memory.Read(address-(nuint)MarkerOffset,1)[0];
            var again=ReadBlocks(memory,address);var markerAgain=memory.Read(address-(nuint)MarkerOffset,1)[0];
            if(marker!=markerAgain||!first.Zip(again).All(x=>x.First.SequenceEqual(x.Second)))return new(profile.ProfileKey,SkillStoreState.Unsupported,Matches:[]);
            var profileAgain=profileReader.Read();if(!profileAgain.ProfileVerified||profileAgain.ProfileKey!=profile.ProfileKey)return new("",SkillStoreState.Unsupported,Matches:[]);
            return SkillStoreMemoryParser.Parse(first,marker,profile.ProfileKey,Wire.Now,map,catalogue);
        }catch(Exception e)when(e is IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception){return new("",SkillStoreState.Unsupported,Matches:[]);}
        finally{foreach(var p in processes)p.Dispose();}
    }
    private static List<nuint> FindCandidates(Process process,ReadOnlyMemory memory,SkillIdMap map,IReadOnlyDictionary<string,CatalogueSkill> catalogue){
        var regions=new List<MEMORY_BASIC_INFORMATION>();var allocations=new HashSet<nuint>();var info=new MEMORY_BASIC_INFORMATION();var address=nuint.Zero;
        while(VirtualQueryEx(memory.Handle,address,out info,(nuint)Marshal.SizeOf<MEMORY_BASIC_INFORMATION>())!=0){
            if(info.State==0x1000&&info.Type==0x20000){allocations.Add(info.AllocationBase);if(info.Protect==0x04)regions.Add(info);}
            var next=info.BaseAddress+info.RegionSize;if(next<=address)break;address=next;
        }
        var list=new List<nuint>();var layout=(nuint)(6*SkillStoreMemoryParser.BlockSize);foreach(var allocation in allocations){var candidate=allocation+RetailAllocationOffset;var region=regions.FirstOrDefault(r=>candidate>=r.BaseAddress&&candidate<r.BaseAddress+r.RegionSize);if(region.RegionSize==0||candidate<(region.BaseAddress+(nuint)MarkerOffset)||candidate+layout>region.BaseAddress+region.RegionSize)continue;try{_ = SkillStoreMemoryParser.Parse(ReadBlocks(memory,candidate),0,"candidate",0,map,catalogue);if(!list.Contains(candidate))list.Add(candidate);}catch(Exception e)when(e is InvalidDataException or InvalidOperationException or IOException){}if(list.Count>1)break;}
        return list;
    }
    private static byte[][] ReadBlocks(ReadOnlyMemory memory,nuint address)=>Enumerable.Range(0,6).Select(i=>memory.Read(address+(nuint)(i*SkillStoreMemoryParser.BlockSize),SkillStoreMemoryParser.BlockSize)).ToArray();
    [StructLayout(LayoutKind.Sequential)] private struct MEMORY_BASIC_INFORMATION { public nuint BaseAddress;public nuint AllocationBase;public uint AllocationProtect;public nuint RegionSize;public uint State;public uint Protect;public uint Type; }
    [DllImport("kernel32.dll",SetLastError=true)] private static extern nuint VirtualQueryEx(IntPtr process,nuint address,out MEMORY_BASIC_INFORMATION info,nuint length);
}
