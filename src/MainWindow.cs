using System;
using System.IO;
using System.Drawing;
using System.Drawing.Imaging;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Collections.Generic;
using System.Runtime.InteropServices;

internal sealed class MainForm : Form
{
    readonly bool preview;
    readonly Controller controller;
    readonly Panel viewport = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
    readonly TableLayoutPanel page = Column();
    readonly Panel statusCard = new Panel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Fill, Padding = new Padding(20), Margin = new Padding(0, 0, 0, 28) };
    readonly Label statusTitle = Caption("準備中", 22, FontStyle.Bold);
    readonly Label statusDescription = Caption("音声の状態を確認しています", 10);
    readonly Label runtime = Caption("", 10);
    readonly Label levelDescription = Caption("入力レベル", 10);
    readonly Label health = Caption("", 10);
    readonly AudioLevelMeter meter = new AudioLevelMeter { Dock = DockStyle.Fill, Height = 14, Margin = new Padding(0, 7, 0, 10) };
    readonly Button toggle = new Button { Text = "中継を開始", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new Size(150, 48), Padding = new Padding(14, 8, 14, 8), Anchor = AnchorStyles.Top | AnchorStyles.Right, Margin = new Padding(24, 0, 0, 0) };
    readonly Button measure = new Button { Text = "遅延を測る", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new Size(140, 44), Padding = new Padding(12, 6, 12, 6), Anchor = AnchorStyles.Top | AnchorStyles.Right, Margin = new Padding(16, 0, 0, 0) };
    readonly ThemedComboBox input = new ThemedComboBox();
    readonly ThemedComboBox output = new ThemedComboBox();
    readonly ThemedComboBox mode = new ThemedComboBox();
    readonly ThemedComboBox appearance = new ThemedComboBox();
    readonly CheckBox startup = new CheckBox { Text = "サインイン時に自動起動", AutoSize = true, Margin = new Padding(0, 4, 20, 4) };
    readonly ToolTip hints = new ToolTip { AutoPopDelay = 15000, InitialDelay = 350 };
    readonly NotifyIcon tray;
    readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 250 };
    readonly EventWaitHandle showEvent, exitEvent, reloadEvent, startEvent, stopEvent;
    readonly Icon baseIcon, activeIcon, idleIcon, waitingIcon;
    bool quitting, changing, restarting, stopping, lastDark;
    int displayedStatus = -1, seenEngine;
    long seenShortages, previousCapture, previousOutput;
    DateTime captureProgress = DateTime.MinValue, outputProgress = DateTime.MinValue;
    string measurementHint;

    protected override bool ShowWithoutActivation { get { return preview; } }

    static TableLayoutPanel Column()
    {
        var panel = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Top, Margin = Padding.Empty, Padding = Padding.Empty };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        return panel;
    }

    static Label Caption(string text, float size, FontStyle style = FontStyle.Regular)
    {
        return new Label { Text = text, AutoSize = true, Dock = DockStyle.Fill, Font = new Font("Yu Gothic UI", size, style), Margin = new Padding(0, 0, 0, 8), UseMnemonic = false };
    }

    static void AddRow(TableLayoutPanel parent, Control child)
    {
        int row = parent.RowCount++;
        parent.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        parent.Controls.Add(child, 0, row);
    }

    internal MainForm(bool background, bool preview)
    {
        SuspendLayout();
        this.preview = preview;
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Yu Gothic UI", 10.5f);
        Text = "ライン入力モニター";
        ClientSize = new Size(680, 780);
        MinimumSize = new Size(600, 640);
        StartPosition = FormStartPosition.CenterScreen;
        ShowInTaskbar = true;
        baseIcon = Theme.AppIcon();
        activeIcon = StatusIcons.Create(baseIcon, Color.FromArgb(26, 185, 133), true);
        idleIcon = StatusIcons.Create(baseIcon, Color.FromArgb(112, 124, 140), false);
        waitingIcon = StatusIcons.Create(baseIcon, Color.FromArgb(226, 163, 49), false);
        Icon = idleIcon;
        controller = new Controller(Application.StartupPath);

        viewport.Padding = new Padding(28);
        viewport.Controls.Add(page);
        Controls.Add(viewport);
        AddRow(page, Caption("ライン入力モニター", 19, FontStyle.Bold));
        var introduction = Caption("入力音を選んだデバイスへ中継します。ほかのPC音も同時に再生できます。", 10);
        introduction.Margin = new Padding(0, 0, 0, 24);
        AddRow(page, introduction);

        var statusLayout = Column();
        var statusTop = new TableLayoutPanel { ColumnCount = 2, RowCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Fill, Margin = Padding.Empty };
        statusTop.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        statusTop.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        statusTop.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var statusText = Column();
        AddRow(statusText, statusTitle);
        statusDescription.Margin = new Padding(0, 0, 0, 4);
        AddRow(statusText, statusDescription);
        statusTop.Controls.Add(statusText, 0, 0);
        statusTop.Controls.Add(toggle, 1, 0);
        AddRow(statusLayout, statusTop);
        runtime.Margin = new Padding(0, 8, 0, 12);
        AddRow(statusLayout, runtime);
        AddRow(statusLayout, levelDescription);
        AddRow(statusLayout, meter);
        health.Margin = Padding.Empty;
        AddRow(statusLayout, health);
        statusCard.Controls.Add(statusLayout);
        AddRow(page, statusCard);

        ConfigureCombo(input);
        ConfigureCombo(output);
        ConfigureCombo(mode);
        ConfigureCombo(appearance);
        FillInputs();
        FillOutputs();
        AddRow(page, DeviceSection("入力デバイス", input, FillInputs));
        AddRow(page, DeviceSection("再生先", output, () => FillOutputs()));

        mode.Items.AddRange(new object[] { "低遅延・自動調整", "安定優先（共有入力）" });
        mode.SelectedIndex = Common.Text(Settings(), "Mode", "exclusive") == "exclusive" ? 0 : 1;
        var modeSection = Column();
        modeSection.Margin = new Padding(0, 0, 0, 28);
        AddRow(modeSection, Caption("音声モード", 10, FontStyle.Bold));
        var modeControls = new TableLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 2, Dock = DockStyle.Fill, Margin = Padding.Empty };
        modeControls.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        modeControls.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        modeControls.Controls.Add(mode, 0, 0);
        modeControls.Controls.Add(measure, 1, 0);
        AddRow(modeSection, modeControls);
        AddRow(page, modeSection);

        var preferences = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 24), WrapContents = true };
        startup.Checked = File.Exists(Common.StartupLink);
        preferences.Controls.Add(startup);
        var logs = new LinkLabel { Text = "設定・ログを開く", AutoSize = true, Margin = new Padding(0, 5, 0, 4) };
        logs.LinkClicked += (s, e) => Process.Start(new ProcessStartInfo(Common.Data) { UseShellExecute = true });
        preferences.Controls.Add(logs);
        AddRow(page, preferences);
        appearance.Items.AddRange(new object[] { "Windowsに合わせる", "ライト", "ダーク" });
        appearance.SelectedIndex = Theme.Saved == "dark" ? 2 : Theme.Saved == "light" ? 1 : 0;
        var themeSection = Column();
        AddRow(themeSection, Caption("外観", 10, FontStyle.Bold));
        AddRow(themeSection, appearance);
        AddRow(page, themeSection);

        tray = new NotifyIcon { Icon = idleIcon, Text = "ライン入力：停止中", Visible = !preview };
        var menu = new ContextMenuStrip();
        menu.Items.Add("ウィンドウを開く", null, (s, e) => ShowMain());
        menu.Items.Add("中継を開始", null, (s, e) => controller.Start());
        menu.Items.Add("中継を停止して元に戻す", null, async (s, e) => await StopAsync());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("終了", null, async (s, e) => await ExitAsync());
        tray.ContextMenuStrip = menu;
        tray.DoubleClick += (s, e) => ShowMain();
        showEvent = Event("Show"); exitEvent = Event("Exit"); reloadEvent = Event("Reload"); startEvent = Event("Start"); stopEvent = Event("Stop");

        toggle.Click += async (s, e) => { if (controller.Requested) await StopAsync(); else controller.Start(); RefreshState(); };
        measure.Click += (s, e) => { if (Common.Signal("Local\\CodexLineMonitorMeasure")) { measurementHint = "測定中です。入力側で音楽や会話を流してください。"; RefreshState(); } };
        input.SelectedIndexChanged += async (s, e) => { if (!changing) await RestartAudio(); };
        output.SelectedIndexChanged += async (s, e) => { if (!changing) await RestartAudio(); };
        mode.SelectedIndexChanged += async (s, e) => { if (!changing) await RestartAudio(); };
        appearance.SelectedIndexChanged += (s, e) => { var config = Settings(); config["Theme"] = AppearanceName(); Common.WriteJson(SettingsPath, config); ApplyAppearance(); };
        startup.CheckedChanged += (s, e) => { try { Common.Startup(startup.Checked, Application.ExecutablePath); } catch (Exception ex) { MessageBox.Show(this, ex.Message, "自動起動の設定"); } };
        timer.Tick += async (s, e) => {
            if (showEvent.WaitOne(0)) ShowMain();
            if (exitEvent.WaitOne(0)) { await ExitAsync(); return; }
            if (startEvent.WaitOne(0)) controller.Start();
            if (stopEvent.WaitOne(0)) await StopAsync();
            if (reloadEvent.WaitOne(0)) { FillOutputs(true); await RestartAudio(); }
            if (appearance.SelectedIndex == 0 && Theme.IsDark("auto") != lastDark) ApplyAppearance();
            RefreshState();
        };
        FormClosing += async (s, e) => {
            if (quitting) return;
            e.Cancel = true;
            if (e.CloseReason == CloseReason.UserClosing) WindowState = FormWindowState.Minimized;
            else await ExitAsync();
        };
        Shown += (s, e) => {
            if (!preview) { FitScreen(); controller.Start(); timer.Start(); if (background) WindowState = FormWindowState.Minimized; }
        };
        ApplyAppearance();
        AutoScaleDimensions = new SizeF(96, 96);
        ResumeLayout(true);
        if (preview) {
            changing = true;
            input.Items.Clear(); input.Items.Add("ライン入力 (Realtek(R) Audio)"); input.SelectedIndex = 0;
            output.Items.Clear(); output.Items.AddRange(new object[] { "Windowsの既定の出力に従う", "スピーカー (FiiO KA13)", "ヘッドホン (USB Audio Device)", "デジタル オーディオ (High Definition Audio Device)" }); output.SelectedIndex = 1;
            changing = false;
            PreviewState(2);
        }
        else RefreshState();
    }

    static EventWaitHandle Event(string suffix) { return new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\LineInputLowLatency" + suffix); }
    static string SettingsPath { get { return Path.Combine(Common.Data, "settings.json"); } }
    static Dictionary<string, object> Settings() { return Common.ReadJson(SettingsPath); }
    string AppearanceName() { return appearance.SelectedIndex == 2 ? "dark" : appearance.SelectedIndex == 1 ? "light" : "auto"; }

    void ConfigureCombo(ThemedComboBox combo)
    {
        combo.AutoSize = true;
        combo.DropDownStyle = ComboBoxStyle.DropDownList;
        combo.Dock = DockStyle.Fill;
        combo.Margin = Padding.Empty;
        combo.SelectedIndexChanged += (s, e) => hints.SetToolTip(combo, combo.Text);
    }

    Control DeviceSection(string name, ThemedComboBox combo, Action refresh)
    {
        var section = Column();
        section.Margin = new Padding(0, 0, 0, 24);
        AddRow(section, Caption(name, 10, FontStyle.Bold));
        var row = new TableLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 2, Dock = DockStyle.Fill, Margin = Padding.Empty };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var reload = new Button { Text = "更新", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new Size(66, 40), Padding = new Padding(10, 4, 10, 4), Anchor = AnchorStyles.Top | AnchorStyles.Right, Margin = new Padding(12, 0, 0, 0) };
        reload.Click += (s, e) => refresh();
        row.Controls.Add(combo, 0, 0); row.Controls.Add(reload, 1, 0);
        AddRow(section, row);
        return section;
    }

    void FitScreen()
    {
        var area = Screen.FromControl(this).WorkingArea;
        MinimumSize = new Size(Math.Min(MinimumSize.Width, area.Width - 24), Math.Min(MinimumSize.Height, area.Height - 24));
        Size = new Size(Math.Min(Width, area.Width - 24), Math.Min(Height, area.Height - 24));
        if (Bottom > area.Bottom || Top < area.Top) Top = area.Top + Math.Max(0, (area.Height - Height) / 2);
    }

    internal void ApplyAppearance(string forced = null)
    {
        string name = forced ?? AppearanceName();
        lastDark = Theme.IsDark(name);
        Theme.Apply(this, name);
        viewport.BackColor = page.BackColor = BackColor;
        meter.Dark = lastDark;
        if (tray.ContextMenuStrip != null) Theme.Menu(tray.ContextMenuStrip, lastDark);
        PaintStatus();
    }

    void PaintStatus()
    {
        bool active = displayedStatus == 2;
        bool pending = displayedStatus == 1;
        statusCard.BackColor = lastDark ? Color.FromArgb(28, 39, 52) : Color.FromArgb(236, 243, 249);
        statusTitle.ForeColor = active ? (lastDark ? Color.FromArgb(97, 232, 183) : Color.FromArgb(0, 117, 79)) : pending ? (lastDark ? Color.FromArgb(255, 207, 112) : Color.FromArgb(148, 93, 0)) : (lastDark ? Color.FromArgb(183, 196, 213) : Color.FromArgb(75, 88, 107));
        toggle.BackColor = active ? (lastDark ? Color.FromArgb(48, 65, 83) : Color.White) : Color.FromArgb(12, 125, 96);
        toggle.ForeColor = active ? ForeColor : Color.White;
        statusDescription.ForeColor = runtime.ForeColor = health.ForeColor = lastDark ? Color.FromArgb(186, 200, 218) : Color.FromArgb(69, 84, 104);
    }

    void FillInputs()
    {
        changing = true;
        string selected = input.SelectedItem is Common.OutputDevice ? ((Common.OutputDevice)input.SelectedItem).Id : Common.Text(Settings(), "InputId", "");
        input.Items.Clear(); int index = -1;
        foreach (var item in Devices.Inputs()) { input.Items.Add(item); if (String.Equals(item.Id, selected, StringComparison.OrdinalIgnoreCase)) index = input.Items.Count - 1; }
        if (index < 0) { input.Items.Add(new Common.OutputDevice { Id = selected, Label = "未接続の入力（選び直せます）" }); index = input.Items.Count - 1; }
        input.SelectedIndex = index; input.RefreshMetrics(); changing = false;
    }

    void FillOutputs(bool useSaved = false)
    {
        changing = true;
        string selected = !useSaved && output.SelectedItem is Common.OutputDevice ? ((Common.OutputDevice)output.SelectedItem).Id : Common.Text(Settings(), "OutputId", "default");
        output.Items.Clear(); int index = -1;
        foreach (var item in Common.Outputs()) { output.Items.Add(item); if (String.Equals(item.Id, selected, StringComparison.OrdinalIgnoreCase)) index = output.Items.Count - 1; }
        if (index < 0) { output.Items.Add(new Common.OutputDevice { Id = selected, Label = "未接続の再生先（選び直せます）" }); index = output.Items.Count - 1; }
        output.SelectedIndex = index; output.RefreshMetrics(); changing = false;
    }

    async Task RestartAudio()
    {
        if (restarting || preview) return;
        restarting = true; RefreshState();
        try {
            var target = output.SelectedItem as Common.OutputDevice;
            var source = input.SelectedItem as Common.OutputDevice;
            var config = Settings();
            config["Mode"] = mode.SelectedIndex == 0 ? "exclusive" : "shared";
            config["InputId"] = source == null ? "" : source.Id;
            config["OutputId"] = target == null ? "default" : target.Id;
            Common.WriteJson(SettingsPath, config);
            input.Enabled = output.Enabled = mode.Enabled = false;
            await Task.Run(() => controller.StopAndWait());
            controller.Start(); measurementHint = null;
        }
        finally { input.Enabled = output.Enabled = mode.Enabled = true; restarting = false; RefreshState(); }
    }

    void RefreshState()
    {
        if (preview || quitting) return;
        var data = Common.ReadJson(Path.Combine(Common.Data, "state.json"));
        int engineId = controller.EngineId;
        bool matching = controller.Running && (int)Common.Number(data, "pid", 0) == engineId;
        bool fresh = false;
        try { fresh = DateTime.UtcNow - File.GetLastWriteTimeUtc(Path.Combine(Common.Data, "state.json")) < TimeSpan.FromSeconds(3); } catch { }
        long captured = (long)Common.Number(data, "captureFrames", 0);
        long consumed = (long)Common.Number(data, "consumedFrames", 0);
        if (matching) {
            if (seenEngine != engineId) { seenShortages = 0; previousCapture = previousOutput = 0; captureProgress = outputProgress = DateTime.MinValue; seenEngine = engineId; }
            if (captured > previousCapture) captureProgress = DateTime.UtcNow;
            if (consumed > previousOutput) outputProgress = DateTime.UtcNow;
            previousCapture = captured; previousOutput = consumed;
        }
        bool active = matching && fresh && captured > 0 && consumed > 0 && DateTime.UtcNow - captureProgress < TimeSpan.FromSeconds(3) && DateTime.UtcNow - outputProgress < TimeSpan.FromSeconds(3) && Common.Text(data, "state", "") == "running";
        bool pending = restarting || stopping || (controller.Requested && !active);
        bool hasInput = input.SelectedItem is Common.OutputDevice && !String.IsNullOrEmpty(((Common.OutputDevice)input.SelectedItem).Id);
        measure.Enabled = active && !pending;
        toggle.Enabled = !restarting && !stopping && (controller.Requested || hasInput);
        toggle.Text = controller.Requested ? "中継を停止" : "中継を開始";
        if (stopping) SetPresentation(1, "停止処理中", "元のWindowsの聴く設定へ戻しています。", "", "", 0);
        else if (pending) SetPresentation(1, controller.Message.Contains("接続") ? "接続待ち" : "準備中", "入力と再生先の接続を確認しています。", "", "ほかのPC音は引き続き再生できます。", 0);
        else if (active) {
            long shortages = (long)Common.Number(data, "bufferShortages", 0);
            if (shortages > seenShortages) { Devices.LearnReserve(Common.Number(data, "reserveMs", 6)); seenShortages = shortages; }
            double peak = Common.Number(data, "inputPeak", 0);
            long issues = shortages + (long)Common.Number(data, "captureGaps", 0);
            string description = peak > .002 ? "入力音を再生先へ送っています。" : "動作中です。入力音は静かです。";
            string summary = issues == 0 ? "音声処理は正常です。" : "音の途切れを防ぐため、余裕を調整しました。";
            if (Common.Flag(data, "measuring", false)) measurementHint = "測定中です。入力側で音楽や会話を流してください。";
            else if (measurementHint != null) { int ms = (int)Common.Number(data, "latencyMs", -1); measurementHint = ms >= 0 ? "PC内の参考遅延：約 " + ms + " ms" : "測定結果は信頼できる値が得られた場合に表示します。"; }
            var elapsed = TimeSpan.FromSeconds(Common.Number(data, "uptimeSeconds", 0));
            SetPresentation(2, "中継中", description, "稼働時間 " + ((int)elapsed.TotalHours).ToString("00") + ":" + elapsed.Minutes.ToString("00") + ":" + elapsed.Seconds.ToString("00"), measurementHint ?? summary, peak);
        }
        else SetPresentation(0, hasInput ? "中継停止" : "入力未選択", hasInput ? "アプリからの中継は停止しています。" : "入力デバイスを選んでください。", "", hasInput ? "Windowsの元の聴く設定に戻しています。" : "再生先と入力を選ぶと開始できます。", 0);
    }

    void SetPresentation(int state, string title, string description, string elapsed, string summary, double peak)
    {
        statusTitle.Text = title; statusDescription.Text = description; runtime.Text = elapsed; runtime.Visible = elapsed.Length > 0;
        health.Text = summary; meter.Level = Math.Max(0, Math.Min(1, peak)); meter.Enabled = state == 2;
        levelDescription.Text = state == 2 ? "入力レベル" : "入力レベル（中継停止中）";
        if (displayedStatus != state) { displayedStatus = state; Icon = state == 2 ? activeIcon : state == 1 ? waitingIcon : idleIcon; tray.Icon = Icon; PaintStatus(); }
        string appTitle = title + " — ライン入力モニター";
        if (Text != appTitle) Text = appTitle;
        tray.Text = "ライン入力：" + title;
    }

    async Task StopAsync()
    {
        if (stopping || quitting) return;
        stopping = true; RefreshState();
        try { await Task.Run(() => controller.StopAndWait()); }
        finally { stopping = false; measurementHint = null; RefreshState(); }
    }

    void ShowMain() { Show(); WindowState = FormWindowState.Normal; Activate(); }

    internal void ValidateLayout()
    {
        foreach (var combo in new[] { input, output, mode, appearance }) {
            combo.RefreshMetrics();
            combo.ValidateNativeMetrics();
            if (combo.RowHeight < TextRenderer.MeasureText("国Ag", combo.Font).Height + 6) throw new Exception("Dropdown row is too short");
            if (combo.Height < combo.RowHeight) throw new Exception("Dropdown selection is clipped");
            if (combo.DropDownWidth < combo.Width) throw new Exception("Dropdown popup is narrower than its field");
            if (combo.Bottom > combo.Parent.ClientSize.Height) throw new Exception("Dropdown extends past its section: "+combo.Text+" bottom="+combo.Bottom+" parent="+combo.Parent.ClientSize.Height);
        }
        if (toggle.Bounds.IntersectsWith(statusTitle.Bounds) && toggle.Parent == statusTitle.Parent) throw new Exception("Status action overlap");
    }

    internal void PreviewState(int state)
    {
        if(!preview)throw new InvalidOperationException();
        toggle.Text=state==0?"中継を開始":"中継を停止";
        if(state==2)SetPresentation(2,"中継中","入力音を再生先へ送っています。","稼働時間 00:03:24","音声処理は正常です。",.42);
        else if(state==1)SetPresentation(1,"接続待ち","入力と再生先の接続を確認しています。","","ほかのPC音は引き続き再生できます。",0);
        else SetPresentation(0,"中継停止","アプリからの中継は停止しています。","","Windowsの元の聴く設定に戻しています。",0);
    }

    internal void SettleLayout()
    {
        foreach(var combo in new[]{input,output,mode,appearance})combo.RefreshMetrics();
        LayoutChildren(this);
        PerformLayout();
    }

    internal void PreviewLargeText() { if(!preview)throw new InvalidOperationException(); Font=new Font("Yu Gothic UI",14); }
    static void LayoutChildren(Control parent)
    {
        foreach(Control child in parent.Controls)LayoutChildren(child);
        parent.PerformLayout();
    }

    internal void RenderDropdownPreview(string path)
    {
        output.RefreshMetrics();
        using(var bitmap=new Bitmap(output.DropDownWidth,output.RowHeight*output.Items.Count)) {
            using(var graphics=Graphics.FromImage(bitmap))output.RenderRows(graphics,new Rectangle(Point.Empty,bitmap.Size));
            bitmap.Save(path,ImageFormat.Png);
        }
    }

    internal void WriteLayout(string path)
    {
        using(var writer=new StreamWriter(path)) WriteControl(writer,this,0);
    }
    static void WriteControl(StreamWriter writer,Control control,int depth)
    {
        writer.WriteLine(new String(' ',depth*2)+control.GetType().Name+" "+control.Text+" bounds="+control.Bounds+" preferred="+control.PreferredSize+" font="+control.Font.Size+" autosize="+control.AutoSize);
        foreach(Control child in control.Controls)WriteControl(writer,child,depth+1);
    }

    async Task ExitAsync()
    {
        if (quitting) return;
        quitting = true; timer.Stop(); Enabled = false;
        await Task.Run(() => controller.Dispose());
        tray.Visible = false;
        Close(); Application.Exit();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) {
            timer.Dispose(); tray.Dispose(); hints.Dispose();
            if (preview) controller.Dispose();
            showEvent.Dispose(); exitEvent.Dispose(); reloadEvent.Dispose(); startEvent.Dispose(); stopEvent.Dispose();
            activeIcon.Dispose(); idleIcon.Dispose(); waitingIcon.Dispose(); baseIcon.Dispose();
        }
        base.Dispose(disposing);
    }
}

