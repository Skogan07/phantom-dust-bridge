using System.Net;
using System.Diagnostics;
using System.Text.Json;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Makaretu.Dns;
using PhantomDust.PcBridge.Core;
using QRCoder;

namespace PhantomDust.PcBridge.App;
internal sealed class DisabledSkillStoreReader : IPhantomDustSkillStoreReader {
    public bool Supported=>false;
    public SkillStoreResult Read()=>new("",SkillStoreState.Unsupported,Matches:[]);
}
public static class Program {
    [STAThread] public static void Main(string[] args) {
        ApplicationConfiguration.Initialize();
        var demo=args.Contains("--demo");var bridgeRoot=BridgePaths.CanonicalRoot();var explicitLegacy=args.FirstOrDefault(x=>x.StartsWith("--legacy-root=",StringComparison.OrdinalIgnoreCase));var legacyRoot=explicitLegacy is null?Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"PhantomDustBridge"):Path.GetFullPath(explicitLegacy[14..]);var directory=Path.Combine(bridgeRoot,demo?"demo":"live");
        var log=Path.Combine(directory,"diagnostics.log");
        void Log(string message){try{File.AppendAllText(log,$"{DateTimeOffset.UtcNow:O} {message}\n");}catch(IOException){}}
        try {
            if(explicitLegacy is not null&&!Path.IsPathFullyQualified(explicitLegacy[14..]))throw new ArgumentException("--legacy-root must be an absolute path.");
            using var instanceLock=BridgeInstanceLock.Acquire(bridgeRoot);
            WindowsSupport.EnsurePortAvailable(17431);
            BridgePaths.MigrateLegacy(legacyRoot,bridgeRoot,explicitLegacy is not null,()=>{WindowsSupport.EnsurePortAvailable(17431);return true;},demo?"demo":"live");
            BridgePaths.EnsureModeIsUsable(bridgeRoot,demo?"demo":"live");
            Directory.CreateDirectory(directory);
            using var store=new BridgeStore(Path.Combine(directory,"bridge.db"));using var certificate=WindowsSupport.Certificate(bridgeRoot,directory,store.CanCreateCertificate);
            var cert=Convert.ToHexString(SHA256.HashData(certificate.RawData));store.ConfirmCertificateIdentity(cert);var catalogue=WindowsSupport.Catalogue();var planner=new Planner(catalogue);
            var testArgument=args.FirstOrDefault(x=>x.StartsWith("--test-profile="));
            if(testArgument is not null && demo)throw new InvalidOperationException("Disposable test mode cannot run in demo mode.");
            DisposableTestSession? test=testArgument is null?null:new(testArgument[15..],directory,planner);
            IPhantomDustProfileReader reader=test is not null?test:demo?new DemoReader(catalogue):new GameReader(store.BridgeId);
            IPhantomDustSkillStoreReader storeReader=test is null&&!demo
                ?new LiveSkillStoreReader(reader,Path.Combine(AppContext.BaseDirectory,"skills.json"),Path.Combine(AppContext.BaseDirectory,"skill-id-map.json"))
                :new DisabledSkillStoreReader();
            GameWriter? writer=test is null&&!demo?new GameWriter(reader):null;
            if(args.Contains("--diagnostic")){File.WriteAllText(Path.Combine(directory,"memory-diagnostic.json"),JsonSerializer.Serialize(new{snapshot=reader.Read(),memory=new GameReader().Diagnostic()},Wire.Json));return;}
            var localOnly=demo||args.Contains("--usb");var allowed=localOnly?[IPAddress.Loopback]:WindowsSupport.Addresses();
            using var tray=new BridgeTray(store,reader,demo,allowed.FirstOrDefault(),cert,log,directory,args.Contains("--headless"),test is not null);
            if(test is not null)tray.SetStatus("FINAL TEST PROFILE · Repeated arsenal writes enabled. Auto Sync is controlled from the phone.");
            var builder=WebApplication.CreateBuilder(new WebApplicationOptions{Args=[]});builder.Logging.ClearProviders();
            builder.Services.ConfigureHttpJsonOptions(x=>{x.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());});
            builder.WebHost.ConfigureKestrel(k=>{k.Limits.MaxRequestBodySize=64*1024;if(localOnly)foreach(var address in allowed)k.Listen(address,17431,o=>o.UseHttps(certificate));else k.ListenAnyIP(17431,o=>o.UseHttps(certificate));});
            var app=builder.Build();
            app.Use(async(ctx,next)=>{
                try {
                    var localAddress=ctx.Connection.LocalIpAddress?.MapToIPv4();
                    if(!localOnly && (localAddress is null || !WindowsSupport.Addresses().Any(x=>x.MapToIPv4().Equals(localAddress)) || ctx.Connection.RemoteIpAddress is null || !WindowsSupport.IsPrivate(ctx.Connection.RemoteIpAddress)))throw new BridgeException(403,"Private LAN only.");
                    var requestPath=ctx.Request.Path.Value;
                    if(requestPath is not "/api/v1/pair" and not "/api/v1/discovery" and not "/api/v1/connect/request" and not "/api/v1/connect/poll")ctx.Items["device"]=store.Authenticate(ctx.Request.Headers.Authorization.ToString());
                    await next();
                }catch(BridgeException e){ctx.Response.StatusCode=e.Status;await ctx.Response.WriteAsJsonAsync(new{message=e.Message,code="RequestRejected",recoveryAction="Refresh the PC bridge and retry."});}
                catch(Exception e)when(e is ArgumentException or JsonException or InvalidOperationException){ctx.Response.StatusCode=400;await ctx.Response.WriteAsJsonAsync(new{message="Invalid or stale request.",code="InvalidRequest",recoveryAction="Refresh the PC bridge and retry."});Log("Request rejected: "+e.GetType().Name);}
            });
            string Device(HttpContext c)=>(string)c.Items["device"]!;
            app.MapPost("/api/v1/pair",(PairRequest r)=>store.Pair(r));
            app.MapPost("/api/v1/connect/request",(HttpContext c,NearbyConnectRequest r)=>store.RequestNearbyConnection(r,c.Connection.RemoteIpAddress?.ToString()??"unknown"));
            app.MapPost("/api/v1/connect/poll",(NearbyPoll r)=>store.PollNearbyConnection(r));
            app.MapPost("/api/v1/connect/finish",(HttpContext c)=>{store.FinishNearbyConnection(Device(c),c.Request.Headers.Authorization.ToString());return new{confirmed=true};});
            app.MapGet("/api/v1/connect/invitation",(HttpContext c)=>new{requestId=store.PendingDeviceReconnect(Device(c))});
            app.MapPost("/api/v1/connect/answer",(HttpContext c,ReconnectDecision r)=>{store.AnswerDeviceReconnect(Device(c),r);return new{confirmed=r.Accept};});
            app.MapGet("/api/v1/discovery",(HttpContext c)=>DiscoveryMetadata.Create(store.BridgeId,store.DisplayName,$"https://{c.Connection.LocalIpAddress?.MapToIPv4()}:17431",cert));
            app.MapGet("/api/v1/status",(HttpContext c)=>{var s=reader.Read();var paused=store.Paused(Device(c));var message=test is not null?(s.ControlledTestSession?"final test profile · Arsenal writes enabled · Save/reload not verified":"final test session ended · Refresh to inspect results"):demo?"DEMO · Simulated profile · No game writes":!s.GameRunning?"Connected to PC · Waiting for Phantom Dust":s.ProfileVerified?(s.Collection?.Status==CollectionStatus.Verified?"Game detected · Ready to sync the loaded profile":s.RequiredAction??"Skill quantities need verification"):s.RequiredAction??"Load your profile and open My Arsenals";return new BridgeStatus(store.BridgeId,store.DisplayName,test is not null?"final-test":demo?"demo":"live",paused?"Paused":s.ControlledTestSession?"TestArmed":!s.GameRunning?"GameClosed":s.WriteSupported?"ReadyToWrite":"ReadOnly",s.GameRunning,s.ProfileVerified,s.WriteSupported,paused,Wire.Now,message,ErrorCode:s.WriteSupported?null:"WriterUnavailable",RecoveryAction:s.RequiredAction??(s.WriteSupported?"":"Open Phantom Dust and refresh the arsenal list."),GameForeground:s.GameForeground,ReadinessCode:s.ReadinessCode,RequiredAction:s.RequiredAction,StoreWatchSupported:storeReader.Supported);});
            app.MapGet("/api/v1/profile/snapshot",()=>reader.Read() with{SkillCountChecksSupported=true});
            app.MapPut("/api/v1/store/watchlist",(HttpContext c,StoreWatchRequest r)=>{var snapshot=reader.Read();if(!(snapshot.ProfileVerified||snapshot.ControlledTestSession)||snapshot.ProfileKey!=r.ProfileKey)throw new BridgeException(409,"Refresh and load the verified Phantom Dust profile before updating its watch list.");return store.ReplaceStoreWatch(Device(c),r);});
            app.MapGet("/api/v1/store/watchlist/{profileKey}",(HttpContext c,string profileKey)=>store.StoreWatch(Device(c),profileKey));
            app.MapPost("/api/v1/sync/plan",(PlanRequest r)=>planner.Plan(r,reader.Read()));
            app.MapGet("/api/v1/links",(HttpContext c)=>store.Links(Device(c)));
            app.MapPost("/api/v1/links",(HttpContext c,LinkRequest r)=>store.Link(Device(c),r,reader.Read()));
            app.MapPost("/api/v1/transfers/receipts",(HttpContext c,TransferReceipt r)=>store.AcknowledgeImport(Device(c),r,reader.Read()));
            app.MapPost("/api/v1/profile-imports",(HttpContext c,ProfileImportRequest r)=>store.ImportProfile(Device(c),r,reader.Read()));
            app.MapDelete("/api/v1/links/{id}",(HttpContext c,string id)=>store.Unlink(Device(c),id));
            app.MapPost("/api/v1/sync/queue",(HttpContext c,QueueRequest r)=>{var j=store.Queue(Device(c),r);Log("Job queued "+j.JobId);return j;});
            app.MapGet("/api/v1/jobs",(HttpContext c)=>store.Jobs(Device(c)));
            app.MapGet("/api/v1/jobs/{id}",(HttpContext c,string id)=>store.Jobs(Device(c)).FirstOrDefault(x=>x.JobId==id)??throw new BridgeException(404,"Job not found."));
            app.MapPost("/api/v1/jobs/{id}/retry",(HttpContext c,string id)=>store.Retry(Device(c),id));
            app.MapPost("/api/v1/jobs/{id}/wait-for-game",(HttpContext c,string id)=>store.WaitForGame(Device(c),id));
            app.MapPost("/api/v1/jobs/{id}/verify-save",(HttpContext c,string id)=>store.VerifySave(Device(c),id,reader.Read()));
            app.MapPost("/api/v1/jobs/{id}/resolve-recovery",(HttpContext c,string id,RecoveryResolutionRequest r)=>store.ResolveRecovery(Device(c),id,r,reader.Read()));
            app.MapPost("/api/v1/jobs/{id}/cancel",(HttpContext c,string id)=>store.Cancel(Device(c),id));
            app.MapPost("/api/v1/journeys/{id}/cancel",(HttpContext c,string id)=>store.CancelJourney(Device(c),id));
            app.MapPost("/api/v1/control/pause",(HttpContext c,ControlRequest r)=>store.Pause(Device(c),r.Paused));
            app.MapPost("/api/v1/unpair",(HttpContext c)=>store.Revoke(Device(c)));
            app.StartAsync().GetAwaiter().GetResult();Log("Bridge started in "+(test is not null?"final test write":demo?"DEMO":"live read-only")+" mode.");
            // Explicit test/setup export only; never include this secret in diagnostics or normal logs.
            var pairFile=args.FirstOrDefault(x=>x.StartsWith("--pair-file="));if(pairFile is not null){var payload=store.BeginPairing($"https://{allowed[0]}:17431",cert);File.WriteAllText(pairFile[12..],JsonSerializer.Serialize(payload,Wire.Json));}
            MulticastService? mdns=null;ServiceDiscovery? discovery=null;
            void RefreshWifiDiscovery(){
                discovery?.Dispose();discovery=null;mdns?.Dispose();mdns=null;allowed=WindowsSupport.Addresses();tray.SetWifiAddress(allowed.FirstOrDefault());
                if(allowed.Length==0){Log("Wi-Fi discovery waiting for a private interface.");return;}
                try{mdns=new MulticastService(nics=>nics.Where(n=>WindowsSupport.PrivateAdapters().Contains(n.Id.Trim('{','}')))){UseIpv6=false};discovery=new ServiceDiscovery(mdns);var profile=new ServiceProfile("PD-"+store.BridgeId,"_phantomdustbridge._tcp",17431,allowed);var metadata=DiscoveryMetadata.Create(store.BridgeId,store.DisplayName,$"https://{allowed[0]}:17431",cert);foreach(var item in DiscoveryMetadata.Txt(metadata))profile.AddProperty(item.Key,item.Value);discovery.Advertise(profile);mdns.Start();Log("Wi-Fi discovery advertising "+string.Join(',',allowed.Select(x=>x.ToString())));}
                catch(Exception e){Log("Discovery unavailable: "+e.GetType().Name);}
            }
            if(!localOnly)RefreshWifiDiscovery();else tray.SetWifiAddress(null);
            using var timer=new System.Windows.Forms.Timer{Interval=3000};timer.Tick+=(_,_)=>{
                try {
                    if(!localOnly&&!allowed.OrderBy(x=>x.ToString()).SequenceEqual(WindowsSupport.Addresses().OrderBy(x=>x.ToString()))){Log("Private network changed; refreshing Wi-Fi discovery.");RefreshWifiDiscovery();}
                    if(test is not null)store.ExecuteOneTestJob(reader,planner,test.Apply);else if(writer is not null)store.ExecuteOneNormalJob(reader,planner,(request,snapshot,progress)=>{tray.NotifySyncStarting(request.TargetSlot);return writer.Apply(request,snapshot,progress);},writer.ProbeFrameReady);else store.Reconcile(reader.Read(),planner);
                    if(storeReader.Supported)store.RecordStoreObservation(storeReader.Read());
                    store.RecordJobNotices();tray.RefreshGameProfile();tray.RefreshDevices();tray.NotifySyncedArsenals();
                }catch(Exception e){Log("Refresh failed: "+e.GetType().Name+": "+e.Message);}
            };timer.Start();
            try{Application.Run(tray);}
            finally{
                timer.Stop();discovery?.Dispose();mdns?.Dispose();
                app.StopAsync().GetAwaiter().GetResult();
                app.DisposeAsync().AsTask().GetAwaiter().GetResult();
                Log("Bridge stopped and released port 17431.");
            }
        }catch(Exception e){Log("Startup failure: "+e.GetType().Name+" "+e.Message);if(!args.Contains("--headless"))MessageBox.Show("Bridge could not start. See "+log+"\n"+e.Message,"Phantom Dust Bridge");}
    }
}
