using System.Net;
using System.Drawing;
using System.Runtime.InteropServices;

using System.Text.Json;

using PhantomDust.PcBridge.Core;

using QRCoder;



namespace PhantomDust.PcBridge.App;



internal sealed class BridgeTray:ApplicationContext {

    private readonly BridgeStore store;

    private readonly IPhantomDustProfileReader reader;

    private readonly Form window;

    private readonly Label status;

    private readonly NotifyIcon icon;
    private readonly Icon? trayIcon;

    private readonly ListView devices;

    private readonly PictureBox qr;

    private readonly Button pair;

    private readonly TextBox pairingCode;

    private IPAddress? pairingAddress;

    private long pairingExpiresAt;

    private string networkStatus="Not available";

    private string deviceSignature="";
    private bool showingConnectionRequest;
    private bool exitRequested;

    private DateTime? lastSuccessfulCheck;

    public void SetStatus(string text)=>status.Text=text;

    public void SetWifiAddress(IPAddress? address){var changed=!Equals(pairingAddress,address);pairingAddress=address;networkStatus=address is null?"waiting for a private network":$"https://{address}:17431";pair.Enabled=address is not null;if(changed){pairingExpiresAt=0;pairingCode.Clear();pairingCode.Tag=null;var old=qr.Image;qr.Image=null;old?.Dispose();}RefreshGameProfile();}

    public void RefreshGameProfile(){

        try{var s=reader.Read();if(s.ProfileVerified||s.ControlledTestSession)lastSuccessfulCheck=DateTime.Now;var detected=s.GameRunning?"Phantom Dust: detected":"Phantom Dust: not detected";var profile=s.GameRunning&&(!string.IsNullOrWhiteSpace(s.GameProfileDisplayName))?$"Active profile: {s.GameProfileDisplayName} · slot {(s.GameProfileSlot??-1)+1} · {s.Arsenals.Count} arsenal{(s.Arsenals.Count==1?"":"s")}":"Open your Phantom Dust profile";var writes=s.GameRunning&&s.GameForeground==false?"Phantom Dust is in the background. Bring it to the foreground and keep it focused for syncing.":s.WriteSupported?"Ready to sync":s.RequiredAction??"Open your profile to sync";SetStatus($"{detected}\n{profile}\n{writes}\nWi-Fi bridge: {networkStatus}\nLast successful check: {lastSuccessfulCheck?.ToString("T")??"Unknown"}");}

        catch(Exception){SetStatus("Game check failed. Profile and arsenal count unknown. Open the arsenal list and try again.");}

    }

    public void RefreshDevices(){

        if(!showingConnectionRequest&&store.PendingConnections().FirstOrDefault() is {} pending){
            showingConnectionRequest=true;
            try{var approved=MessageBox.Show(window,$"Connect {pending.DeviceName} to this PC?\n\nApprove only the phone you are setting up. If it was already paired, this repairs its credentials and leaves sync paused for review.","Phone wants to connect",MessageBoxButtons.YesNo,MessageBoxIcon.Question)==DialogResult.Yes;store.ApproveNearbyConnection(pending.RequestId,approved);}catch(BridgeException e){MessageBox.Show(window,e.Message);}finally{showingConnectionRequest=false;}
        }
        var rows=store.HostDevices();var signature=JsonSerializer.Serialize(rows);

        if(signature==deviceSignature)return;deviceSignature=signature;

        var selected=devices.SelectedItems.Cast<ListViewItem>().FirstOrDefault()?.Tag as string;

        devices.BeginUpdate();devices.Items.Clear();

        foreach(var d in rows){string Time(long value)=>value==0?"Unknown":DateTimeOffset.FromUnixTimeMilliseconds(value).LocalDateTime.ToString("g");var row=new ListViewItem([d.Name,d.Presence,Time(d.LastSeenAt),Time(d.PairedAt),d.Revoked?Time(d.UnpairedAt):"—"]){Tag=d.Id};devices.Items.Add(row);if(d.Id==selected)row.Selected=true;}

        devices.EndUpdate();

    }

