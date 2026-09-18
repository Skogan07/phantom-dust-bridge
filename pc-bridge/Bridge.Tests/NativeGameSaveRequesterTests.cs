using PhantomDust.PcBridge.App;
using Xunit;

namespace PhantomDust.PcBridge.Tests;

public sealed class NativeGameSaveRequesterTests {
    [Theory]
    [InlineData(0, 0, 4)]
    [InlineData(1, 1, 1)]
    [InlineData(2, 2, 2)]
    [InlineData(3, 3, 3)]
    [InlineData(99, 255, 4)]
    public void CompletionStatusMapsToRequestResult(byte raw, byte expectedStatus, byte expectedResult) {
        var status=(NativeSaveCompletionStatus)expectedStatus;var result=(NativeSaveRequestResult)expectedResult;
        Assert.Equal(status, NativeGameSaveRequester.DecodeCompletionStatus(raw));
        Assert.Equal(result, NativeGameSaveRequester.DecodeRequestResult(status));
    }

    [Fact]
    public void ThunkContainsPrepareArgumentsSuppressionReadAndStatusWrites() {
        var thunk=NativeGameSaveRequester.BuildThunk(0x1111,0x2222,0x3333,0x4444,0x5555,0x6666);
        Assert.Equal(123,thunk.Length);
        AssertSegment(thunk,0,0x48,0x83,0xEC,0x28);
        AssertSegment(thunk,107,0x48,0x83,0xC4,0x28,0x48,0xB8);
        Assert.Equal((ulong)0x5555,ReadImmediate(thunk,6));
        AssertSegment(thunk,14,0x31,0xD2,0x48,0xB8);
        Assert.Equal((ulong)0x1111,ReadImmediate(thunk,18));
        AssertSegment(thunk,26,0xFF,0xD0,0x48,0xB8);
        Assert.Equal((ulong)0x6666,ReadImmediate(thunk,30));
        AssertSegment(thunk,40,0xF7,0xC0,0x00,0x40,0x00,0x00,0x75,0x2E);
        Assert.Equal((ulong)0x2222,ReadImmediate(thunk,50));
        AssertSegment(thunk,58,0xFF,0xD0,0x84,0xC0,0x75,0x0F);
        AssertStatusWrite(thunk,64,0x4444,2);
        AssertStatusWrite(thunk,79,0x4444,1);
        AssertStatusWrite(thunk,94,0x4444,3);
        AssertSegment(thunk,77,0xEB,0x1C);
        AssertSegment(thunk,92,0xEB,0x0D);
        Assert.Equal(94,RelativeTarget(thunk,46));
        Assert.Equal(79,RelativeTarget(thunk,62));
        Assert.Equal(107,RelativeTarget(thunk,77));
        Assert.Equal(107,RelativeTarget(thunk,92));
        Assert.Equal((ulong)0x3333,ReadImmediate(thunk,113));
        AssertSegment(thunk,121,0xFF,0xE0);
    }

    private static ulong ReadImmediate(byte[] bytes,int offset)=>BitConverter.ToUInt64(bytes,offset);
    private static int RelativeTarget(byte[] bytes,int opcodeOffset)=>opcodeOffset+2+(sbyte)bytes[opcodeOffset+1];
    private static void AssertStatusWrite(byte[] bytes,int offset,ulong target,byte status) {
        AssertSegment(bytes,offset,0x48,0xB8);Assert.Equal(target,ReadImmediate(bytes,offset+2));AssertSegment(bytes,offset+10,0xC6,0x00,status);
    }
    private static void AssertSegment(byte[] bytes,int offset,params byte[] expected) => Assert.Equal(expected,bytes[offset..(offset+expected.Length)]);
}
