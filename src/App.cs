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
 internal bool Requested {get{return wanted;}}
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
internal static class Program {
 [DllImport("shell32.dll",CharSet=CharSet.Unicode)]static extern int SetCurrentProcessExplicitAppUserModelID(string appId);
 [STAThread]static int Main(string[]args){Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);SetCurrentProcessExplicitAppUserModelID("Ryuya.LineInputLowLatency");Directory.CreateDirectory(Common.Data);
  if(args.Length==2&&args[0].StartsWith("--render-preview")){using(var form=new MainForm(false,true)){form.ApplyAppearance(args[0].Contains("-light")?"light":"dark");if(args[0].Contains("-stopped"))form.PreviewState(0);else if(args[0].Contains("-pending"))form.PreviewState(1);form.ShowInTaskbar=false;form.StartPosition=FormStartPosition.Manual;form.Location=new Point(-30000,-30000);form.Show();if(args[0].Contains("-large"))form.PreviewLargeText();if(args[0].Contains("-small"))form.Size=form.MinimumSize;form.SettleLayout();form.Update();form.WriteLayout(args[1]+".layout.txt");form.ValidateLayout();form.RenderDropdownPreview(args[1]+".dropdown.png");using(var image=new Bitmap(form.Width,form.Height)){form.DrawToBitmap(image,new Rectangle(Point.Empty,image.Size));image.Save(args[1],System.Drawing.Imaging.ImageFormat.Png);}}return 0;}
  bool changeOutput=args.Length==2&&args[0]=="--output-id";if(changeOutput){if(!Common.Outputs().Exists(d=>String.Equals(d.Id,args[1],StringComparison.OrdinalIgnoreCase)))return 2;var saved=Common.ReadJson(Path.Combine(Common.Data,"settings.json"));saved["OutputId"]=args[1];if(!saved.ContainsKey("Mode"))saved["Mode"]="exclusive";Common.WriteJson(Path.Combine(Common.Data,"settings.json"),saved);}
  string control=args.Length==1?args[0]:"";bool owned;using(var singleton=new Mutex(true,"Local\\LineInputLowLatencyUi",out owned)){if(!owned){string signal=control=="--stop"?"Local\\LineInputLowLatencyStop":control=="--start"?"Local\\LineInputLowLatencyStart":control=="--exit"?"Local\\LineInputLowLatencyExit":changeOutput?"Local\\LineInputLowLatencyReload":"Local\\LineInputLowLatencyShow";Common.Signal(signal);return 0;}if(control=="--stop"||control=="--exit"){singleton.ReleaseMutex();return 0;}foreach(string arg in args)if(arg=="--register-startup")Common.Startup(true,Application.ExecutablePath);Application.Run(new MainForm(Array.IndexOf(args,"--background")>=0||control=="--start",false));singleton.ReleaseMutex();}return 0;
 }
}
