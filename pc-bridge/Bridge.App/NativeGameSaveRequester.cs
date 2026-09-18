using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PhantomDust.PcBridge.App;

internal enum NativeSaveRequestResult { NotRequested, Requested, QueueRejected, SaveSuppressed, Uncertain }
internal enum NativeFrameProbeResult { NotReady, Ready, Uncertain }
internal enum NativeSaveCompletionStatus : byte { Pending=0, Accepted=1, Rejected=2, Suppressed=3, Invalid=255 }

// Requests the profile-save function Phantom Dust uses after an Arsenal edit.
// The handoff occurs only at the entry to the game's normal frame callback,
// where the Win64 calling convention allows volatile registers to be changed.
internal static class NativeGameSaveRequester {
    private const int FrameCallbackOffset=0x001CAC50;
    private const int PrepareArsenalsOffset=0x0015A730;
    private const int ProfileSaveOffset=0x001D01B0;
    private const int SaveSuppressionFlagsOffset=0x00530034;
    private static readonly byte[] ExpectedFramePrefix=[0x48,0x89,0x5C,0x24,0x08,0x48,0x89,0x74,0x24,0x10,0x57,0x48,0x83,0xEC,0x20];
    private static readonly byte[] ExpectedPreparePrefix=[0x48,0x89,0x6C,0x24,0x18,0x48,0x89,0x7C,0x24,0x20,0x41,0x54,0x41,0x56,0x41,0x57];
    private static readonly byte[] ExpectedSavePrefix=[0x48,0x83,0xEC,0x28,0xE8,0x67,0xED,0xEE,0xFF,0xE8,0x72,0xFF,0xFF,0xFF,0x33,0xC0];
    private const uint DbgContinue=0x00010002,DbgExceptionNotHandled=0x80010001;
    private const uint ExceptionDebugEvent=1,Breakpoint=0x80000003,ContextAll=0x0010001F;
    private const int ContextSize=1232,ContextFlagsOffset=48,RipOffset=248;

    // A probe uses the same callback breakpoint as the save handoff, but never
    // allocates a thunk or calls any game function. It only proves that a real
    // frame boundary can be reached, then restores the byte and RIP.
    internal static NativeFrameProbeResult ProbeFrame(Process process,nuint moduleBase,int timeoutMs=750) {
        using var handle=new NativeHandle(OpenProcess(0x1F0FFF,false,process.Id));
        if(handle.Value==IntPtr.Zero)return NativeFrameProbeResult.Uncertain;
        var frame=moduleBase+FrameCallbackOffset;var original=Array.Empty<byte>();var patched=false;var attached=false;var pending=false;var pid=0;var tid=0;var address=(nuint)0;
        try {
            if(!Read(handle.Value,frame,ExpectedFramePrefix.Length).SequenceEqual(ExpectedFramePrefix))return NativeFrameProbeResult.NotReady;
            original=Read(handle.Value,frame,1);if(!DebugActiveProcess(process.Id))return NativeFrameProbeResult.NotReady;attached=true;
            if(!DebugSetProcessKillOnExit(false))return NativeFrameProbeResult.Uncertain;
            var ev=Marshal.AllocHGlobal(176);try {Write(handle.Value,frame,[0xCC]);FlushInstructionCache(handle.Value,(IntPtr)frame,1);patched=true;
                var deadline=Environment.TickCount64+timeoutMs;while(Environment.TickCount64<deadline){if(!WaitForDebugEvent(ev,100))continue;pending=true;pid=Marshal.ReadInt32(ev,4);tid=Marshal.ReadInt32(ev,8);var code=(uint)Marshal.ReadInt32(ev);var status=DbgContinue;address=0;
                    if(code==ExceptionDebugEvent){var exception=(uint)Marshal.ReadInt32(ev,16);address=(nuint)Marshal.ReadInt64(ev,32);if(exception==Breakpoint&&address==frame){using var thread=new NativeHandle(OpenThread(0x001A,false,tid));using var context=new NativeBuffer(ContextSize);Marshal.WriteInt32(context.Value,ContextFlagsOffset,(int)ContextAll);if(thread.Value==IntPtr.Zero||!GetThreadContext(thread.Value,context.Value))return NativeFrameProbeResult.Uncertain;var rip=(nuint)Marshal.ReadInt64(context.Value,RipOffset);if(rip!=frame+1)return NativeFrameProbeResult.Uncertain;Write(handle.Value,frame,original);FlushInstructionCache(handle.Value,(IntPtr)frame,1);patched=false;Marshal.WriteInt64(context.Value,RipOffset,(long)frame);if(!SetThreadContext(thread.Value,context.Value))return NativeFrameProbeResult.Uncertain;ContinueDebugEvent(pid,tid,DbgContinue);pending=false;return NativeFrameProbeResult.Ready;}status=exception==Breakpoint?DbgContinue:DbgExceptionNotHandled;}
                    ContinueDebugEvent(pid,tid,status);pending=false;
                }return NativeFrameProbeResult.NotReady;
            } finally {Marshal.FreeHGlobal(ev);}
        } catch{return NativeFrameProbeResult.Uncertain;} finally {if(patched&&original.Length==1){try{Write(handle.Value,frame,original);FlushInstructionCache(handle.Value,(IntPtr)frame,1);}catch{}}if(pending){if(address==frame)TrySetRip(tid,frame);ContinueDebugEvent(pid,tid,DbgContinue);}if(attached)DebugActiveProcessStop(process.Id);}
    }

