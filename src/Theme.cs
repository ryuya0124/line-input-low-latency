using System;
using System.Drawing;
using System.Windows.Forms;
using Microsoft.Win32;
using System.Runtime.InteropServices;
internal static class Theme {
 [DllImport("dwmapi.dll")]static extern int DwmSetWindowAttribute(IntPtr h,int key,ref int value,int size);
 internal static bool IsDark(string mode){if(mode=="dark")return true;if(mode=="light")return false;using(var key=Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))return key!=null&&Convert.ToInt32(key.GetValue("AppsUseLightTheme",1))==0;}
 internal static string Saved {get{return Common.Text(Common.ReadJson(System.IO.Path.Combine(Common.Data,"settings.json")),"Theme","auto");}}
 internal static Icon AppIcon(){return Icon.ExtractAssociatedIcon(Application.ExecutablePath)??SystemIcons.Application;}
 internal static void Apply(Form form,string mode){bool dark=IsDark(mode);form.BackColor=dark?Color.FromArgb(20,26,35):Color.FromArgb(247,249,252);form.ForeColor=dark?Color.FromArgb(235,241,249):Color.FromArgb(25,34,46);Paint(form,dark);int value=dark?1:0;if(form.IsHandleCreated)DwmSetWindowAttribute(form.Handle,20,ref value,4);else form.HandleCreated+=(s,e)=>{int v=dark?1:0;DwmSetWindowAttribute(form.Handle,20,ref v,4);};}
 static void Paint(Control parent,bool dark){Color text=dark?Color.FromArgb(235,241,249):Color.FromArgb(25,34,46);foreach(Control c in parent.Controls){c.ForeColor=text;if(c is Panel||c is Label||c is CheckBox)c.BackColor=Color.Transparent;var link=c as LinkLabel;if(link!=null){link.LinkColor=dark?Color.FromArgb(104,211,249):Color.FromArgb(0,92,170);link.ActiveLinkColor=link.LinkColor;}var button=c as Button;if(button!=null){button.FlatStyle=FlatStyle.Flat;button.UseVisualStyleBackColor=false;button.BackColor=dark?Color.FromArgb(34,45,60):Color.White;button.FlatAppearance.BorderColor=dark?Color.FromArgb(72,89,111):Color.FromArgb(193,204,219);}var combo=c as ComboBox;if(combo!=null){combo.FlatStyle=FlatStyle.Flat;combo.BackColor=dark?Color.FromArgb(34,45,60):Color.White;combo.ForeColor=text;var custom=combo as ThemedComboBox;if(custom!=null){custom.Dark=dark;custom.Invalidate();}}if(c.HasChildren)Paint(c,dark);}}
 internal static void Menu(ContextMenuStrip menu,bool dark){menu.ForeColor=dark?Color.FromArgb(235,241,249):Color.FromArgb(25,34,46);menu.BackColor=dark?Color.FromArgb(27,36,49):Color.White;if(dark)menu.Renderer=new ToolStripProfessionalRenderer(new DarkMenu());else menu.Renderer=new ToolStripProfessionalRenderer();}
 sealed class DarkMenu:ProfessionalColorTable{public override Color ToolStripDropDownBackground{get{return Color.FromArgb(27,36,49);}}public override Color ImageMarginGradientBegin{get{return ToolStripDropDownBackground;}}public override Color ImageMarginGradientMiddle{get{return ToolStripDropDownBackground;}}public override Color ImageMarginGradientEnd{get{return ToolStripDropDownBackground;}}public override Color MenuItemSelected{get{return Color.FromArgb(48,65,86);}}public override Color MenuItemBorder{get{return Color.FromArgb(72,89,111);}}public override Color MenuBorder{get{return Color.FromArgb(72,89,111);}}}
}
internal sealed class ThemedComboBox : ComboBox
{
 [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h,int message,IntPtr w,IntPtr l);
 internal bool Dark;
 bool measuring;
 int rowHeight=36;
 internal int RowHeight { get { return rowHeight; } }
 internal ThemedComboBox(){DrawMode=DrawMode.OwnerDrawFixed;IntegralHeight=true;MaxDropDownItems=8;}
 internal void RefreshMetrics()
 {
  if(measuring||IsDisposed)return;
  measuring=true;
  try {
   using(var g=CreateGraphics()){
    int pad=Math.Max(8,(int)Math.Ceiling(12*g.DpiY/96));
    rowHeight=TextRenderer.MeasureText(g,"国Ag",Font,Size.Empty,TextFormatFlags.SingleLine|TextFormatFlags.NoPrefix).Height+pad;
    if(ItemHeight!=rowHeight)ItemHeight=rowHeight;
    int width=Width;
    foreach(var item in Items)width=Math.Max(width,TextRenderer.MeasureText(g,GetItemText(item),Font,Size.Empty,TextFormatFlags.SingleLine|TextFormatFlags.NoPrefix).Width+pad*2+SystemInformation.VerticalScrollBarWidth);
    DropDownWidth=Math.Max(Width,Math.Min(width,Screen.FromControl(this).WorkingArea.Width-24));
   }
   if(IsHandleCreated)SendMessage(Handle,0x153,new IntPtr(-1),new IntPtr(rowHeight));
   MinimumSize=new Size(0,rowHeight+6);
   Height=Math.Max(PreferredHeight,rowHeight+6);
  }finally{measuring=false;}
 }
 public override Size GetPreferredSize(Size proposedSize){return new Size(base.GetPreferredSize(proposedSize).Width,Math.Max(PreferredHeight,rowHeight+6));}
 protected override void OnFontChanged(EventArgs e){base.OnFontChanged(e);RefreshMetrics();}
 protected override void OnHandleCreated(EventArgs e){base.OnHandleCreated(e);RefreshMetrics();}
 protected override void OnSizeChanged(EventArgs e){base.OnSizeChanged(e);if(IsHandleCreated)RefreshMetrics();}
 protected override void OnDropDown(EventArgs e){RefreshMetrics();base.OnDropDown(e);}
 internal void ValidateNativeMetrics()
 {
  if(SendMessage(Handle,0x154,new IntPtr(-1),IntPtr.Zero).ToInt32()<rowHeight)throw new InvalidOperationException("Native selection height is too short");
  if(Items.Count>0&&SendMessage(Handle,0x154,IntPtr.Zero,IntPtr.Zero).ToInt32()<rowHeight)throw new InvalidOperationException("Native dropdown height is too short");
 }
 protected override void OnDrawItem(DrawItemEventArgs e)
 {
  if(e.Bounds.Width<=0)return;
  bool selected=(e.State&DrawItemState.Selected)!=0;
  Color background=selected?(Dark?Color.FromArgb(50,71,94):Color.FromArgb(221,237,251)):BackColor;
  using(var brush=new SolidBrush(background))e.Graphics.FillRectangle(brush,e.Bounds);
  string text=e.Index>=0&&e.Index<Items.Count?GetItemText(Items[e.Index]):Text;
  var bounds=Rectangle.Inflate(e.Bounds,-Math.Max(6,(int)(8*e.Graphics.DpiX/96)),0);
  TextRenderer.DrawText(e.Graphics,text,Font,bounds,Enabled?ForeColor:SystemColors.GrayText,TextFormatFlags.Left|TextFormatFlags.VerticalCenter|TextFormatFlags.SingleLine|TextFormatFlags.NoPrefix|TextFormatFlags.EndEllipsis);
  if((e.State&DrawItemState.Focus)!=0)e.DrawFocusRectangle();
 }
 internal void RenderRows(Graphics graphics,Rectangle bounds)
 {
  int y=bounds.Top;
  for(int i=0;i<Items.Count;i++){OnDrawItem(new DrawItemEventArgs(graphics,Font,new Rectangle(bounds.Left,y,bounds.Width,rowHeight),i,i==SelectedIndex?DrawItemState.Selected:DrawItemState.None));y+=rowHeight;}
 }
 protected override void WndProc(ref Message m)
 {
  base.WndProc(ref m);
  if(m.Msg==0x2E0){RefreshMetrics();return;}
  if(!Dark||!IsHandleCreated||(m.Msg!=0xF&&m.Msg!=0x317&&m.Msg!=0x318))return;
  using(var g=(m.Msg==0x317||m.Msg==0x318)&&m.WParam!=IntPtr.Zero?Graphics.FromHdc(m.WParam):Graphics.FromHwnd(Handle)){
   int width=SystemInformation.VerticalScrollBarWidth+4;
   var box=new Rectangle(Width-width,1,width-1,Height-2);
   using(var brush=new SolidBrush(BackColor))g.FillRectangle(brush,box);
   int x=box.Left+box.Width/2,y=Height/2;
   using(var brush=new SolidBrush(ForeColor))g.FillPolygon(brush,new[]{new Point(x-4,y-2),new Point(x+4,y-2),new Point(x,y+2)});
   using(var pen=new Pen(Color.FromArgb(72,89,111)))g.DrawRectangle(pen,0,0,Width-1,Height-1);
  }
 }
}
