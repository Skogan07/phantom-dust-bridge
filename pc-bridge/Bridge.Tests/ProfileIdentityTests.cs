using System.Text;
using PhantomDust.PcBridge.Core;
using Xunit;

namespace PhantomDust.PcBridge.Tests;

public sealed class ProfileIdentityTests {
    private static byte[] Record(int slot,string name,byte mutable=1) {
        var bytes=new byte[ProfileIdentity.RecordBytes]; BitConverter.GetBytes(slot).CopyTo(bytes,0);
        Encoding.ASCII.GetBytes(name.PadRight(ProfileIdentity.NameLength,'\0')[..ProfileIdentity.NameLength]).CopyTo(bytes,ProfileIdentity.NameOffset);
        for(var i=48;i<ProfileIdentity.RecordBytes;i++)bytes[i]=mutable;return bytes;
    }
    [Fact] public void UniqueMatchProducesSlotKeyAndDisplayData() {
        var a=Record(0,"PlayerOne",1);var b=Record(1,"Other",2);var current=(byte[])a.Clone();
        var result=ProfileIdentity.Resolve("bridge","family",true,current,[a,b]);
        Assert.True(result.Verified);Assert.Equal(0,result.Slot);Assert.Equal("PlayerOne",result.DisplayName);Assert.Equal(ProfileIdentity.DeriveKey("bridge","family",0),result.ProfileKey);
    }
    [Fact] public void OrdinaryEditMayChangeMutableTailWithoutLosingSlotIdentity() {
        var stored=Record(1,"final",1);var current=(byte[])stored.Clone();
        foreach(var offset in new[]{0x30,0x31,0x32,0x33,0x34,0x3c,0x3d,0x3e,0x3f,0x40})current[offset]++;
        var result=ProfileIdentity.Resolve("bridge","family",true,current,[Record(0,"PlayerOne",3),stored]);
        Assert.True(result.Verified);Assert.Equal(1,result.Slot);Assert.Equal("final",result.DisplayName);
    }
    [Fact] public void ObservedOffsetTwentyMayChangeWithoutLosingFinalIdentity() {
        var stored=Record(1,"final",1);var current=(byte[])stored.Clone();current[20]=20;
        var result=ProfileIdentity.Resolve("bridge","family",true,current,[Record(0,"PlayerOne",3),stored]);
        Assert.True(result.Verified);Assert.Equal(1,result.Slot);Assert.Equal("final",result.DisplayName);
    }
    [Fact] public void SelectorMissingFailsClosed() {
        var result=ProfileIdentity.Resolve("bridge","family",false,Record(0,"PlayerOne"),[Record(0,"PlayerOne")]);
        Assert.False(result.Verified);Assert.Null(result.ProfileKey);
    }
    [Fact] public void AmbiguousMatchFailsClosed() {
        var current=Record(0,"PlayerOne");var result=ProfileIdentity.Resolve("bridge","family",true,current,[Record(0,"PlayerOne"),Record(1,"PlayerOne")]);
        Assert.False(result.Verified);Assert.Contains("multiple",result.Reason,StringComparison.OrdinalIgnoreCase);
    }
    [Fact] public void ChangedRecordBytesDoNotMatch() {
        var current=Record(0,"PlayerOne");var other=Record(0,"PlayerOne");other[10]++;
        var result=ProfileIdentity.Resolve("bridge","family",true,current,[other]);
        Assert.False(result.Verified);
    }
    [Fact] public void PersistentKeyDependsOnSlotNotDisplayData() {
        Assert.NotEqual(ProfileIdentity.DeriveKey("bridge","family",0),ProfileIdentity.DeriveKey("bridge","family",1));
        Assert.Equal(ProfileIdentity.DeriveKey("bridge","family",0),ProfileIdentity.DeriveKey("bridge","family",0));
    }
    [Fact] public void ErrorDisplayNameFailsClosed() {
        var result=ProfileIdentity.Resolve("bridge","family",true,Record(0,"_ERROR_"),[Record(0,"_ERROR_"),Record(1,"Other")]);
        Assert.False(result.Verified);
    }
    [Fact] public void SlotMustMatchItsLogicalRecordIndex() {
        var bad=Record(1,"PlayerOne");var result=ProfileIdentity.Resolve("bridge","family",true,bad,[bad,Record(0,"Other")]);
        Assert.False(result.Verified);Assert.Contains("slot",result.Reason,StringComparison.OrdinalIgnoreCase);
    }
}