    public static NativeSaveRequestResult Request(Process process,nuint moduleBase,nuint profileManager,Func<bool> contextMatches,int timeoutMs=3000) {
        var handle=OpenProcess(0x1F0FFF,false,process.Id);
        if(handle==IntPtr.Zero)return NativeSaveRequestResult.NotRequested;
        var frame=moduleBase+FrameCallbackOffset;var prepare=moduleBase+PrepareArsenalsOffset;var save=moduleBase+ProfileSaveOffset;
        var original=Array.Empty<byte>();var attached=false;var patched=false;var pending=false;
        var pendingPid=0;var pendingTid=0;var pendingAddress=(nuint)0;
        var result=NativeSaveRequestResult.NotRequested;IntPtr allocation=IntPtr.Zero;var redirected=false;
        try {
            if(!Read(handle,frame,ExpectedFramePrefix.Length).SequenceEqual(ExpectedFramePrefix)||
               !Read(handle,prepare,ExpectedPreparePrefix.Length).SequenceEqual(ExpectedPreparePrefix)||
               !Read(handle,save,ExpectedSavePrefix.Length).SequenceEqual(ExpectedSavePrefix))return result;
            original=Read(handle,frame,1);
            if(!DebugActiveProcess(process.Id))return result;
            attached=true;if(!DebugSetProcessKillOnExit(false))return result;
            Write(handle,frame,[0xCC]);FlushInstructionCache(handle,(IntPtr)frame,1);patched=true;
            var debugEvent=Marshal.AllocHGlobal(176);
            try {
                var deadline=Environment.TickCount64+timeoutMs;
                while(Environment.TickCount64<deadline) {
                    if(!WaitForDebugEvent(debugEvent,100))continue;
                    pending=true;pendingPid=Marshal.ReadInt32(debugEvent,4);pendingTid=Marshal.ReadInt32(debugEvent,8);
                    var code=(uint)Marshal.ReadInt32(debugEvent);var status=DbgContinue;pendingAddress=0;
                    if(code==ExceptionDebugEvent) {
                        var exception=(uint)Marshal.ReadInt32(debugEvent,16);pendingAddress=(nuint)Marshal.ReadInt64(debugEvent,32);
                        if(exception==Breakpoint&&pendingAddress==frame) {
                            using var thread=new NativeHandle(OpenThread(0x001A,false,pendingTid));if(thread.Value==IntPtr.Zero)throw new IOException();
                            using var context=new NativeBuffer(ContextSize);Marshal.WriteInt32(context.Value,ContextFlagsOffset,(int)ContextAll);
                            if(!GetThreadContext(thread.Value,context.Value)||(nuint)Marshal.ReadInt64(context.Value,RipOffset)!=frame+1)throw new IOException();
                            Write(handle,frame,original);FlushInstructionCache(handle,(IntPtr)frame,1);patched=false;
                            if(!contextMatches()) {
                                Marshal.WriteInt64(context.Value,RipOffset,(long)frame);if(!SetThreadContext(thread.Value,context.Value))throw new IOException();
                                ContinueDebugEvent(pendingPid,pendingTid,DbgContinue);pending=false;return result;
                            }
                            // Keep code and completion data on separate pages. The
                            // thunk marks the writable page after the native save
                            // returns, then jumps back to the frame callback.
                            allocation=VirtualAllocEx(handle,IntPtr.Zero,0x2000,0x3000,0x04);if(allocation==IntPtr.Zero)throw new IOException();
                            var completion=(nuint)allocation+0x1000;var suppressionFlags=moduleBase+SaveSuppressionFlagsOffset;var thunk=BuildThunk(prepare,save,frame,completion,profileManager,suppressionFlags);Write(handle,(nuint)allocation,thunk);
                            if(!VirtualProtectEx(handle,allocation,0x1000,0x20,out _))throw new IOException();FlushInstructionCache(handle,allocation,(nuint)thunk.Length);
                            Marshal.WriteInt64(context.Value,RipOffset,allocation.ToInt64());if(!SetThreadContext(thread.Value,context.Value))throw new IOException();
                            redirected=true;ContinueDebugEvent(pendingPid,pendingTid,DbgContinue);pending=false;
                            var completionDeadline=Environment.TickCount64+10000;var completionStatus=NativeSaveCompletionStatus.Pending;
                            while(Environment.TickCount64<completionDeadline) {
                                completionStatus=DecodeCompletionStatus(Read(handle,completion,1)[0]);
                                if(completionStatus!=NativeSaveCompletionStatus.Pending)break;
                                Thread.Sleep(10);
                            }
                            result=DecodeRequestResult(completionStatus);
                            if(result==NativeSaveRequestResult.Requested)Thread.Sleep(10);
                            // The small immutable mapping remains for this game
                            // process so it cannot be freed while RIP is returning.
                            allocation=IntPtr.Zero;return result;
                        }
                        status=exception==Breakpoint?DbgContinue:DbgExceptionNotHandled;
                    }
                    ContinueDebugEvent(pendingPid,pendingTid,status);pending=false;
                }
            } finally { Marshal.FreeHGlobal(debugEvent); }
            return result;
        } catch { return redirected?NativeSaveRequestResult.Uncertain:result; }
        finally {
            if(patched&&original.Length==1){try{Write(handle,frame,original);FlushInstructionCache(handle,(IntPtr)frame,1);}catch{}}
            if(pending){if(pendingAddress==frame&&result==NativeSaveRequestResult.NotRequested)TrySetRip(pendingTid,frame);ContinueDebugEvent(pendingPid,pendingTid,DbgContinue);}
            if(attached)DebugActiveProcessStop(process.Id);
            if(allocation!=IntPtr.Zero&&!redirected)VirtualFreeEx(handle,allocation,0,0x8000);
            CloseHandle(handle);
        }
    }