    public void NotifySyncedArsenals(){if(!icon.Visible)return;var pending=store.ClaimNotices();if(pending.Length==1)icon.ShowBalloonTip(4500,pending[0].ArsenalName,pending[0].Message,ToolTipIcon.Info);else if(pending.Length>1)icon.ShowBalloonTip(6000,"Phantom Dust updates",string.Join("\n",pending.Select(n=>n.Message)),ToolTipIcon.Info);}

    public void NotifySyncStarting(int targetSlot){if(icon.Visible)icon.ShowBalloonTip(4500,$"Syncing PC case {targetSlot}","Keep Phantom Dust focused until sync completes",ToolTipIcon.Info);}

    public BridgeTray(BridgeStore store,IPhantomDustProfileReader reader,bool demo,IPAddress? address,string certificate,string log,string directory,bool headless,bool controlledTest=false){

        this.store=store;this.reader=reader;pairingAddress=address;
        trayIcon=LoadTrayIcon();

        window=new Form{Text="Phantom Dust Bridge"+(controlledTest?" — final test writes":demo?" — Demo":""),Icon=trayIcon??SystemIcons.Application,Width=790,Height=850,MinimumSize=new Size(650,600),StartPosition=FormStartPosition.CenterScreen,AutoScaleMode=AutoScaleMode.Dpi};

        var layout=new FlowLayoutPanel{Dock=DockStyle.Fill,FlowDirection=FlowDirection.TopDown,WrapContents=false,AutoScroll=true,Padding=new Padding(16)};window.Controls.Add(layout);

        var nameRow=new FlowLayoutPanel{Width=720,Height=42};nameRow.Controls.Add(new Label{Text="PC name",AutoSize=true,Padding=new Padding(0,8,0,0)});var name=new TextBox{Text=store.DisplayName,Width=350,MaxLength=80};nameRow.Controls.Add(name);

        var confirmName=new Button{Text=store.NameConfirmed?"Save name":"Confirm PC name",AutoSize=true};nameRow.Controls.Add(confirmName);layout.Controls.Add(nameRow);

        status=new Label{Width=710,Height=135,Text="Checking game…"};layout.Controls.Add(status);

        var refresh=new Button{Text="Refresh game and devices",AutoSize=true};refresh.Click+=(_,_)=>{RefreshGameProfile();RefreshDevices();icon?.ShowBalloonTip(3000,"Bridge refreshed",status.Text,ToolTipIcon.Info);};layout.Controls.Add(refresh);

        layout.Controls.Add(new Label{Text="Paired phones and tablets",AutoSize=true,Padding=new Padding(0,12,0,4)});

        devices=new ListView{Width=710,Height=180,View=View.Details,FullRowSelect=true,MultiSelect=false,HideSelection=false};devices.Columns.Add("Device",155);devices.Columns.Add("Connection",180);devices.Columns.Add("Last contact",175);devices.Columns.Add("Paired",175);devices.Columns.Add("Unpaired",175);layout.Controls.Add(devices);

        // Four actions wrap to two lines at common Windows DPI settings. Give
        // both rows their full height so the following pairing control cannot
        // overlap and clip the Reconnect button.
        var deviceActions=new FlowLayoutPanel{Width=710,Height=100,WrapContents=true};var rename=new Button{Text="Rename selected",AutoSize=true};var revoke=new Button{Text="Unpair selected",AutoSize=true};deviceActions.Controls.Add(rename);deviceActions.Controls.Add(revoke);var remove=new Button{Text="Remove from list",AutoSize=true};deviceActions.Controls.Add(remove);remove.Click+=(_,_)=>{if(devices.SelectedItems.Count==0)return;try{store.HideDevice((string)devices.SelectedItems[0].Tag!);RefreshDevices();}catch(Exception e){MessageBox.Show(window,e.Message);}};var reconnect=new Button{Text="Reconnect selected",AutoSize=true};deviceActions.Controls.Add(reconnect);reconnect.Click+=(_,_)=>{if(devices.SelectedItems.Count==0)return;try{store.RequestDeviceReconnect((string)devices.SelectedItems[0].Tag!);MessageBox.Show(window,"Open the companion app on this phone and confirm the connection. If its credentials were lost, use Connect PC on the phone to request repair.","Confirm on phone");}catch(BridgeException e){MessageBox.Show(window,e.Message);}};layout.Controls.Add(deviceActions);

        rename.Click+=(_,_)=>{if(devices.SelectedItems.Count==0)return;var row=devices.SelectedItems[0];using var dialog=new Form{Text="Device name",Width=420,Height=150,StartPosition=FormStartPosition.CenterParent};var input=new TextBox{Text=row.Text,Dock=DockStyle.Top,MaxLength=100};var save=new Button{Text="Save",Dock=DockStyle.Bottom,DialogResult=DialogResult.OK};dialog.Controls.Add(input);dialog.Controls.Add(save);dialog.AcceptButton=save;if(dialog.ShowDialog(window)==DialogResult.OK)try{store.RenameDevice((string)row.Tag!,input.Text);RefreshDevices();}catch(Exception e){MessageBox.Show(window,e.Message);}};

        revoke.Click+=(_,_)=>{if(devices.SelectedItems.Count==0)return;var row=devices.SelectedItems[0];if(MessageBox.Show(window,$"Unpair {row.Text} and cancel its pending sync requests? PC arsenals will remain untouched.","Unpair device",MessageBoxButtons.YesNo)==DialogResult.Yes){store.Revoke((string)row.Tag!);RefreshDevices();}};

        var pairPanel=new FlowLayoutPanel{Width=710,Height=420,FlowDirection=FlowDirection.TopDown,Visible=false};var pairToggle=new Button{Text="Pair another device…",AutoSize=true};layout.Controls.Add(pairToggle);layout.Controls.Add(pairPanel);

        pairingCode=new TextBox{Multiline=true,ReadOnly=true,Width=680,Height=65,ScrollBars=ScrollBars.Vertical};qr=new PictureBox{Width=270,Height=270,SizeMode=PictureBoxSizeMode.Zoom};

        pair=new Button{Text="Generate a new pairing code",AutoSize=true,Enabled=pairingAddress is not null};

        confirmName.Click+=(_,_)=>{try{store.RenameBridge(name.Text);confirmName.Text="Save name";pair.Enabled=pairingAddress is not null;}catch(Exception e){MessageBox.Show(window,e.Message);}};

        void GeneratePairing(){

            if(pairingAddress is null)return;

            try{

                // Pairing is also an implicit confirmation of the name already visible to the user.

                // Requiring a separate click made a healthy Bridge look disabled and broken.

                store.RenameBridge(name.Text);confirmName.Text="Save name";

                var payload=store.BeginPairing($"https://{pairingAddress}:17431",certificate);var json=JsonSerializer.Serialize(payload,Wire.Json);

                pairingExpiresAt=payload.ExpiresAt;pairingCode.Tag=json;pairingCode.Text=$"Short pairing code: {payload.ShortCode} (expires in 5 minutes)\r\n\r\n"+json;

                using var data=QRCodeGenerator.GenerateQrCode(json,QRCodeGenerator.ECCLevel.M);using var generator=new PngByteQRCode(data);using var stream=new MemoryStream(generator.GetGraphic(5));using var generated=new Bitmap(stream);var old=qr.Image;qr.Image=new Bitmap(generated);old?.Dispose();

            }catch(Exception e){MessageBox.Show(window,e.Message,"Could not start pairing",MessageBoxButtons.OK,MessageBoxIcon.Error);}

        }

        pairToggle.Click+=(_,_)=>{pairPanel.Visible=!pairPanel.Visible;if(pairPanel.Visible&&(pairingCode.Text.Length==0||pairingExpiresAt<=Wire.Now))GeneratePairing();};

        pair.Click+=(_,_)=>GeneratePairing();

        pairPanel.Controls.Add(pair);pairPanel.Controls.Add(qr);pairPanel.Controls.Add(pairingCode);var copy=new Button{Text="Copy pairing text",AutoSize=true};copy.Click+=(_,_)=>{if(pairingCode.Tag is string json)Clipboard.SetText(json);};pairPanel.Controls.Add(copy);

        var pause=new CheckBox{Text="Pause sync",AutoSize=true,Checked=store.Paused()};pause.CheckedChanged+=(_,_)=>store.Pause(null,pause.Checked);layout.Controls.Add(pause);

        if(!demo&&!controlledTest){var startup=new CheckBox{Text="Start Bridge when I sign in to Windows",AutoSize=true};using(var key=Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))startup.Checked=key?.GetValue("PhantomDustBridge") is not null;startup.CheckedChanged+=(_,_)=>{using var key=Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");if(startup.Checked)key.SetValue("PhantomDustBridge",$"\"{Environment.ProcessPath}\"");else key.DeleteValue("PhantomDustBridge",false);};layout.Controls.Add(startup);}

        var export=new Button{Text="Export troubleshooting details",AutoSize=true};export.Click+=(_,_)=>{using var dialog=new SaveFileDialog{FileName="phantom-dust-diagnostics.json",Filter="JSON|*.json"};if(dialog.ShowDialog(window)==DialogResult.OK)File.WriteAllText(dialog.FileName,JsonSerializer.Serialize(new{snapshot=reader.Read(),memory=demo?null:new GameReader().Diagnostic(),log=File.Exists(log)?File.ReadAllText(log):""},Wire.Json));};layout.Controls.Add(export);

        void OpenWindow(){window.Show();if(window.WindowState==FormWindowState.Minimized)window.WindowState=FormWindowState.Normal;window.Activate();}
        icon=new NotifyIcon{Icon=trayIcon??SystemIcons.Application,Visible=!headless,Text="Phantom Dust Bridge"};var menu=new ContextMenuStrip();menu.Items.Add("Open",null,(_,_)=>OpenWindow());menu.Items.Add("Exit",null,(_,_)=>{exitRequested=true;ExitThread();});icon.ContextMenuStrip=menu;icon.DoubleClick+=(_,_)=>OpenWindow();
        window.FormClosing+=(_,e)=>{if(BridgeWindowLifecycle.ShouldHide(e.CloseReason==CloseReason.UserClosing,exitRequested)){e.Cancel=true;window.Hide();}};
        window.FormClosed+=(_,_)=>ExitThread();

        RefreshGameProfile();RefreshDevices();if(!headless)window.Show();

    }

    protected override void ExitThreadCore(){exitRequested=true;icon.Visible=false;icon.Dispose();qr.Image?.Dispose();window.Dispose();trayIcon?.Dispose();base.ExitThreadCore();}

    [DllImport("user32.dll",SetLastError=true)] private static extern bool DestroyIcon(IntPtr handle);
    private static Icon? LoadTrayIcon(){try{
        using var resource=typeof(BridgeTray).Assembly.GetManifestResourceStream("PhantomDust.PcBridge.App.footer_arsenals.png");if(resource is null)return null;
        using var source=new Bitmap(resource);using var square=new Bitmap(256,256,System.Drawing.Imaging.PixelFormat.Format32bppArgb);using(var graphics=Graphics.FromImage(square)){graphics.Clear(Color.Transparent);graphics.DrawImageUnscaled(source,(square.Width-source.Width)/2,(square.Height-source.Height)/2);}
        var handle=square.GetHicon();try{return (Icon)Icon.FromHandle(handle).Clone();}finally{DestroyIcon(handle);}
    }catch{return null;}}

}
