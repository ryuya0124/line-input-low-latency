using System;
using System.IO;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Windows.Forms;
using System.Collections.Generic;
using System.Runtime.InteropServices;

internal sealed class Controller : IDisposable {
 readonly string root;readonly object gate=new object(),logGate=new object();Thread worker;volatile bool wanted;Process engine;
 internal volatile string Message="停止中";
 internal bool Running {get{lock(gate){try{return engine!=null&&!engine.HasExited;}catch{return false;}}}}
 internal int EngineId {get{lock(gate){try{return engine==null?0:engine.Id;}catch{return 0;}}}}
 internal Controller(string root){this.root=root;Directory.CreateDirectory(Common.Data);}
 internal void Start(){lock(gate){wanted=true;if(worker!=null&&worker.IsAlive)return;worker=new Thread(Run){IsBackground=true,Name="Audio supervisor"};worker.Start();}}
 internal void Stop(){bool owned=wanted;wanted=false;lock(gate){owned=owned||engine!=null;}if(owned)Common.Signal("Local\\CodexLineMonitorStop");}
 internal void StopAndWait(){Stop();Thread w;lock(gate){w=worker;}if(w!=null)w.Join(15000);}
 void Log(string text){lock(logGate){try{File.AppendAllText(Path.Combine(Common.Data,"app.log"),DateTime.Now.ToString("s")+" "+text+Environment.NewLine);}catch{}}}
 void Run(){bool original=false,haveOriginal=false;string activeInput=null;try{
   while(wanted){string ready=Path.Combine(Common.Data,"ready-"+Guid.NewGuid().ToString("N")+".tmp");try{
     Message="音声を準備中";var settings=Common.ReadJson(Path.Combine(Common.Data,"settings.json"));bool exclusive=Common.Text(settings,"Mode","exclusive")=="exclusive";activeInput=Common.Text(settings,"InputId","");if(String.IsNullOrEmpty(activeInput)){Message="入力デバイスを選んでください";wanted=false;break;}original=Devices.Snapshot(activeInput);haveOriginal=true;Common.SetListen(root,activeInput,false);
     string arguments="\""+ready+"\" "+(exclusive?"--exclusive-input":"--shared-input")+" --input-id "+activeInput+" --output-id "+Common.Text(settings,"OutputId","default")+" --min-reserve-ms "+Devices.LearnedReserve(settings).ToString(System.Globalization.CultureInfo.InvariantCulture);
     var info=new ProcessStartInfo(Path.Combine(root,"AudioEngine.exe"),arguments){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,WorkingDirectory=Common.Data};
     var p=new Process{StartInfo=info,EnableRaisingEvents=true};p.OutputDataReceived+=(s,e)=>{if(e.Data!=null)Log(e.Data);};p.ErrorDataReceived+=(s,e)=>{if(e.Data!=null)Log(e.Data);};p.Start();p.BeginOutputReadLine();p.BeginErrorReadLine();lock(gate){engine=p;}
     DateTime deadline=DateTime.UtcNow.AddSeconds(10);while(wanted&&!File.Exists(ready)&&!p.HasExited&&DateTime.UtcNow<deadline)Thread.Sleep(50);if(!wanted)Common.Signal("Local\\CodexLineMonitorStop");else if(!File.Exists(ready)||p.HasExited)throw new IOException("音声機器を開けませんでした。接続を確認してください。");else if(!Common.Signal("Local\\CodexLineMonitorGo"))throw new IOException("音声の開始に失敗しました。");
     Message="再生中";while(wanted&&!p.WaitForExit(200)){}if(!wanted){Common.Signal("Local\\CodexLineMonitorStop");if(!p.WaitForExit(3000))p.Kill();}p.WaitForExit();int exit=p.ExitCode;lock(gate){engine=null;}p.Dispose();if(exit==0){wanted=false;break;}Message="機器の接続待ち";
    }catch(Exception ex){Log(ex.ToString());Message="機器の接続待ち";Process p;lock(gate){p=engine;engine=null;}if(p!=null){try{Common.Signal("Local\\CodexLineMonitorStop");if(!p.WaitForExit(2000))p.Kill();p.WaitForExit();}catch{}p.Dispose();}}
    finally{try{if(File.Exists(ready))File.Delete(ready);}catch{}}
    if(wanted)for(int i=0;i<30&&wanted;i++)Thread.Sleep(100);
   }
  }catch(Exception ex){Log(ex.ToString());Message="開始できませんでした";}
  finally{try{if(haveOriginal)Common.SetListen(root,activeInput,original);}catch(Exception ex){Log(ex.ToString());}Message=haveOriginal?"停止中（Windowsの元の設定に復元）":"入力を選んで開始してください";wanted=false;}
 }
 public void Dispose(){StopAndWait();}
}
internal sealed class MainForm : Form {
 long seenShortages;int seenEngine;
 protected override bool ShowWithoutActivation {get{return preview;}}
 readonly Controller controller;readonly NotifyIcon tray;readonly System.Windows.Forms.Timer timer;
 readonly Label state=new Label(),detail=new Label(),latency=new Label(),health=new Label();readonly Button start=new Button(),stop=new Button(),measure=new Button();readonly ComboBox mode=new ThemedComboBox(),output=new ThemedComboBox(),input=new ThemedComboBox(),appearance=new ThemedComboBox();readonly CheckBox startup=new CheckBox();readonly EventWaitHandle showEvent,exitEvent,reloadEvent,startEvent,stopEvent;bool quitting,preview,loadingOutputs,loadingInputs,restarting,lastDark;
 internal MainForm(bool background,bool preview){this.preview=preview;AutoScaleMode=AutoScaleMode.None;MaximizeBox=false;Text="ライン入力モニター";ClientSize=new Size(514,520);MinimumSize=new Size(530,559);MaximumSize=MinimumSize;Font=new Font("Yu Gothic UI",10);BackColor=Color.FromArgb(247,249,252);Icon=Theme.AppIcon();StartPosition=FormStartPosition.CenterScreen;ShowInTaskbar=true;
  var title=new Label{Text="ライン入力モニター",Font=new Font("Yu Gothic UI",17,FontStyle.Bold),Location=new Point(24,18),Size=new Size(450,38)};Controls.Add(title);
  detail.Text="選んだ入力音を、再生先のデバイスへ中継します";detail.Location=new Point(26,61);detail.Size=new Size(470,25);detail.ForeColor=Color.FromArgb(80,88,102);Controls.Add(detail);
  state.Text="音声を準備中";state.Font=new Font("Yu Gothic UI",12,FontStyle.Bold);state.ForeColor=Color.FromArgb(0,107,87);state.Location=new Point(26,103);state.Size=new Size(460,30);Controls.Add(state);
  latency.Text="ほかのPC音も同時に聴けます";latency.Location=new Point(26,138);latency.Size=new Size(460,26);Controls.Add(latency);
  health.Text="音切れを避けるよう、余裕を自動調整します";health.Location=new Point(26,168);health.Size=new Size(460,24);health.ForeColor=Color.FromArgb(80,88,102);Controls.Add(health);
  var inputLabel=new Label{Text="入力",Location=new Point(26,205),Size=new Size(80,27)};Controls.Add(inputLabel);input.DropDownStyle=ComboBoxStyle.DropDownList;input.Location=new Point(111,202);input.Size=new Size(327,30);input.DropDownWidth=470;Controls.Add(input);FillInputs();var inputRefresh=new Button{Text="↻",Location=new Point(449,201),Size=new Size(33,31)};inputRefresh.Click+=(s,e)=>FillInputs();Controls.Add(inputRefresh);
  var outputLabel=new Label{Text="再生先",Location=new Point(26,251),Size=new Size(80,27)};Controls.Add(outputLabel);output.DropDownStyle=ComboBoxStyle.DropDownList;output.Location=new Point(111,248);output.Size=new Size(327,30);output.DropDownWidth=470;Controls.Add(output);FillOutputs();var refresh=new Button{Text="↻",Location=new Point(449,247),Size=new Size(33,31)};refresh.Click+=(s,e)=>FillOutputs();Controls.Add(refresh);
  mode.DropDownStyle=ComboBoxStyle.DropDownList;mode.Items.AddRange(new object[]{"低遅延・自動調整","安定優先（共有入力）"});mode.Location=new Point(26,300);mode.Size=new Size(235,30);mode.SelectedIndex=Common.Text(Common.ReadJson(Path.Combine(Common.Data,"settings.json")),"Mode","exclusive")=="exclusive"?0:1;Controls.Add(mode);
  measure.Text="遅延を測る";measure.Location=new Point(280,298);measure.Size=new Size(202,32);Controls.Add(measure);
  start.Text="開始";start.Location=new Point(26,346);start.Size=new Size(100,33);Controls.Add(start);stop.Text="停止して元に戻す";stop.Location=new Point(139,346);stop.Size=new Size(202,33);Controls.Add(stop);
  startup.Text="サインイン時に自動起動";startup.Checked=File.Exists(Common.StartupLink);startup.Location=new Point(26,398);startup.Size=new Size(280,26);Controls.Add(startup);
  var logs=new LinkLabel{Text="設定・ログ",Location=new Point(371,397),Size=new Size(110,25)};logs.LinkClicked+=(s,e)=>Process.Start(new ProcessStartInfo(Common.Data){UseShellExecute=true});Controls.Add(logs);
  var appearanceLabel=new Label{Text="外観",Location=new Point(26,449),Size=new Size(80,27)};Controls.Add(appearanceLabel);appearance.DropDownStyle=ComboBoxStyle.DropDownList;appearance.Items.AddRange(new object[]{"Windowsに合わせる","ライト","ダーク"});appearance.Location=new Point(111,446);appearance.Size=new Size(235,30);appearance.SelectedIndex=Theme.Saved=="dark"?2:Theme.Saved=="light"?1:0;Controls.Add(appearance);
  controller=new Controller(Application.StartupPath);tray=new NotifyIcon{Icon=Icon,Text="ライン入力モニター",Visible=!preview};var menu=new ContextMenuStrip();menu.Items.Add("ウィンドウを開く",null,(s,e)=>ShowMain());menu.Items.Add("開始",null,(s,e)=>controller.Start());menu.Items.Add("停止して元に戻す",null,async(s,e)=>await StopAsync());menu.Items.Add(new ToolStripSeparator());menu.Items.Add("終了",null,async(s,e)=>await ExitAsync());tray.ContextMenuStrip=menu;tray.DoubleClick+=(s,e)=>ShowMain();
  showEvent=new EventWaitHandle(false,EventResetMode.AutoReset,"Local\\LineInputLowLatencyShow");exitEvent=new EventWaitHandle(false,EventResetMode.AutoReset,"Local\\LineInputLowLatencyExit");
  reloadEvent=new EventWaitHandle(false,EventResetMode.AutoReset,"Local\\LineInputLowLatencyReload");startEvent=new EventWaitHandle(false,EventResetMode.AutoReset,"Local\\LineInputLowLatencyStart");stopEvent=new EventWaitHandle(false,EventResetMode.AutoReset,"Local\\LineInputLowLatencyStop");
  start.Click+=(s,e)=>controller.Start();stop.Click+=async(s,e)=>await StopAsync();measure.Click+=(s,e)=>{if(Common.Signal("Local\\CodexLineMonitorMeasure"))latency.Text="測定中です。入力に音楽や会話を流してください";};
  mode.SelectedIndexChanged+=async(s,e)=>await RestartAudio();output.SelectedIndexChanged+=async(s,e)=>{if(!loadingOutputs)await RestartAudio();};input.SelectedIndexChanged+=async(s,e)=>{if(!loadingInputs)await RestartAudio();};appearance.SelectedIndexChanged+=(s,e)=>{var config=Common.ReadJson(Path.Combine(Common.Data,"settings.json"));config["Theme"]=appearance.SelectedIndex==2?"dark":appearance.SelectedIndex==1?"light":"auto";Common.WriteJson(Path.Combine(Common.Data,"settings.json"),config);ApplyAppearance();};
  startup.CheckedChanged+=(s,e)=>{try{Common.Startup(startup.Checked,Application.ExecutablePath);}catch(Exception ex){MessageBox.Show(this,ex.Message,"自動起動の設定");}};
  timer=new System.Windows.Forms.Timer{Interval=1000};timer.Tick+=async(s,e)=>{if(showEvent.WaitOne(0))ShowMain();if(exitEvent.WaitOne(0)){await ExitAsync();return;}if(startEvent.WaitOne(0))controller.Start();if(stopEvent.WaitOne(0))await StopAsync();if(reloadEvent.WaitOne(0)){FillOutputs(true);await RestartAudio();}if(appearance.SelectedIndex==0&&Theme.IsDark("auto")!=lastDark)ApplyAppearance();RefreshState();};
  FormClosing+=async(s,e)=>{if(!quitting&&e.CloseReason==CloseReason.UserClosing){e.Cancel=true;WindowState=FormWindowState.Minimized;}else if(!quitting){e.Cancel=true;await ExitAsync();}};
  if(!preview){timer.Start();Shown+=(s,e)=>{controller.Start();if(background)WindowState=FormWindowState.Minimized;};}else{state.Text="再生中";latency.Text="ほかのPC音も同時に聴けます";health.Text="状態：正常　／　音切れ防止の余裕を自動調整中";}
  ApplyAppearance();
 }
 internal void ApplyAppearance(string forced=null){string name=forced??(appearance.SelectedIndex==2?"dark":appearance.SelectedIndex==1?"light":"auto");lastDark=Theme.IsDark(name);Theme.Apply(this,name);detail.ForeColor=health.ForeColor=lastDark?Color.FromArgb(174,189,209):Color.FromArgb(80,88,102);state.ForeColor=lastDark?Color.FromArgb(104,230,194):Color.FromArgb(0,107,87);if(tray.ContextMenuStrip!=null)Theme.Menu(tray.ContextMenuStrip,lastDark);}
 void FillInputs(){loadingInputs=true;string selected=input.SelectedItem is Common.OutputDevice?((Common.OutputDevice)input.SelectedItem).Id:Common.Text(Common.ReadJson(Path.Combine(Common.Data,"settings.json")),"InputId","");input.Items.Clear();int index=-1;foreach(var item in Devices.Inputs()){input.Items.Add(item);if(String.Equals(item.Id,selected,StringComparison.OrdinalIgnoreCase))index=input.Items.Count-1;}if(index<0){input.Items.Add(new Common.OutputDevice{Id=selected,Label="未接続の入力（選び直せます）"});index=input.Items.Count-1;}input.SelectedIndex=index;loadingInputs=false;}
 void FillOutputs(bool useSaved=false){loadingOutputs=true;string selected=!useSaved&&output.SelectedItem is Common.OutputDevice?((Common.OutputDevice)output.SelectedItem).Id:Common.Text(Common.ReadJson(Path.Combine(Common.Data,"settings.json")),"OutputId","default");output.Items.Clear();int index=-1;foreach(var item in Common.Outputs()){output.Items.Add(item);if(String.Equals(item.Id,selected,StringComparison.OrdinalIgnoreCase))index=output.Items.Count-1;}if(index<0){output.Items.Add(new Common.OutputDevice{Id=selected,Label="未接続の再生先（選び直せます）"});index=output.Items.Count-1;}output.SelectedIndex=index;loadingOutputs=false;}
 async Task RestartAudio(){if(restarting)return;restarting=true;try{var device=output.SelectedItem as Common.OutputDevice;var source=input.SelectedItem as Common.OutputDevice;var config=Common.ReadJson(Path.Combine(Common.Data,"settings.json"));config["Mode"]=mode.SelectedIndex==0?"exclusive":"shared";config["InputId"]=source==null?"":source.Id;config["OutputId"]=device==null?"default":device.Id;Common.WriteJson(Path.Combine(Common.Data,"settings.json"),config);mode.Enabled=false;output.Enabled=false;input.Enabled=false;await Task.Run(()=>controller.StopAndWait());controller.Start();}finally{mode.Enabled=true;output.Enabled=true;input.Enabled=true;restarting=false;}}
 void ShowMain(){Show();WindowState=FormWindowState.Normal;Activate();}
 void RefreshState(){state.Text=controller.Message;bool running=controller.Running;measure.Enabled=running;start.Enabled=!running;stop.Enabled=running;var s=Common.ReadJson(Path.Combine(Common.Data,"state.json"));if(running&&(int)Common.Number(s,"pid",0)==controller.EngineId){int current=controller.EngineId;long shortages=(long)Common.Number(s,"bufferShortages",0);if(seenEngine!=current){seenEngine=current;seenShortages=0;}if(shortages>seenShortages){Devices.LearnReserve(Common.Number(s,"reserveMs",6));seenShortages=shortages;}long issues=(long)(Common.Number(s,"bufferShortages",0)+Common.Number(s,"captureGaps",0));health.Text=issues==0?"状態：正常　／　音切れ防止の余裕を自動調整中":"状態：音声の余裕を調整しました（検出 "+issues+" 回）";if(Common.Flag(s,"measuring",false))latency.Text="測定中です。入力に音楽や会話を流してください";else{int ms=(int)Common.Number(s,"latencyMs",-1);latency.Text=ms>=0?"PC内の遅延：約 "+ms+" ms　／　ほかのPC音も同時再生":"ほかのPC音も同時に聴けます";}}tray.Text=running?"ライン入力：再生中":"ライン入力：停止中";}
 async Task StopAsync(){stop.Enabled=false;await Task.Run(()=>controller.StopAndWait());RefreshState();}
 async Task ExitAsync(){if(quitting)return;quitting=true;timer.Stop();Enabled=false;await Task.Run(()=>controller.Dispose());tray.Visible=false;tray.Dispose();showEvent.Dispose();exitEvent.Dispose();reloadEvent.Dispose();startEvent.Dispose();stopEvent.Dispose();Close();Application.Exit();}
 protected override void Dispose(bool disposing){if(disposing){timer.Dispose();tray.Dispose();if(preview){controller.Dispose();showEvent.Dispose();exitEvent.Dispose();reloadEvent.Dispose();startEvent.Dispose();stopEvent.Dispose();}}base.Dispose(disposing);}
}
internal static class Program {
 [DllImport("shell32.dll",CharSet=CharSet.Unicode)]static extern int SetCurrentProcessExplicitAppUserModelID(string appId);
 [STAThread]static int Main(string[]args){Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);SetCurrentProcessExplicitAppUserModelID("Ryuya.LineInputLowLatency");Directory.CreateDirectory(Common.Data);
  if(args.Length==2&&args[0].StartsWith("--render-preview")){using(var form=new MainForm(false,true)){form.ApplyAppearance(args[0].EndsWith("-light")?"light":"dark");form.ShowInTaskbar=false;form.StartPosition=FormStartPosition.Manual;form.Location=new Point(-30000,-30000);form.Show();form.Update();using(var image=new Bitmap(form.Width,form.Height)){form.DrawToBitmap(image,new Rectangle(Point.Empty,image.Size));image.Save(args[1],System.Drawing.Imaging.ImageFormat.Png);}}return 0;}
  bool changeOutput=args.Length==2&&args[0]=="--output-id";if(changeOutput){if(!Common.Outputs().Exists(d=>String.Equals(d.Id,args[1],StringComparison.OrdinalIgnoreCase)))return 2;var saved=Common.ReadJson(Path.Combine(Common.Data,"settings.json"));saved["OutputId"]=args[1];if(!saved.ContainsKey("Mode"))saved["Mode"]="exclusive";Common.WriteJson(Path.Combine(Common.Data,"settings.json"),saved);}
  bool owned;using(var singleton=new Mutex(true,"Local\\LineInputLowLatencyUi",out owned)){if(!owned){Common.Signal(changeOutput?"Local\\LineInputLowLatencyReload":"Local\\LineInputLowLatencyShow");return 0;}foreach(string arg in args)if(arg=="--register-startup")Common.Startup(true,Application.ExecutablePath);Application.Run(new MainForm(Array.IndexOf(args,"--background")>=0,false));singleton.ReleaseMutex();}return 0;
 }
}
