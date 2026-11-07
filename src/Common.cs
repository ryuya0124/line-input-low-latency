using System;
using System.IO;
using System.Diagnostics;
using System.Collections.Generic;
using System.Threading;
using System.Web.Script.Serialization;
using Microsoft.Win32;
using System.Runtime.InteropServices;

internal static class Common {
 internal const string Product="LineInputLowLatency", Version="0.3.1", ProductId="cd83fe09-230c-4e62-864c-3b0e14b4ac15";
 internal static readonly string Data=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),Product);
 internal static readonly string Install=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),Product);
 internal static readonly string Source=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),Product);
 internal static readonly string StartupLink=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup),"ライン入力モニター.lnk");
 internal static readonly string MenuFolder=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs),"ライン入力モニター");
 internal static Dictionary<string,object> ReadJson(string path){try{using(var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete))using(var reader=new StreamReader(stream)){return new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(reader.ReadToEnd());}}catch{return new Dictionary<string,object>();}}
 internal static void WriteJson(string path,object value){Directory.CreateDirectory(Path.GetDirectoryName(path));string temp=path+"."+Guid.NewGuid().ToString("N")+".tmp";File.WriteAllText(temp,new JavaScriptSerializer().Serialize(value),new System.Text.UTF8Encoding(false));try{for(int attempt=0;;attempt++){try{if(File.Exists(path))File.Replace(temp,path,null);else File.Move(temp,path);return;}catch(IOException){if(attempt>=4)throw;Thread.Sleep(20);}}}finally{if(File.Exists(temp))File.Delete(temp);}}
 internal static double Number(Dictionary<string,object>d,string key,double fallback){try{return d.ContainsKey(key)?Convert.ToDouble(d[key]):fallback;}catch{return fallback;}}
 internal static bool Flag(Dictionary<string,object>d,string key,bool fallback){try{return d.ContainsKey(key)?Convert.ToBoolean(d[key]):fallback;}catch{return fallback;}}
 internal static string Text(Dictionary<string,object>d,string key,string fallback){return d.ContainsKey(key)?Convert.ToString(d[key]):fallback;}
 internal static bool ListenEnabled(string id){return Devices.ListenEnabled(id);}
 internal static void SetListen(string appRoot,string inputId,bool value){Devices.SetListen(appRoot,inputId,value);}
 internal static bool Signal(string name){try{using(var e=EventWaitHandle.OpenExisting(name)){e.Set();return true;}}catch{return false;}}
 internal static void Shortcut(string path,string target,string args){Directory.CreateDirectory(Path.GetDirectoryName(path));Type t=Type.GetTypeFromProgID("WScript.Shell");dynamic shell=Activator.CreateInstance(t);dynamic link=shell.CreateShortcut(path);link.TargetPath=target;link.Arguments=args;link.WorkingDirectory=Path.GetDirectoryName(target);link.IconLocation=target+",0";link.WindowStyle=7;link.Save();Marshal.FinalReleaseComObject(link);Marshal.FinalReleaseComObject(shell);}
 internal static void Startup(bool enabled,string exe){if(enabled)Shortcut(StartupLink,exe,"--background");else if(File.Exists(StartupLink))File.Delete(StartupLink);}
 internal sealed class OutputDevice {internal string Id,Label;public override string ToString(){return Label;}}
 internal static List<OutputDevice> Outputs(){var list=new List<OutputDevice>{new OutputDevice{Id="default",Label="Windowsの既定の出力に従う"}};using(var root=Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Render")){if(root!=null)foreach(string id in root.GetSubKeyNames()){using(var device=root.OpenSubKey(id)){if(device==null||Convert.ToInt32(device.GetValue("DeviceState",0))!=1)continue;using(var p=device.OpenSubKey("Properties")){if(p==null)continue;string name=Convert.ToString(p.GetValue("{a45c254e-df1c-4efd-8020-67d146a850e0},14",""));if(String.IsNullOrEmpty(name))name=Convert.ToString(p.GetValue("{a45c254e-df1c-4efd-8020-67d146a850e0},2","出力"))+" ("+Convert.ToString(p.GetValue("{b3f8fa53-0004-438e-9003-51a46e139bfc},6",""))+")";list.Add(new OutputDevice{Id="{0.0.0.00000000}."+id,Label=name});}}}}return list;}
 internal static void StopInstalledApp(){Signal("Local\\LineInputLowLatencyExit");Thread.Sleep(800);Signal("Local\\CodexLineMonitorStop");foreach(var p in Process.GetProcessesByName("LineInputLowLatency")){try{if(p.Id!=Process.GetCurrentProcess().Id&&(Path.GetFullPath(p.MainModule.FileName).StartsWith(Install+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)||Path.GetFullPath(p.MainModule.FileName).StartsWith(Source+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)))p.WaitForExit(10000);}catch{}finally{p.Dispose();}}}
 internal static void RestoreIfNeeded(string appRoot){Devices.RestoreAll(appRoot);}
}