    internal static NativeSaveCompletionStatus DecodeCompletionStatus(byte status)=>status switch {
        (byte)NativeSaveCompletionStatus.Pending=>NativeSaveCompletionStatus.Pending,
        (byte)NativeSaveCompletionStatus.Accepted=>NativeSaveCompletionStatus.Accepted,
        (byte)NativeSaveCompletionStatus.Rejected=>NativeSaveCompletionStatus.Rejected,
        (byte)NativeSaveCompletionStatus.Suppressed=>NativeSaveCompletionStatus.Suppressed,
        _=>NativeSaveCompletionStatus.Invalid
    };
    internal static NativeSaveRequestResult DecodeRequestResult(NativeSaveCompletionStatus status)=>status switch {
        NativeSaveCompletionStatus.Accepted=>NativeSaveRequestResult.Requested,
        NativeSaveCompletionStatus.Rejected=>NativeSaveRequestResult.QueueRejected,
        NativeSaveCompletionStatus.Suppressed=>NativeSaveRequestResult.SaveSuppressed,
        _=>NativeSaveRequestResult.Uncertain
    };
    internal static byte[] BuildThunk(nuint prepare,nuint save,nuint frame,nuint completion,nuint profileManager,nuint suppressionFlags) {
        var bytes=new List<byte>(64);var jumps=new List<(int Index,string Label)>();var labels=new Dictionary<string,int>();
        void Add(params byte[] b)=>bytes.AddRange(b);
        void Imm(ulong value)=>bytes.AddRange(BitConverter.GetBytes(value));
        void Label(string name)=>labels[name]=bytes.Count;
        void Jump(byte opcode,string label){bytes.Add(opcode);jumps.Add((bytes.Count,label));bytes.Add(0);}
        void WriteStatus(byte status){Add(0x48,0xB8);Imm((ulong)completion);Add(0xC6,0x00,status);}
        Add(0x48,0x83,0xEC,0x28,0x48,0xB9);Imm((ulong)profileManager);Add(0x31,0xD2,0x48,0xB8);Imm((ulong)prepare);Add(0xFF,0xD0);
        Add(0x48,0xB8);Imm((ulong)suppressionFlags);Add(0x8B,0x00,0xF7,0xC0,0x00,0x40,0x00,0x00);Jump(0x75,"suppressed");
        Add(0x48,0xB8);Imm((ulong)save);Add(0xFF,0xD0,0x84,0xC0);Jump(0x75,"accepted");WriteStatus((byte)NativeSaveCompletionStatus.Rejected);Jump(0xEB,"return");
        Label("accepted");WriteStatus((byte)NativeSaveCompletionStatus.Accepted);Jump(0xEB,"return");
        Label("suppressed");WriteStatus((byte)NativeSaveCompletionStatus.Suppressed);
        Label("return");Add(0x48,0x83,0xC4,0x28,0x48,0xB8);Imm((ulong)frame);Add(0xFF,0xE0);
        foreach(var (index,label) in jumps){if(!labels.TryGetValue(label,out var target))throw new InvalidOperationException();var relative=target-(index+1);if(relative is<-128 or>127)throw new InvalidOperationException();bytes[index]=(byte)(sbyte)relative;}
        return bytes.ToArray();
    }
    private static void TrySetRip(int threadId,nuint rip){using var thread=new NativeHandle(OpenThread(0x001A,false,threadId));if(thread.Value==IntPtr.Zero)return;using var context=new NativeBuffer(ContextSize);Marshal.WriteInt32(context.Value,ContextFlagsOffset,(int)ContextAll);if(!GetThreadContext(thread.Value,context.Value))return;Marshal.WriteInt64(context.Value,RipOffset,(long)rip);SetThreadContext(thread.Value,context.Value);}
    private static byte[] Read(IntPtr h,nuint a,int n){var b=new byte[n];if(!ReadProcessMemory(h,(IntPtr)a,b,(nuint)n,out var r)||r!=(nuint)n)throw new IOException();return b;}
    private static void Write(IntPtr h,nuint a,byte[] b){if(!WriteProcessMemory(h,(IntPtr)a,b,(nuint)b.Length,out var w)||w!=(nuint)b.Length)throw new IOException();}
    private sealed class NativeHandle(IntPtr value):IDisposable{public IntPtr Value{get;}=value;public void Dispose(){if(Value!=IntPtr.Zero)CloseHandle(Value);}}
    private sealed class NativeBuffer:IDisposable{public IntPtr Value{get;}public NativeBuffer(int size){Value=Marshal.AllocHGlobal(size);for(var i=0;i<size;i+=8)Marshal.WriteInt64(Value,i,0);}public void Dispose()=>Marshal.FreeHGlobal(Value);}
    [DllImport("kernel32.dll")]private static extern IntPtr OpenProcess(uint access,bool inherit,int id);[DllImport("kernel32.dll")]private static extern IntPtr OpenThread(uint access,bool inherit,int id);[DllImport("kernel32.dll")]private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll")]private static extern bool ReadProcessMemory(IntPtr h,IntPtr a,byte[] b,nuint n,out nuint read);[DllImport("kernel32.dll")]private static extern bool WriteProcessMemory(IntPtr h,IntPtr a,byte[] b,nuint n,out nuint written);[DllImport("kernel32.dll")]private static extern bool FlushInstructionCache(IntPtr h,IntPtr a,nuint n);
    [DllImport("kernel32.dll")]private static extern IntPtr VirtualAllocEx(IntPtr h,IntPtr a,nuint n,uint type,uint protection);[DllImport("kernel32.dll")]private static extern bool VirtualProtectEx(IntPtr h,IntPtr a,nuint n,uint protection,out uint old);[DllImport("kernel32.dll")]private static extern bool VirtualFreeEx(IntPtr h,IntPtr a,nuint n,uint type);
    [DllImport("kernel32.dll")]private static extern bool DebugActiveProcess(int id);[DllImport("kernel32.dll")]private static extern bool DebugActiveProcessStop(int id);[DllImport("kernel32.dll")]private static extern bool DebugSetProcessKillOnExit(bool kill);[DllImport("kernel32.dll")]private static extern bool WaitForDebugEvent(IntPtr debugEvent,uint timeout);[DllImport("kernel32.dll")]private static extern bool ContinueDebugEvent(int pid,int tid,uint status);
    [DllImport("kernel32.dll")]private static extern bool GetThreadContext(IntPtr thread,IntPtr context);[DllImport("kernel32.dll")]private static extern bool SetThreadContext(IntPtr thread,IntPtr context);
}
