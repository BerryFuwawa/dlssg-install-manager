using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace DlssgManager
{
    public sealed class MainForm : FluentForm
    {
        readonly Upstream upstream = new Upstream();
        readonly GuiUpdater updater = new GuiUpdater();
        readonly Installer installer;
        readonly CancellationTokenSource lifetime = new CancellationTokenSource();
        readonly ToolTip tips = new ToolTip { AutoPopDelay = 12000, InitialDelay = 300 };
        CancellationTokenSource operation;
        readonly bool smoke;
        bool busy, exitChecked, replacing;
        string game;
        GuiUpdate available;
        Settings settings;
        Panel content, home, backupsPage, cachePage, logsPage, settingsPage;
        Label selected, gameState, activity, graphics, cacheState, headerTitle, headerSubtitle;
        RoundProgress progress;
        Label emptyCandidates;
        TextBox statusBox;
        RoundButton detect, browse, verify, install, cancel, update, cacheCheck, restore, refreshBackups;
        RoundButton repositoryLink;
        DataGridView candidates, backups;
        TextBox logBox;
        CheckBox autoUpdate;
        readonly List<RoundButton> navigation = new List<RoundButton>();
        readonly List<string> logs = new List<string>();
        public MainForm(bool smokeTest)
        {
            smoke = smokeTest;
            string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (String.IsNullOrWhiteSpace(documents)) throw new IOException("Windows 文档目录不可用。");
            installer = new Installer(upstream, new ProcessProbe(), Path.Combine(documents, "DLSS帧生成安装管理工具"));
            try { settings = Data.Read<Settings>(Path.Combine(installer.Root, "settings.json")); } catch { settings = new Settings(); }
            if (settings == null) settings = new Settings();
            Text = "DLSS 帧生成安装管理工具  ·  v" + GuiUpdater.Current;
            Font = Theme.Font(9.5f, false);
            BackColor = Theme.Background; ForeColor = Theme.Ink;
            ClientSize = new Size(1160, 780); MinimumSize = new Size(1080, 780);
            StartPosition = FormStartPosition.CenterScreen; AutoScaleMode = AutoScaleMode.None;
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            CreateFluentChrome();
            BuildLayout();
            InitializeDpi();
            Shown += async delegate {
                Log("欢迎使用 GUI v" + GuiUpdater.Current + "。所有安装与恢复均须确认游戏已退出。");
                if (smoke) return;
                try { graphics.Text = await Task.Run(() => ProcessProbe.Graphics()); } catch (Exception e) { graphics.Text = "显卡信息读取失败：" + e.Message; }
                await RefreshCache();
                if (settings.CheckGuiUpdates) await CheckGuiUpdate(false);
            };
            FormClosing += delegate(object sender, FormClosingEventArgs e) {
                if (busy && !replacing)
                {
                    e.Cancel = true;
                    MessageBox.Show(this, "操作正在进行。请先点击“取消当前操作”，等待安全结束后再关闭。", "请稍候", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                lifetime.Cancel();
            };
            FormClosed += delegate { tips.Dispose(); upstream.Dispose(); updater.Dispose(); lifetime.Dispose(); };
        }
        RoundButton Button(string text, int x, int y, int width, EventHandler click, bool secondary)
        {
            var b = new RoundButton { Text = text, Bounds = new Rectangle(x, y, width, 42) };
            if (secondary) { b.Fill = Color.FromArgb(237, 239, 253); b.TextColor = Theme.Accent; }
            b.Click += click; return b;
        }
        Label LabelAt(Control parent, string text, int x, int y, int width, int height, float size, Color color, bool bold)
        {
            var l = Theme.Label(text, size, color, bold); l.SetBounds(x, y, width, height); l.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right; parent.Controls.Add(l); return l;
        }
        void Responsive(Control control, Action layout)
        {
            control.Resize += delegate { layout(); };
            DpiLayoutChanged += delegate { layout(); };
        }
        void BuildLayout()
        {
            var side = new Panel { Dock = DockStyle.Left, Width = 205, BackColor = Theme.Sidebar };
            Surface.Controls.Add(side);
            LabelAt(side, "DLSSG", 26, 26, 156, 42, 25, Theme.Ink, true);
            LabelAt(side, "INSTALL MANAGER", 28, 69, 165, 25, 9, Theme.Muted, false);
            LabelAt(side, "让帧生成安装更简单", 28, 104, 160, 30, 10, Theme.Muted, false);
            string[] labels = { "安装与更新", "备份与恢复", "缓存与更新", "游戏设置", "操作日志" };
            string[] glyphs = { "\uE896", "\uE777", "\uE895", "\uE713", "\uE9D9" };
            for (int i = 0; i < labels.Length; i++)
            {
                int index = i;
                var b = Button(labels[i], 16, 183 + 60 * i, 173, delegate { Navigate(index); }, false);
                b.Height = 46; b.Fill = i == 0 ? Color.FromArgb(222, 233, 246) : side.BackColor;
                b.TextColor = Theme.Ink; b.Glyph = glyphs[i]; b.LeftAligned = true; b.Selected = i == 0;
                side.Controls.Add(b); navigation.Add(b);
            }
            var sideFooter = new Panel { Dock = DockStyle.Bottom, Height = 132 }; side.Controls.Add(sideFooter);
            repositoryLink = Button("GitHub 仓库 ↗", 18, 6, 170, delegate { Open(Convert.ToString(repositoryLink.Tag)); }, true);
            repositoryLink.Tag = GuiUpdater.Repository;
            repositoryLink.Fill = Color.FromArgb(225, 232, 242); sideFooter.Controls.Add(repositoryLink);
            tips.SetToolTip(repositoryLink, GuiUpdater.Repository);
            LabelAt(sideFooter, "GUI v" + GuiUpdater.Current + "\r\n非 NVIDIA 官方工具", 26, 69, 166, 48, 9, Theme.Muted, false);
            var right = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Background, Padding = new Padding(26, 0, 26, 0) };
            Surface.Controls.Add(right); right.BringToFront();
            var header = new Panel { Dock = DockStyle.Top, Height = 122 };
            headerTitle = LabelAt(header, "准备好，让游戏更流畅", 0, 24, 700, 42, 22, Theme.Ink, true);
            headerSubtitle = LabelAt(header, "选择游戏 → 确认退出 → 备份并安装。每一步，都清楚可见。", 2, 72, 820, 28, 10, Theme.Muted, false);
            right.Controls.Add(header);
            var footer = new Panel { Dock = DockStyle.Bottom, Height = 58 };
            activity = LabelAt(footer, "就绪", 0, 10, 380, 38, 9, Theme.Muted, false);
            activity.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
            update = Button("检查 GUI 更新", 0, 10, 225, async delegate { if (available == null) await CheckGuiUpdate(true); else await InstallGuiUpdate(); }, true);
            update.Anchor = AnchorStyles.Right | AnchorStyles.Bottom; update.Left = right.ClientSize.Width - right.Padding.Horizontal - update.Width;
            footer.Controls.Add(update);
            Responsive(footer, delegate { update.Left = footer.ClientSize.Width - update.Width; activity.Width = Math.Max(Px(60), update.Left - Px(18)); });
            tips.SetToolTip(update, "仅检查 GUI 软件版本。红灯表示存在可用更新；游戏 DLL 更新在安装时另行检查。");
            right.Controls.Add(footer);
            content = new Panel { Dock = DockStyle.Fill };
            right.Controls.Add(content); content.BringToFront();
            home = Page(); backupsPage = Page(); cachePage = Page(); settingsPage = Page(); logsPage = Page();
            BuildHome(); BuildBackups(); BuildCache(); BuildGameSettings(); BuildLogs();
            Navigate(0);
        }
        Panel Page()
        {
            var p = new Panel { Dock = DockStyle.Fill, Visible = false, BackColor = Theme.Background };
            content.Controls.Add(p); return p;
        }
        void BuildHome()
        {
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = Padding.Empty, Padding = Padding.Empty };
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 60)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 40)); home.Controls.Add(layout);
            var choice = new Card { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 14) }; layout.Controls.Add(choice, 0, 0);
            LabelAt(choice, "01  选择实际渲染程序", 22, 15, 570, 35, 13, Theme.Ink, true);
            LabelAt(choice, "自动检测前，请打开游戏并进入实际游戏画面。也可以直接选择 EXE。", 22, 53, 790, 28, 9.5f, Theme.Muted, false);
            detect = Button("检测 GPU · 10 秒", 22, 94, 180, async delegate { await Detect(); }, false);
            browse = Button("手动选择 EXE", 214, 94, 156, Browse, true);
            cancel = Button("取消当前操作", 382, 94, 156, delegate { if (operation != null) operation.Cancel(); }, true); cancel.Enabled = false;
            choice.Controls.AddRange(new Control[] { detect, browse, cancel });
            progress = new RoundProgress { Height = 5, Left = 22, Top = 148, Width = 790, Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top };
            choice.Controls.Add(progress);
            candidates = Grid(); candidates.SetBounds(22, 165, 790, 140); candidates.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
            candidates.Columns.Add("Name", "进程"); candidates.Columns.Add("Load", "平均 GPU"); candidates.Columns.Add("Path", "程序路径");
            candidates.Columns[0].Width = 175; candidates.Columns[1].Width = 100; candidates.Columns[2].AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
            candidates.SelectionChanged += delegate { if (!busy && candidates.SelectedRows.Count == 1 && candidates.SelectedRows[0].Tag is Candidate) SelectGame(((Candidate)candidates.SelectedRows[0].Tag).Path); };
            choice.Controls.Add(candidates);
            emptyCandidates = LabelAt(candidates, "游戏检测结果会显示在这里\r\n也可以使用上方“手动选择 EXE”直接继续", 10, 55, 680, 60, 10, Theme.Muted, false);
            emptyCandidates.TextAlign = ContentAlignment.MiddleCenter;
            candidates.RowsAdded += delegate { emptyCandidates.Visible = candidates.Rows.Count == 0; };
            candidates.RowsRemoved += delegate { emptyCandidates.Visible = candidates.Rows.Count == 0; };
            Responsive(choice, delegate { candidates.Width = choice.Width - Px(44); candidates.Height = Math.Max(Px(50), choice.Height - Px(186)); progress.Width = choice.Width - Px(44); emptyCandidates.Width = candidates.Width - Px(20); });
            var deployment = new Card { Dock = DockStyle.Fill, Margin = Padding.Empty }; layout.Controls.Add(deployment, 0, 1);
            LabelAt(deployment, "02  确认退出，然后安全安装", 22, 14, 650, 33, 13, Theme.Ink, true);
            selected = LabelAt(deployment, "尚未选择游戏程序", 22, 51, 790, 34, 9.5f, Theme.Ink, false);
            selected.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            gameState = LabelAt(deployment, "选择后请完全退出游戏，再点击“检查游戏已退出”。", 22, 88, 790, 29, 9.5f, Theme.Muted, false);
            gameState.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            verify = Button("检查游戏已退出", 22, 132, 180, async delegate { await VerifyExit(); }, true); verify.Enabled = false;
            install = Button("备份并安装 / 更新", 214, 132, 228, async delegate { await Install(); }, false); install.Enabled = false;
            deployment.Controls.AddRange(new Control[] { verify, install });
            tips.SetToolTip(selected, "此目录会收到 version.dll 和官方 dlssg_sm86.ini；原文件会先集中备份。");
        }
        DataGridView Grid()
        {
            var g = new DataGridView { BackgroundColor = Color.White, BorderStyle = BorderStyle.None, ReadOnly = true,
                AllowUserToAddRows = false, AllowUserToDeleteRows = false, AllowUserToResizeRows = false, RowHeadersVisible = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false, AutoGenerateColumns = false,
                EnableHeadersVisualStyles = false, GridColor = Theme.Line, ColumnHeadersHeight = 36, RowTemplate = { Height = 35 } };
            g.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle { BackColor = Color.FromArgb(245, 247, 252), ForeColor = Theme.Muted,
                Font = Theme.Font(9, false), SelectionBackColor = Color.FromArgb(245, 247, 252) };
            g.DefaultCellStyle = new DataGridViewCellStyle { BackColor = Color.White, ForeColor = Theme.Ink, SelectionBackColor = Color.FromArgb(237, 239, 253), SelectionForeColor = Theme.Accent, Padding = new Padding(5, 0, 5, 0) };
            g.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal; return g;
        }
        void BuildBackups()
        {
            var card = new Card { Dock = DockStyle.Fill }; backupsPage.Controls.Add(card);
            LabelAt(card, "可恢复的安装记录", 22, 14, 700, 38, 15, Theme.Ink, true);
            LabelAt(card, "集中备份 + 当前所选游戏的旧版备份。恢复前会保留当前文件快照。", 22, 57, 800, 35, 9.5f, Theme.Muted, false);
            refreshBackups = Button("刷新备份记录", 22, 108, 165, async delegate { await RefreshBackups(); }, true);
            restore = Button("恢复选中的记录", 200, 108, 195, async delegate { await Restore(); }, false);
            card.Controls.AddRange(new Control[] { refreshBackups, restore });
            backups = Grid(); backups.SetBounds(22, 169, 790, 340); backups.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            backups.Columns.Add("Date", "备份时间"); backups.Columns.Add("Game", "游戏"); backups.Columns.Add("Version", "DLL 版本"); backups.Columns.Add("State", "状态");
            backups.Columns[0].Width = 175; backups.Columns[1].AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
            backups.Columns[2].Width = 95; backups.Columns[3].Width = 180;
            card.Controls.Add(backups);
            Responsive(card, delegate { backups.Width = card.Width - Px(44); backups.Height = Math.Max(Px(80), card.Height - Px(191)); });
        }
        void BuildCache()
        {
            cachePage.AutoScroll = true;
            var card = new Card { Dock = DockStyle.Top, Height = 550 }; cachePage.Controls.Add(card);
            LabelAt(card, "一个缓存，服务多个游戏", 22, 18, 730, 37, 17, Theme.Ink, true);
            LabelAt(card, "每次安装检查上游提交；未变化就复用缓存。断网时只允许使用校验通过的缓存。", 22, 62, 800, 45, 9.5f, Theme.Muted, false);
            cacheState = LabelAt(card, "缓存尚未检查", 22, 119, 780, 38, 11, Theme.Accent, true);
            cacheCheck = Button("检查 DLL 缓存更新", 22, 173, 205, async delegate { await Run(async token => { await Task.Run(() => installer.CheckCacheAsync(Log, token)); await RefreshCache(); }); }, false);
            card.Controls.Add(cacheCheck);
            card.Controls.Add(Button("打开缓存与备份目录", 240, 173, 220, delegate { Directory.CreateDirectory(installer.Root); Open(installer.Root); }, true));
            LabelAt(card, "显卡信息", 22, 237, 700, 30, 12, Theme.Ink, true);
            graphics = LabelAt(card, "正在读取显卡信息……", 22, 274, 790, 65, 10, Theme.Muted, false);
            autoUpdate = new CheckBox { Text = "启动软件时自动检查 GUI 新版本", Checked = settings.CheckGuiUpdates, AutoSize = true,
                Location = new Point(24, 360), BackColor = Color.White, ForeColor = Theme.Ink };
            autoUpdate.CheckedChanged += delegate {
                settings.CheckGuiUpdates = autoUpdate.Checked;
                try { Directory.CreateDirectory(installer.Root); Data.Json(Path.Combine(installer.Root, "settings.json"), settings); }
                catch (Exception e) { Log("设置保存失败：" + e.Message); }
            };
            card.Controls.Add(autoUpdate);
            LabelAt(card, "检测到新版时，右下角显示红灯。点击后确认下载，校验成功再退出并更新。", 22, 397, 800, 44, 9.5f, Theme.Muted, false);
            card.Controls.Add(Button("GUI 项目与说明 ↗", 22, 465, 205, delegate { Open(GuiUpdater.Repository); }, true));
            tips.SetToolTip(cacheState, installer.CacheRoot);
        }
        void BuildLogs()
        {
            var card = new Card { Dock = DockStyle.Fill }; logsPage.Controls.Add(card);
            LabelAt(card, "每一步都有记录", 22, 15, 600, 40, 15, Theme.Ink, true);
            card.Controls.Add(Button("导出本次日志", 22, 69, 170, delegate {
                using (var dialog = new SaveFileDialog { Filter = "文本日志|*.txt", FileName = "DLSSG_操作日志_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".txt" })
                    if (dialog.ShowDialog(this) == DialogResult.OK) File.WriteAllLines(dialog.FileName, logs.ToArray(), new System.Text.UTF8Encoding(true));
            }, true));
            logBox = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, BorderStyle = BorderStyle.None,
                BackColor = Color.White, ForeColor = Theme.Ink, Font = Theme.Font(9, false), WordWrap = false };
            logBox.SetBounds(22, 132, 790, 380); logBox.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            card.Controls.Add(logBox);
            Responsive(card, delegate { logBox.Width = card.Width - Px(44); logBox.Height = Math.Max(Px(80), card.Height - Px(154)); });
        }
        void BuildGameSettings()
        {
            settingsPage.AutoScroll = true;
            var card = new Card { Dock = DockStyle.Top, Height = 550 }; settingsPage.Controls.Add(card);
            LabelAt(card, "查看安装状态与诊断配置", 22, 18, 750, 38, 16, Theme.Ink, true);
            LabelAt(card, "先在“安装与更新”选择游戏。更改日志等级前必须退出游戏，并自动备份原配置。", 22, 64, 800, 38, 9.5f, Theme.Muted, false);
            card.Controls.Add(Button("读取当前安装状态", 22, 118, 190, async delegate {
                if (game == null) { MessageBox.Show(this, "请先选择游戏程序。"); return; }
                await Run(async token => { statusBox.Text = await Task.Run(() => installer.InstallationStatus(game)); });
            }, false));
            card.Controls.Add(Button("最近一次恢复说明", 225, 118, 190, delegate {
                string path = Path.Combine(installer.Root, "最近一次恢复说明.txt");
                if (File.Exists(path)) Open(path); else MessageBox.Show(this, "还没有集中备份恢复说明。");
            }, true));
            var level = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Bounds = new Rectangle(24, 183, 255, 30) };
            level.Items.AddRange(new object[] { "0 · 关闭日志", "1 · 仅错误（推荐）", "2 · 配置与能力信息", "3 · 内核与调用跟踪" }); level.SelectedIndex = 1; card.Controls.Add(level);
            card.Controls.Add(Button("备份并应用日志等级", 296, 177, 220, async delegate {
                if (game == null) { MessageBox.Show(this, "请先选择游戏程序。"); return; }
                if (MessageBox.Show(this, "修改目标：" + game + "\r\n日志等级：" + level.SelectedIndex + "\r\n将先备份原 DLL 和 INI，是否继续？", "确认配置变更", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
                int selectedLevel = level.SelectedIndex;
                await Run(async token => { await Task.Run(() => installer.SetLogLevel(game, selectedLevel, Log)); });
            }, true));
            statusBox = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BorderStyle = BorderStyle.None, BackColor = Color.White,
                ForeColor = Theme.Muted, Font = Theme.Font(9.5f, false), Text = "尚未读取状态。\r\n\r\n本工具只管理 version.dll 和 dlssg_sm86.ini，不检测或修改游戏反作弊。" };
            statusBox.SetBounds(22, 246, 790, 280); statusBox.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right; card.Controls.Add(statusBox);
            Responsive(card, delegate { statusBox.Width = card.Width - Px(44); statusBox.Height = Math.Max(Px(80), card.Height - Px(268)); });
        }
        void Navigate(int index)
        {
            Panel[] pages = { home, backupsPage, cachePage, settingsPage, logsPage };
            for (int i = 0; i < pages.Length; i++) { pages[i].Visible = i == index; navigation[i].Fill = i == index ? Color.FromArgb(222, 233, 246) : Theme.Sidebar; navigation[i].Selected = i == index; navigation[i].Invalidate(); }
            string[] titles = { "准备好，让游戏更流畅", "可追溯，也可恢复", "文件可复用，版本保持新", "需要的信息，清楚呈现", "操作过程，一目了然" };
            string[] subtitles = { "选择游戏 → 确认退出 → 备份并安装。每一步，都清楚可见。", "恢复不会启动游戏，也不会改动游戏中的其他文件。", "GUI 软件更新与游戏 DLL 更新分别检查，互不混淆。", "查看目标文件哈希，按需调整日志；每次修改前先备份。", "日志只保留在本次窗口中；导出前请留意日志内的本机路径。" };
            headerTitle.Text = titles[index]; headerSubtitle.Text = subtitles[index];
            if (index == 1 && !smoke && !busy) { var task = RefreshBackups(); }
        }
        void SelectGame(string path)
        {
            game = Data.Full(path); selected.Text = game; tips.SetToolTip(selected, game);
            exitChecked = false; verify.Enabled = !busy; install.Enabled = false;
            gameState.Text = "程序已选好。请退出游戏后检查；不会自动关闭或强制结束游戏。"; gameState.ForeColor = Theme.Muted;
            Log("选择程序：" + game);
        }
        void Browse(object sender, EventArgs args)
        {
            using (var dialog = new OpenFileDialog { Filter = "游戏主程序 (*.exe)|*.exe", Title = "选择实际渲染 EXE（不是启动器或卸载程序）", CheckFileExists = true })
                if (dialog.ShowDialog(this) == DialogResult.OK) { candidates.Rows.Clear(); SelectGame(dialog.FileName); }
        }
        async Task Detect()
        {
            if (MessageBox.Show(this, "请启动游戏并进入实际游戏画面，再回到本窗口。\r\n\r\n点击确定开始约 10 秒 GPU 采样。高负载程序未必是游戏，请检查自动选择结果。", "开始 GPU 检测", MessageBoxButtons.OKCancel, MessageBoxIcon.Information) != DialogResult.OK) return;
            await Run(async token => {
                exitChecked = false; progress.Value = 0; candidates.Rows.Clear();
                var result = await Task.Run(() => GpuDetector.Sample((count, elapsed) => Ui(delegate {
                    progress.Value = count; activity.Text = String.Format("GPU 样本 {0}/10 · 实际经过 {1:F1} 秒", count, elapsed);
                }), token), token);
                foreach (Candidate c in result) { int row = candidates.Rows.Add(c.Name, c.Load.ToString("F1") + "%", c.Path); candidates.Rows[row].Tag = c; }
                candidates.Rows[0].Selected = true; SelectGame(result[0].Path);
                Log("已按有效 GPU 负载排序。请核对选中程序，再退出游戏。");
            });
        }
        async Task VerifyExit()
        {
            if (game == null) return;
            await Run(async token => {
                bool running = await Task.Run(() => installer.IsRunning(game));
                exitChecked = !running;
                gameState.Text = running ? "游戏仍在运行，或同名进程路径无法确认。请完全退出后重新检查。" : "已确认游戏退出。可以点击“备份并安装 / 更新”继续。";
                gameState.ForeColor = running ? Color.FromArgb(212, 75, 81) : Color.FromArgb(30, 139, 104);
                Log(gameState.Text);
            });
        }
        async Task Install()
        {
            if (!exitChecked || game == null) return;
            string warning = "目标程序：\r\n" + game + "\r\n\r\n将备份并替换：version.dll、dlssg_sm86.ini。\r\n备份位置：" + installer.BackupRoot +
                "\r\n\r\n反作弊/在线游戏可能封禁或禁止 DLL 注入。本工具不能确认游戏兼容性，也不绕过反作弊。请仅用于允许修改的游戏，并自行确认风险。\r\n\r\n是否继续安装？";
            if (MessageBox.Show(this, warning, "最终确认 · 备份后安装", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            await Run(async token => {
                var b = await Task.Run(() => installer.InstallAsync(game, Log, token));
                gameState.Text = "安装完成 · 上游 " + b.Version + " · 原文件已集中备份"; gameState.ForeColor = Color.FromArgb(30, 139, 104);
                await RefreshCache();
                MessageBox.Show(this, "DLL 和官方 INI 安装成功，备份已校验。\r\n请根据上游说明在游戏内启用帧生成。\r\n\r\n" + b.Directory, "安装完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
            });
        }
        async Task RefreshCache()
        {
            try { cacheState.Text = await Task.Run(() => installer.CacheStatus()); } catch (Exception e) { Log(e.Message); }
        }
        async Task RefreshBackups()
        {
            if (busy) return;
            await Run(async token => {
                var records = await Task.Run(() => installer.Backups(game)); backups.Rows.Clear();
                foreach (Backup b in records)
                {
                    DateTimeOffset time; string date = DateTimeOffset.TryParse(b.CreatedAt, out time) ? time.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss") : b.CreatedAt;
                    int i = backups.Rows.Add(date, Path.GetFileName(b.GameExe), b.Version, b.State); backups.Rows[i].Tag = b;
                    backups.Rows[i].Cells[1].ToolTipText = b.TargetDirectory;
                }
                Log("读取到 " + records.Count + " 条有效备份记录。");
            });
        }
        async Task Restore()
        {
            if (backups.SelectedRows.Count == 0) { MessageBox.Show(this, "请先选择一条备份记录。"); return; }
            var b = backups.SelectedRows[0].Tag as Backup; if (b == null) return;
            if (MessageBox.Show(this, "请完全退出游戏。\r\n\r\n恢复目标：" + b.TargetDirectory + "\r\n备份时间：" + b.CreatedAt +
                "\r\n\r\n只恢复 version.dll 与 dlssg_sm86.ini。继续？", "确认恢复", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            await Run(async token => { await Task.Run(() => installer.Restore(b, Log)); MessageBox.Show(this, "恢复完成，恢复前文件快照已保留。", "完成"); });
        }
        async Task Run(Func<CancellationToken, Task> action)
        {
            if (busy) return;
            busy = true; operation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); SetBusy();
            try { await action(operation.Token); activity.Text = "操作完成 · 详情见操作日志"; }
            catch (OperationCanceledException) { Log("操作已取消，未完成的下载不会进入正式缓存。"); activity.Text = "已安全取消"; }
            catch (Exception e) { Log("失败：" + e.Message); activity.Text = "操作未完成 · 原因已记录"; MessageBox.Show(this, e.Message, "操作未完成", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            finally { busy = false; operation.Dispose(); operation = null; SetBusy(); }
        }
        void SetBusy()
        {
            detect.Enabled = browse.Enabled = cacheCheck.Enabled = refreshBackups.Enabled = restore.Enabled = !busy;
            verify.Enabled = !busy && game != null; install.Enabled = !busy && exitChecked;
            cancel.Enabled = busy; candidates.Enabled = !busy; update.Enabled = !busy;
            UseWaitCursor = busy;
        }
        async Task CheckGuiUpdate(bool interactive)
        {
            if (busy) return;
            update.Enabled = false; update.Text = "正在检查 GUI 版本…";
            try
            {
                available = await updater.Check(lifetime.Token);
                DisplayUpdate(available);
                Log(available == null ? "GUI 版本检查完成，未发现更新。" : "GUI 发现新版本 v" + available.Version + "，点击右下角红灯可更新。");
                if (interactive && available == null) MessageBox.Show(this, "当前 GUI v" + GuiUpdater.Current + "，没有可用的较新正式版本。", "版本检查");
            }
            catch (OperationCanceledException) { }
            catch (Exception e) { update.Text = "GUI 检查失败 · 点击重试"; Log("GUI 更新检查失败（不影响 DLL 安装）：" + e.Message); if (interactive) MessageBox.Show(this, e.Message, "检查失败"); }
            finally { if (!IsDisposed) { update.Enabled = !busy; update.Invalidate(); } }
        }
        async Task InstallGuiUpdate()
        {
            if (available == null || busy) return;
            if (MessageBox.Show(this, "发现 GUI v" + available.Version + "（当前 " + GuiUpdater.Current + "）。\r\n\r\n" +
                (available.Notes ?? "").Substring(0, Math.Min(900, (available.Notes ?? "").Length)) +
                "\r\n\r\n下载并校验成功后会关闭当前窗口、更新程序并重启。缓存与备份不会被删除。\r\n是否立即更新？", "GUI 软件更新", MessageBoxButtons.YesNo, MessageBoxIcon.Information, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            await Run(async token => {
                string stage = await updater.Download(available, installer.Root, Log, token);
                await GuiUpdater.StartReplacement(stage);
                replacing = true; Close();
            });
        }
        internal void DisplayUpdate(GuiUpdate release)
        {
            update.Dot = release != null;
            update.Text = release == null ? "GUI 已是最新 · v" + GuiUpdater.Current : "存在更新 · v" + release.Version;
            update.Fill = release == null ? Color.FromArgb(237, 239, 253) : Color.FromArgb(255, 235, 238);
            update.TextColor = release == null ? Theme.Accent : Color.FromArgb(193, 46, 66);
            update.Invalidate();
        }
        void Ui(Action action)
        {
            if (IsDisposed || !IsHandleCreated) return;
            if (InvokeRequired) BeginInvoke(action); else action();
        }
        void Log(string message)
        {
            Ui(delegate {
                string line = DateTime.Now.ToString("HH:mm:ss") + "  " + message;
                logs.Add(line); logBox.AppendText(line + Environment.NewLine);
                activity.Text = message.Replace("\r", " ").Replace("\n", " "); tips.SetToolTip(activity, message);
            });
        }
        void Open(string target)
        {
            try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
            catch (Exception e) { MessageBox.Show(this, e.Message, "无法打开"); }
        }
    }
}
