using PhantomDust.PcBridge.Core;
using Xunit;
namespace PhantomDust.PcBridge.Tests;
public class BridgeVisibilityTests {
    private static string PathForTest()=>Path.Combine(Path.GetTempPath(),"pd-visibility-"+Guid.NewGuid(),"bridge.db");
    [Fact] public void HiddenUnpairedDeviceStillRejectsItsOldToken(){using var store=new BridgeStore(PathForTest());var pair=store.Pair(new(store.BeginPairing("https://localhost","certificate").BootstrapSecret,"Phone"));Assert.Throws<BridgeException>(()=>store.HideDevice(pair.PairingId));store.Revoke(pair.PairingId);var at=Assert.Single(store.HostDevices()).UnpairedAt;store.Revoke(pair.PairingId);Assert.Equal(at,Assert.Single(store.HostDevices()).UnpairedAt);store.HideDevice(pair.PairingId);Assert.Empty(store.HostDevices());Assert.Throws<BridgeException>(()=>store.Authenticate("Bearer "+pair.Token));}
    [Fact] public void NamesAndPairedDeviceProfilesSurviveRestart(){
        var path=PathForTest();string device;
        using(var store=new BridgeStore(path)){
            Assert.False(store.NameConfirmed);store.RenameBridge("Living room PC");
            var code=store.BeginPairing("https://localhost","certificate");var pair=store.Pair(new(code.BootstrapSecret,"Tablet","stable-tablet"));device=pair.PairingId;
            Assert.Equal(device,store.Authenticate("Bearer "+pair.Token));store.RenameDevice(device,"Lenovo tablet");
            var row=Assert.Single(store.HostDevices());Assert.Equal("Connected recently",row.Presence);Assert.True(row.LastSeenAt>0);Assert.True(row.PairedAt>0);
        }
        using(var store=new BridgeStore(path)){Assert.Equal("Living room PC",store.DisplayName);Assert.True(store.NameConfirmed);Assert.Equal("Lenovo tablet",Assert.Single(store.HostDevices()).Name);store.Revoke(device);Assert.Equal("Unpaired",Assert.Single(store.HostDevices()).Presence);Assert.True(Assert.Single(store.HostDevices()).UnpairedAt>0);store.HideDevice(device);Assert.Empty(store.HostDevices());}
    }
    [Fact] public void InvalidNamesNeverReplaceSavedName(){using var store=new BridgeStore(PathForTest());store.RenameBridge("My PC");Assert.Throws<BridgeException>(()=>store.RenameBridge(" "));Assert.Equal("My PC",store.DisplayName);}
    [Fact] public void UserCloseHidesButExitAndWindowsShutdownClose(){Assert.True(PhantomDust.PcBridge.App.BridgeWindowLifecycle.ShouldHide(true,false));Assert.False(PhantomDust.PcBridge.App.BridgeWindowLifecycle.ShouldHide(true,true));Assert.False(PhantomDust.PcBridge.App.BridgeWindowLifecycle.ShouldHide(false,false));}
    [Fact] public void ImportNotificationsAreDurableAndNotRepeated(){
        var path=PathForTest();
        using(var store=new BridgeStore(path)){
            var pair=store.Pair(new(store.BeginPairing("https://localhost","certificate").BootstrapSecret,"Phone"));
            var cards=Enumerable.Repeat("000",30).ToArray();var target=new SnapshotDeck(1,"Test",2,cards,"fingerprint",Planner.CardContentFingerprint(cards));
            var snapshot=new Snapshot("profile","snapshot",Wire.Now,true,true,false,false,false,[target],new Dictionary<string,int>(),GameProfileSlot:1,GameProfileDisplayName:"final");
            var request=new ProfileImportRequest(Guid.NewGuid().ToString(),"profile",[new(Guid.NewGuid().ToString(),Guid.NewGuid().ToString(),1,"fingerprint",target.ContentFingerprint!)]);
            store.ImportProfile(pair.PairingId,request,snapshot);store.ImportProfile(pair.PairingId,request,snapshot);
            Assert.Single(store.ClaimNotices());Assert.Empty(store.ClaimNotices());
        }
        using(var store=new BridgeStore(path))Assert.Empty(store.ClaimNotices());
    }
    [Fact] public void RetiredNoticeHistoryKeepsDurableDeduplication(){
        var path=PathForTest();string device;Snapshot snapshot;TransferReceipt first;
        using(var store=new BridgeStore(path)){
            device=store.Pair(new(store.BeginPairing("https://localhost","certificate").BootstrapSecret,"Phone")).PairingId;
            var cards=Enumerable.Repeat("000",30).ToArray();var target=new SnapshotDeck(1,"Test",2,cards,"fingerprint",Planner.CardContentFingerprint(cards));
            snapshot=new Snapshot("profile","snapshot",Wire.Now,true,true,false,false,false,[target],new Dictionary<string,int>(),GameProfileSlot:1,GameProfileDisplayName:"final");
            store.ImportProfile(device,new(Guid.NewGuid().ToString(),"profile",[new(Guid.NewGuid().ToString(),Guid.NewGuid().ToString(),1,"fingerprint",target.ContentFingerprint!)]),snapshot);store.ClaimNotices();
            first=new("receipt-0","profile",1,"fingerprint");
            for(var i=0;i<505;i++){store.AcknowledgeImport(device,new("receipt-"+i,"profile",1,"fingerprint"),snapshot);Assert.Single(store.ClaimNotices());}
        }
        using(var store=new BridgeStore(path)){store.AcknowledgeImport(device,first,snapshot);Assert.Empty(store.ClaimNotices());}
        using var db=new Microsoft.Data.Sqlite.SqliteConnection("Data Source="+path);db.Open();using var query=db.CreateCommand();query.CommandText="SELECT json FROM state";
        using var json=System.Text.Json.JsonDocument.Parse((string)query.ExecuteScalar()!);Assert.Equal(500,json.RootElement.GetProperty("notices").GetArrayLength());
    }

}