internal sealed class AudioLevelMeter : Control
{
    double level;
    internal bool Dark;
    internal double Level { get { return level; } set { level = value; Invalidate(); } }
    internal AudioLevelMeter() { DoubleBuffered = true; SetStyle(ControlStyles.ResizeRedraw, true); }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using (var background = new SolidBrush(Dark ? Color.FromArgb(51, 65, 82) : Color.FromArgb(207, 220, 232))) e.Graphics.FillRectangle(background, ClientRectangle);
        double amount = Enabled ? Math.Sqrt(Math.Max(0, Math.Min(1, level))) : 0;
        using (var fill = new SolidBrush(level > .95 ? Color.FromArgb(238, 164, 72) : Color.FromArgb(26, 185, 133))) e.Graphics.FillRectangle(fill, 0, 0, (int)(Width * amount), Height);
    }
}

internal static class StatusIcons
{
    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr handle);
    internal static Icon Create(Icon original, Color color, bool play)
    {
        using (var bitmap = new Bitmap(32, 32)) {
            using (var g = Graphics.FromImage(bitmap)) {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.DrawIcon(original, new Rectangle(0, 0, 32, 32));
                using (var border = new SolidBrush(Color.White)) g.FillEllipse(border, 17, 17, 15, 15);
                using (var badge = new SolidBrush(color)) g.FillEllipse(badge, 18, 18, 13, 13);
                using (var symbol = new SolidBrush(Color.White)) {
                    if (play) g.FillPolygon(symbol, new[] { new Point(23, 21), new Point(23, 28), new Point(28, 24) });
                    else g.FillRectangle(symbol, 22, 22, 5, 5);
                }
            }
            IntPtr handle = bitmap.GetHicon();
            try { using (var icon = Icon.FromHandle(handle)) return (Icon)icon.Clone(); }
            finally { DestroyIcon(handle); }
        }
    }
}
