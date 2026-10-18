// ============================================================
//  Recorder.cs -- 录制逻辑（唯一的实现）
//
//  2026-10-03 从旧界面的 MainForm 里整段搬出来，为的是让逻辑和界面分开：旧界面 2026-10-12
//  已经删掉（作为独立快照留在 RepRecorder-旧版WinForms\），所以这里现在是**唯一**一份录制逻辑，
//  不会再出现"改了这边忘了那边"。
//
//  硬规矩：这个文件不许碰任何控件类型。所有跟界面有关的事都走 Ui 门面
//  （Ui.Status / Ui.AddRow / Ui.Post …），界面层在 Ui*.cs。
// ============================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace RepRecorder
{
    // ---- "影子控件"：逻辑里本来写的是 WinForms 控件的属性赋值（btnSound.Text = "…"）。
    //      下面这几个代理把赋值转成对 WPF 界面的操作，这样逻辑那边的 60 来处一字不用改，
    //      也不会因为手改而漏掉某一处。名字沿用旧的，纯粹为了少动逻辑。
    sealed class UiButton
    {
        readonly string id;
        readonly Flat flat;
        public UiButton(string automationId) { id = automationId; flat = new Flat(automationId); }
        public string Id { get { return id; } }
        public sealed class Flat
        {
            readonly string id;
            public Flat(string automationId) { id = automationId; }
            public System.Drawing.Color BorderColor { set { Ui.SetButtonBorderColor(id, value); } }
        }
        public string Text { get { return Ui.GetButtonText(id); } set { Ui.SetButtonText(id, value); } }
        public bool Enabled { get { return true; } set { Ui.SetButtonEnabled(id, value); } }
        public System.Drawing.Color ForeColor { set { Ui.SetButtonForeColor(id, value); } }
        public System.Drawing.Color BackColor { set { Ui.SetButtonBackColor(id, value); } }
        public Flat FlatAppearance { get { return flat; } }
        public double Width { set { Ui.SetButtonWidth(id, value); } }
        public void Refresh() { }
    }

    sealed class UiLabel
    {
        readonly string id;
        public UiLabel(string automationId) { id = automationId; }
        public string Text { get { return ""; } set { Ui.SetLabelText(id, value); } }
    }

    public class Recorder
    {

        // ---- 这三处旧版写在 MainForm 里（弹窗和策略混在一起）。现在策略留在这边，弹窗交给 Ui。
        //      AskSaveLocation* 的两个对话框在 Ui.AskDialog 里，返回 Choice：0 留在原地 / 1 另存 / 2 第三个按钮。
        string AskSaveLocationDialog(Job job, int count)
        {
            string result = "keep";
            try
            {
                string where = job == null || string.IsNullOrEmpty(job.Output) ? "" : Path.GetDirectoryName(job.Output);
                string info;
                if (count > 1)
                    info = "这一批 " + count + " 个视频都录好了（例如：" + Path.GetFileName(job.Output) + "，"
                         + (job.Size / 1048576.0).ToString("0.0") + " MB，" + FmtDur(job.VideoSeconds) + "）\r\n"
                         + "现在位置：" + where;
                else
                    info = "已录好：" + Path.GetFileName(job.Output) + "\r\n"
                         + (job.Size / 1048576.0).ToString("0.0") + " MB，时长 " + FmtDur(job.VideoSeconds) + "\r\n"
                         + "现在位置：" + where;
                info += "\r\n\r\n" + (count > 1 ? "这 " + count + " 个视频要放到哪里？" : "你想把这个视频放到哪里？");
                AskResult res = Ui.ShowAskDialog(false, info, cfg == null ? "" : cfg.LastSaveFolder,
                                                    "本队列后面录好的视频也都这样处理（不再询问）");
                if (res == null) return result;
                if (res.Checked) queueSaveDecided = true;
                if (res.Choice == 1 && res.Picked != null)
                {
                    // ⚠ 落盘必须回 UI 线程：这个方法是在 worker 线程上被调的
                    //   （RunParallelBatch / ProcessJob），而 Settings.Save() 是裸 File.WriteAllText，
                    //   界面那边还有个 2 秒的定时器也会 Save —— 两边同时写同一个文件会写坏/丢更新。
                    //   字段赋值留在本线程（那是 worker 自己的状态），只把 Save 这一步挪过去。
                    Ui.Post(delegate { cfg.Save(); });
                    if (res.Checked) queueSaveFolder = res.Picked;
                    result = res.Picked;
                }
                else if (res.Choice == 2)
                {
                    cfg.AskSaveLocation = false;
                    Ui.Post(delegate { cfg.Save(); });
                    Log.Write("已关闭“每次询问保存位置”，可在设置里重新打开");
                }
            }
            catch (Exception ex) { Log.Write("询问保存位置失败：" + ex.Message); }
            return result;
        }

        string AskSaveLocationDialog(Job job) { return AskSaveLocationDialog(job, 1); }

        bool AskSaveLocationUpFront(int count)
        {
            queueSaveDecided = false;
            queueSaveFolder = null;
            bool go = true;
            try
            {
                string info = "这次要录 " + count + " 个 rep" + (count > 6 ? "（会按「同时录制数量」自动分批）" : "") + "。\r\n"
                            + "先选好视频放在哪里，选完就开始录。\r\n\r\n"
                            + "上次用的位置：" + (cfg == null || string.IsNullOrEmpty(cfg.LastSaveFolder) ? "（还没选过）" : cfg.LastSaveFolder);
                // 没有「以后不再询问」那个勾了 —— 用户要求每次开录都问一遍
                AskResult res = Ui.ShowAskDialog(true, info, cfg == null ? "" : cfg.LastSaveFolder, null);
                if (res == null || res.Choice == 2) go = false;
                else
                {
                    if (res.Choice == 1 && res.Picked != null)
                    {
                        queueSaveFolder = res.Picked;
                        // ⚠ 记住这次选的文件夹（2026-10-08 补）。移植时把旧 WinForms
                        //   PickSaveFolder 里那句 cfg.LastSaveFolder = fb.SelectedPath 弄丢了 ——
                        //   全工程再没有第二处写它，于是"上次用的位置"永远显示"（还没选过）"，
                        //   而且 FindExistingOutput 靠它去找已录的文件：另存到别处之后就找不回来，
                        //   那些 rep 下次刷新会变回「等待」，被重录一遍。
                        cfg.LastSaveFolder = res.Picked;
                    }
                    queueSaveDecided = true;
                    cfg.Save();
                }
            }
            catch (Exception ex)
            {
                Log.Write("询问保存位置失败（照常开始录制）：" + ex.Message);
                queueSaveDecided = true;
            }
            if (!go) Log.Write("你在询问里点了「取消（先不录）」——这次不录制");
            else if (!string.IsNullOrEmpty(queueSaveFolder)) Log.Write("本次录好的视频会放到：" + queueSaveFolder);
            else Log.Write("本次录好的视频就放在各自 rep 旁边");
            return go;
        }

        // 把"本批"（钉住的优先）标绿
        void HighlightBatchRows()
        {
            List<Job> batch = pinnedBatch.Count > 0 ? pinnedBatch : selectedBatch;
            Ui.HighlightBatch(batch);
        }

        // 底栏右边的环境状态（旧版是直接改 obsState 那个 Label）
        void RefreshEnvLabel()
        {
            string obsExe = (!string.IsNullOrEmpty(cfg.ObsExe) && File.Exists(cfg.ObsExe)) ? cfg.ObsExe : Settings.FindObsExe();
            string game = (!string.IsNullOrEmpty(cfg.GameDir) && File.Exists(Path.Combine(cfg.GameDir, "th123.exe"))) ? cfg.GameDir : Settings.FindGameDir();
            string s = (obsExe == null) ? "OBS：未安装（winget install --id OBSProject.OBSStudio -e）" : "OBS：已就绪";
            s += (game == null) ? "    游戏：未找到 th123.exe" : "    游戏：已就绪";
            // 2026-10-08：左下角这一行以前**完全不提 ffmpeg** —— 并行录制每一格都会因为缺它而
            // 「切分失败」，而这里是用户唯一会看一眼的地方，写着「已就绪」等于骗人。
            string ffm = null;
            try { ffm = FindFfmpeg(); } catch { }
            if (ffm == null) s += "    ffmpeg：未找到（并行录制需要，单录不用）";
            Ui.Env(s, (obsExe == null || game == null || ffm == null) ? 3 : 0);
        }

        static string MuteFlagPath
        {
            get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "RepRecorder.已静音待恢复"); }
        }

        bool? ReadBgmMuted()
        {
            try
            {
                foreach (string p in BgmIniPaths())
                {
                    string t = File.ReadAllText(p);
                    System.Text.RegularExpressions.Match mm =
                        System.Text.RegularExpressions.Regex.Match(t, @"(?im)^\s*MuteMusic\s*=\s*(\d+)\s*$");
                    if (mm.Success) return mm.Groups[1].Value.Trim() != "0";
                }
            }
            catch { }
            return null;
        }

        // ---- 颜色常量：转发到 Palette（见 Palette.cs）----
        static Color HudOff { get { return Palette.HudOff; } }
        static Color HudOn { get { return Palette.HudOn; } }
        static Color HudDim { get { return Palette.HudDim; } }
        static Color HudLine { get { return Palette.HudLine; } }
        static Color HudText { get { return Palette.HudText; } }
        static Color HudRed { get { return Palette.HudRed; } }
        static Color HudOrange { get { return Palette.HudOrange; } }
        static Color HudAccent2 { get { return Palette.HudAccent2; } }

        // ⚠ 旧版是 MainForm 的构造函数里 cfg = Settings.Load()。搬字段的时候把这一句漏了，
        //   结果逻辑这边的 cfg 一直是 null（启动当场空引用）。现在由界面那边建好同一个实例传进来 ——
        //   必须是【同一个】实例：界面改了并行数、逻辑也在读 cfg。
        public Recorder(Settings settings) { cfg = settings; }

        // ================================================================
        //  给 WPF 界面的公开入口（旧版这些是挂在按钮的 Click 上的私有方法；
        //  现在按钮在界面层，这里开一层很薄的公开壳，逻辑一行没动）
        // ================================================================
        public void UiStart() { OnStart(null, null); }
        public void UiStop() { OnStop(null, null); }
        public void UiClear() { OnClear(null, null); }
        public void UiRemove() { OnRemove(null, null); }
        public void UiMarkBatch() { OnMarkBatch(null, null); }
        public void UiRefreshQueue() { OnRefreshQueue(null, null); }
        public void UiCheck() { OnCheck(null, null); }
        public void UiCheckUpdate() { OnCheckUpdate(null, null); }
        public void UiPrepareObs() { OnPrepare(null, null); }
        public void UiOpenSettings() { OnSettings(null, null); }
        public void UiOpenLog() { OnOpenLog(null, null); }
        public void UiToggleSound() { OnSoundToggle(null, null); }
        public void UiToggleGameAudio() { OnGameAudioToggle(null, null); }
        public void UiToggleBgm() { OnBgmToggle(null, null); }
        public void UiAddPaths(string[] paths) { if (paths != null && paths.Length > 0) AddPaths(paths); }
        public void UiRefreshSoundButtons() { RefreshSoundButtons(); }

        // 后台全盘扫描找到游戏后，界面每 2 秒来取一次（挂在 UiWindow.cs 的 soundTimer 上）。
        // 走定时器而不用回调，是因为那个定时器本来就在 UI 线程上 —— 回填设置在 UI 线程做，
        // 就完全不用跨线程碰控件。特意这么设计：Ui.OnUi 在 Application 还没建好时会直接在
        // 调用线程上跑（Ui.cs: `app == null → return f()`），扫描快的话正好能撞上。
        // 界面上那个 2 秒定时器要做的所有事。以前叫 UiAdoptSearches —— 现在它还管
        // 更新提示，名字得跟上。
        public void UiTick()
        {
            // 后台那个检查更新线程记的时间戳，在这里落盘（设置只许在 UI 线程写）
            if (UpdateCheck.TakeDirty()) { try { cfg.Save(); } catch { } }

            bool got = Settings.AdoptScannedGameDir(cfg);
            if (Settings.AdoptScannedFfmpeg(cfg)) got = true;
            if (got)
            {
                RefreshEnvLabel();
                // 环境卡可能正开着（用户点「检查环境」会强制重新查找，卡是那一刻画的）——
                // 卡里那些行得跟着重画，否则又变成"卡上说未找到、设置里已经填好"。
                try { Ui.RefreshEnvDialogIfOpen(EnvReport.Build(cfg)); } catch { }
            }
            // 等两个自动查找都落定的那个首次自检窗（见 UiOnShown 里那段）。
            // 75 秒兜底：万一扫描线程出什么意外、Running 一直挂着，也不能让这个窗永远不弹
            // —— 那等于把"新用户当场知道缺什么"整个弄没了。
            if (firstRunPending && (!Settings.AnyScanRunning || Environment.TickCount - firstRunSince > 75000))
                ShowFirstRunEnvDialog();
            ShowUpdateIfAny();
        }

        // 更新提示（2026-10-10）。和首检卡一样的规矩：**先把待办清掉再弹** —— 这个窗是
        // 模态的，模态期间定时器照样在跑，不清就会重入、弹出第二个第三个。
        void ShowUpdateIfAny()
        {
            if (firstRunPending) return;     // 先让首检卡把话说完，别两个窗一起糊到脸上
            UpdateInfo up = UpdateCheck.TakePending();
            if (up == null) return;
            try { Ui.ShowUpdateDialog(up); }
            catch (Exception ex) { Log.Write("更新提示弹窗失败（不影响使用）：" + ex.Message); }
        }

        // 旧版是挂在 list.ItemSelectionChanged 上的那句「列表里选中几个 rep，「同时录制数量」就跟着变（用户要求）」。
        // 移植时这根线漏接了（WPF 的 ListBox 没有原生句柄，没人提醒），靠 验证WPF-10 抓出来 —— 补在 HookRecorder 里。
        // 设置窗里改了音频相关的项 → 立刻生效并刷新那几个按钮（Ui.ShowSettings 里调）
        public void UiApplyAudioSettings()
        {
            try { ApplyBgmSetting(true); } catch { }
            try { ApplyGameAudioSetting(); } catch { }
            // 「音频提前」在设置窗里改完要立刻生效（不然要等下一次「准备 OBS」才轮到它）
            try { ApplyAudioLead(); } catch { }
            try { RefreshSoundButtons(); } catch { }
            try { RefreshGameAudioButton(); } catch { }
            try { RefreshBgmButton(); } catch { }
        }

        public void UiOnSelectionChanged()
        {
            try
            {
                if (suppressSelSync) return;
                if (Ui.SelectedJobCount > 0) pinnedBatch.Clear();   // 用户重新选了 → 之前的绿色标记作废
                SyncParallelCountToSelection();
            }
            catch { }
        }

        // 旧版 MainForm.OnFormClosing 的移植（2026-10-05 补）。
        // ⚠ 移植时整块漏了 —— `closing` 这个字段搬过来了却没人用，编译器都报了 CS0414
        //   （"已赋值但从未使用"），当时的收尾没注意到那句话。
        //   后果：录到一半点右上角 ✕，进程直接退，OBS 在后台**一直录下去**；
        //   之后所有 SetVideoSettings / StartRecord 都会失败（交接文档 §十 记过这条）。
        // 返回 true = 现在可以关；false = 已经拦下来去做收尾，窗口先别关（收尾完自己再关一次）。
        public bool UiOnClosing()
        {
            try { cancel = true; } catch { }        // 让正在跑的那一轮尽快收手
            if (!closing && worker != null && worker.IsAlive)
            {
                closing = true;
                Ui.Status("正在停止录制并收尾…");
                Log.Write("关闭窗口：先停止录制并收尾…");
                Thread t = new Thread(delegate()
                {
                    try { worker.Join(20000); } catch { }
                    Ui.CloseMain();                 // 收尾完了再关（那时候 closing 已是 true，放行）
                });
                t.IsBackground = true;
                t.Start();
                return false;
            }
            try { cfg.Save(); } catch { }
            return true;
        }

        // 首次自检那个窗还没弹过 —— 在等自动找游戏落定（见 UiOnShown 里那段注释）
        bool firstRunPending;
        int firstRunSince;

        // 「检查更新」正在查（防止连点：按钮也是靠它变灰的）
        bool checkingUpdate;

        // 首次自检：只报事实，不修任何东西。
        // ⚠ 先把 EnvChecked 落盘再弹：这个窗是**模态**的（Ui.ShowEnvDialog → ShowDialog），
        //   用户在窗开着的时候把程序结束掉，放在后面那句就永远执行不到、下次启动又弹一遍。
        void ShowFirstRunEnvDialog()
        {
            // ⚠ 必须在 ShowDialog 之前清掉：模态期间定时器照样在跑，不清就会重入、
            //   弹出第二个第三个一模一样的窗。
            firstRunPending = false;
            cfg.EnvChecked = true;
            cfg.Save();
            try
            {
                EnvReport rep0 = EnvReport.Build(cfg);
                if (!rep0.AllOk) Ui.ShowEnvDialog(rep0);
                RefreshEnvLabel();   // 用户可能顺手在设置里把游戏目录指对了
            }
            catch (Exception ex) { Log.Write("首次自检弹窗失败（不影响使用）：" + ex.Message); }
        }

        // 删掉上一次更新留下的 <自己>.old。
        //
        // ⚠ 必须重试，不能只删一次（2026-10-11 用户报的"老版没有自动删除"）。
        //   更新完成之后是【当场自动重启】的，新进程起来的那一刻旧进程通常还没退干净；
        //   而 .old 正是旧进程的镜像文件 —— 只要它还在跑，Windows 就不让删（共享冲突）。
        //   实测复现：起 A → 把 A 的镜像改名成 .old → 起 B，B 删不掉；
        //             杀掉 A 之后再起 C，删得掉。
        //   一次就放弃的话，用户每次更新完都会看到一个赖着不走的 RepRecorder.exe.old，
        //   要等他自己【再手动开一次】才会消失 —— 那正是用户看到的"没自动删"。
        //   这里丢到后台每 0.5 秒试一次（旧进程一般一两秒内就退了），试满 15 秒还不行就算了，
        //   免得白占着线程：那种情况下次启动还会再来一遍。
        void CleanupOldExe()
        {
            string self = UpdateCheck.RunningExePath;
            if (string.IsNullOrEmpty(self)) return;
            string oldSelf = self + ".old";
            if (!File.Exists(oldSelf)) return;              // 绝大多数启动走这条，不建线程
            Thread t = new Thread(delegate()
            {
                try
                {
                    for (int i = 0; i < 30; i++)
                    {
                        if (!File.Exists(oldSelf)) return;
                        try
                        {
                            File.Delete(oldSelf);
                            if (i > 0) Log.Write("清掉了上次更新留下的旧程序：" + Path.GetFileName(oldSelf)
                                                 + "（等了 " + (i * 0.5) + " 秒，旧进程退干净才删得掉）");
                            return;
                        }
                        catch { }
                        Thread.Sleep(500);
                    }
                    Log.Write("旧程序 " + Path.GetFileName(oldSelf) + " 还被占着，这次没删掉，下次启动再试。");
                }
                catch { }
            });
            t.IsBackground = true;
            t.Name = "清理旧exe";
            t.Start();
        }

        // 启动那一段（旧版写在 MainForm 的 Shown 里，顺序照搬）
        public void UiOnShown(string[] args)
        {
            try
            {
                cfg.Save();
                RefreshEnvLabel();
                RefreshSoundButtons();
                RefreshGameAudioButton();
                Ui.SetBusy(false);
                Ui.Status("就绪");
                RestoreSoundIfLeftMuted();
                Log.RotateOnStart();
                // 查更新。⚠ 必须在日志轮转【之后】起 —— 它那几行日志才留得住。
                // 后台线程、失败静默；6 小时内查过就直接跳过。
                UpdateCheck.StartBackgroundCheck(cfg);
                // 上一次更新留下的旧 exe，顺手清掉（细节见 CleanupOldExe）。
                CleanupOldExe();
                Log.Write("==== Rep → MP4 录制器 v" + AppInfo.Version + " 启动 ====");
                Log.Write("界面：WPF 版");   // 日志轮转之后写，这一行才留得住
                Log.Write("游戏目录：" + (string.IsNullOrEmpty(cfg.GameDir) ? "<未找到>" : cfg.GameDir));
                Log.Write("OBS     ：" + (string.IsNullOrEmpty(cfg.ObsExe) ? "<未找到>" : cfg.ObsExe));
                SyncBgmFromIni();
                if (args != null && args.Length > 0)
                {
                    // 只入队，不自动开录 —— 2026-10-05 用户要求「拖入后自动开始」整条去掉，
                    // 一律由用户自己点「开始录制」。
                    AddPaths(args);
                }
                // 首次启动自检（2026-10-08）：新用户拿到包，双击之后必须当场知道缺什么。
                // 只在【有问题时】弹，而且一台机器只弹一次（EnvChecked）—— 环境正常的人永远看不到它，
                // 想看随时点「检查环境」，那个永远会弹。
                // 2026-10-09（用户报的）：自动找游戏**还在跑**的时候不能弹这个窗。
                //   它是一次性快照，弹出来之后内容再也不会变；而扫描要一两秒才回来、
                //   回来才把 GameDir 填进设置。启动那一瞬就照"游戏目录：<未找到>"建卡，
                //   用户隔一秒进设置却看到路径已经填好了 —— 卡片和设置互相矛盾。
                //   所以扫描在跑就等着，落定之后再弹，看到的才是终值。
                if (!cfg.EnvChecked)
                {
                    if (Settings.AnyScanRunning) { firstRunPending = true; firstRunSince = Environment.TickCount; }
                    else ShowFirstRunEnvDialog();
                }
            }
            catch (Exception ex) { Log.Write("启动初始化出错：" + ex); }
        }

        // 影子控件：逻辑里本来写的是控件属性赋值（btnSound.Text = "…"），现在这些都走 Ui 门面；
        // 这几个字段只是为了在同一处保留"这批按钮叫什么名"，名字和界面上的 AutomationId 一致。
        readonly UiButton btnStart = new UiButton("btnStart");
        readonly UiButton btnStop = new UiButton("btnStop");
        readonly UiButton btnSound = new UiButton("btnSound");
        readonly UiButton btnGameAudio = new UiButton("btnGameAudio");
        readonly UiButton btnBgm = new UiButton("btnBgm");
        readonly UiLabel lblParHint = new UiLabel("lblParHint");
        readonly Settings cfg;
        readonly List<Job> jobs = new List<Job>();
        readonly object jobsLock = new object();
        Thread worker;
        volatile bool cancel;
        volatile bool prepareBusy;          // 「准备 OBS」正在后台跑（见 OnPrepare）：挡连点，也挡开录
        volatile bool titleWatch = false;   // 并行录制期间盯着窗口标题，防止游戏改回去导致三格抢同一窗口
        ObsClient obs = new ObsClient();
        string captureSource = null;    // 采集源名字
        string captureScene = null;     // 用来截图自检的场景名
        bool captureSourceIsOurs = false;
        string originalScene = null;
        string originalRecordDir = null;
        bool closing = false;
        int currentMethod = -1;   // 当前用的捕获方式
        int blackStreak = 0;      // 连续检测到黑屏的次数
        bool gameMutedByUs = false;      // 录制期间是否由我们静音了游戏声音
        bool weMutedSystem = false;      // 系统静音是不是我们按的（录完要自动恢复）
        double jobMaxGameLevel = 0;      // 本次录制中游戏音频的最大电平（音频自检用）
        string queueSaveFolder = null;   // 本队列统一保存到的文件夹
        bool queueSaveDecided = false;   // 本队列是否已经问过/决定过
        const string RecSceneName = "RepRecorder 录制场景";
        const string RecSourceName = "RepRecorder 游戏窗口";
        const string AudioSourceName = "RepRecorder 游戏声音";

        // 成品的落点。平时就是「和 rep 同名」那一份；但用户在询问里选了
        // 「再录一次（旧的保留，新的另存）」时，绝不能让新视频盖掉旧的那份，
        // 于是改成 xxx (2).mp4、xxx (3).mp4 …… 一直往后找一个没人用的名字。
        // ⚠ 找名字要【到真开录那一刻】再做（所以这里是每次调用现算，不是提前存进 job）：
        //   一批里同一个 rep 被排了两次、或者上一条还没写完，都会让"提前定好的名字"撞车。
        string OutputPathFor(Job job, string ext)
        {
            string plain = Path.ChangeExtension(job.Rep, ext);
            if (job == null || !job.KeepOld) return plain;
            try
            {
                string dir = Path.GetDirectoryName(job.Rep);
                string name = Path.GetFileNameWithoutExtension(job.Rep);
                for (int n = 2; n < 1000; n++)
                {
                    string cand = Path.Combine(dir, name + " (" + n + ")" + ext);
                    if (!File.Exists(cand)) return cand;
                }
            }
            catch { }
            return plain;      // 兜底：真找不到空名字就还是老地方（宁可覆盖也别录不出来）
        }

        string FindExistingOutput(Job job)
        {
            try
            {
                if (!string.IsNullOrEmpty(job.Output) && File.Exists(job.Output)) return job.Output;
                string[] exts = new string[] { ".mp4", ".mkv", ".flv", ".mov" };
                foreach (string e in exts)
                {
                    string p = Path.ChangeExtension(job.Rep, e);
                    if (File.Exists(p)) return p;
                }
                if (!string.IsNullOrEmpty(cfg.LastSaveFolder) && Directory.Exists(cfg.LastSaveFolder))
                {
                    string baseName = Path.GetFileNameWithoutExtension(job.Rep);
                    foreach (string e in exts)
                    {
                        string p = Path.Combine(cfg.LastSaveFolder, baseName + e);
                        if (File.Exists(p)) return p;
                    }
                }
            }
            catch { }
            return null;
        }
        IntPtr WaitForMainWindow(Process p, int seconds)
        {
            DateTime end = DateTime.Now.AddSeconds(seconds);
            while (DateTime.Now < end)
            {
                Thread.Sleep(400);
                try
                {
                    p.Refresh();
                    if (p.HasExited) return IntPtr.Zero;
                    if (p.MainWindowHandle != IntPtr.Zero) return p.MainWindowHandle;
                }
                catch { return IntPtr.Zero; }
            }
            return IntPtr.Zero;
        }
        void MoveWindowClientTo(IntPtr h, int x, int y)
        {
            RECT wr, cr; POINT pt;
            if (!GetWindowRect(h, out wr) || !GetClientRect(h, out cr)) return;
            pt.X = 0; pt.Y = 0;
            if (!ClientToScreen(h, ref pt)) return;
            int dx = pt.X - wr.Left;
            int dy = pt.Y - wr.Top;
            SetWindowPos(h, IntPtr.Zero, x - dx, y - dy, wr.Right - wr.Left, wr.Bottom - wr.Top, SWP_NOZORDER | SWP_NOACTIVATE);
        }
        // 按设置把每个游戏窗口设成「640×480 × GameScale」—— 就是游戏里的 Alt+大键盘1/2/3。
        // **以设置为准**：比目标大的也缩下来（2026-10-13 用户拍板："连缩也管"）。
        //
        // 为什么要有这个：每格采到的像素就是游戏窗口的**真实像素**。窗口 640×480 而成品是
        // 1440×1080，就必然放大 2.25 倍（糊）；1280×960 只放大 1.125 倍（几乎原生）。
        // 而窗口大小平时由 WindowResizer 模块的 ini 决定（整合包默认 1x=640×480），用户不会去改它。
        //
        // 实测依据（2026-10-13，本机）：外部 SetWindowPos 之后，游戏是**按新尺寸重新渲染**的
        //   —— 抓同一局的画面比过：六角形/HUD 的硬边像素块和原生时同一风格，不是"把 640×480 拉大"。
        //   另外实测：那个模块的 Alt+1/2/3 只对当次运行有效，尺寸只在游戏【正常退出】时才写回 ini，
        //   而本程序结束一批时是**强杀**游戏 —— 所以"让用户自己按键"在录制流程里根本留不下来。
        //
        // 不动用户磁盘上的游戏配置：这里只改运行时的窗口，强杀不会触发写回。
        // 唯一的安全闸门：屏幕工作区装不下所选档 → 退回能装下的最大档。否则窗口跑到屏幕外，
        // 那块没被 Windows 合成的内容会被采成黑/花。
        void ApplyGameScale(List<IntPtr> handles)
        {
            try
            {
                int mult = cfg.GameScale;
                if (mult < 1) mult = 1;
                if (mult > 3) mult = 3;
                RECT wa;
                int workW = 1920, workH = 1080;
                if (SystemParametersInfo(SPI_GETWORKAREA, 0, out wa, 0) && wa.Right > 0 && wa.Bottom > 0)
                {
                    workW = wa.Right; workH = wa.Bottom;
                }
                int fit = 0;
                for (int m = mult; m >= 1; m--)          // 从所选档往下找第一个装得下的（+320/+60 是下面摆窗口用的偏移）
                {
                    if (640 * m + 320 <= workW && 480 * m + 60 <= workH) { fit = m; break; }
                }
                if (fit == 0)
                {
                    Log.Write("并行：屏幕连 Alt+1（640×480）都放不下，这一批不改游戏窗口大小");
                    return;
                }
                if (fit != mult)
                    Log.Write("并行：屏幕装不下 Alt+" + mult + "（" + (640 * mult) + "×" + (480 * mult) +
                              "），改按 Alt+" + fit + " 设");
                int tw = 640 * fit, th = 480 * fit;
                int done = 0;
                for (int pass = 0; pass < 2; pass++)     // 设完复核一次：模块启动时也会按 ini 设一次，谁先谁后不定
                {
                    int n = 0;
                    foreach (IntPtr h in handles)
                    {
                        if (h == IntPtr.Zero) continue;
                        int cw, chh;
                        if (!ClientSizeOf(h, out cw, out chh)) continue;
                        if (cw == tw && chh == th) continue;      // 已经是这个尺寸：不动
                        SetClientSize(h, tw, th);
                        n++;
                    }
                    if (n == 0) break;
                    done = n;
                    Thread.Sleep(1500);              // 等游戏重建渲染目标
                }
                if (done > 0)
                    Log.Write("并行：已把 " + done + " 个游戏窗口设为 " + tw + "×" + th +
                              "（按设置「游戏画面大小」= Alt+" + fit + "）");
            }
            catch (Exception ex) { Log.Write("并行：设置游戏窗口大小失败（不影响录制）：" + ex.Message); }
        }
        bool ClientSizeOf(IntPtr h, out int cw, out int ch)
        {
            cw = ch = 0;
            RECT cr;
            if (!GetClientRect(h, out cr)) return false;
            if (cr.Right <= 0 || cr.Bottom <= 0) return false;
            double sc = WindowPixelScale(h);
            cw = (int)Math.Round(cr.Right * sc);
            ch = (int)Math.Round(cr.Bottom * sc);
            return true;
        }
        void SetClientSize(IntPtr h, int cw, int ch)
        {
            RECT wr, cr; POINT pt;
            if (!GetWindowRect(h, out wr) || !GetClientRect(h, out cr)) return;
            pt.X = 0; pt.Y = 0;
            if (!ClientToScreen(h, ref pt)) return;
            int fw = (wr.Right - wr.Left) - cr.Right;      // 边框总宽
            int fh = (wr.Bottom - wr.Top) - cr.Bottom;
            SetWindowPos(h, IntPtr.Zero, wr.Left, wr.Top, cw + fw, ch + fh, SWP_NOZORDER | SWP_NOACTIVATE);
        }

        bool ForceAllWindowTitles(List<IntPtr> handles, List<string> wants, int waitSeconds)
        {
            try
            {
                int n = handles.Count;
                string[] last = new string[n];
                int[] stable = new int[n];
                DateTime end = DateTime.Now.AddSeconds(waitSeconds);
                while (DateTime.Now < end)
                {
                    bool allStable = true;
                    for (int i = 0; i < n; i++)
                    {
                        if (handles[i] == IntPtr.Zero) continue;
                        StringBuilder sb = new StringBuilder(512);
                        GetWindowText(handles[i], sb, 512);
                        string cur = sb.ToString();
                        if (cur == last[i]) stable[i]++;
                        else { stable[i] = 0; last[i] = cur; }
                        if (stable[i] < 3) allStable = false;
                    }
                    if (allStable) break;
                    Thread.Sleep(400);
                }
                for (int round = 0; round < 15; round++)
                {
                    for (int i = 0; i < n; i++)
                        if (handles[i] != IntPtr.Zero) SetWindowText(handles[i], wants[i]);
                    Thread.Sleep(300);
                    bool all = true;
                    for (int i = 0; i < n; i++)
                    {
                        if (handles[i] == IntPtr.Zero) continue;
                        StringBuilder sb = new StringBuilder(512);
                        GetWindowText(handles[i], sb, 512);
                        if (sb.ToString() != wants[i]) { all = false; break; }
                    }
                    if (all && round >= 2) return true;
                }
                return false;
            }
            catch { return false; }
        }
        static string MakeWindowString(string title, string cls)
        {
            return (title == null ? "" : title) + ":" + (cls == null ? "" : cls) + ":th123.exe";
        }
        void EnsureInput(string name, string kind, Dictionary<string, object> settings)
        {
            List<object> inputs = ObsClient.ToList(obs.Request("GetInputList", null)["inputs"]);
            foreach (object o in inputs)
            {
                Dictionary<string, object> m = (Dictionary<string, object>)o;
                if (Convert.ToString(m["inputName"]) == name) return;
            }
            Dictionary<string, object> req = new Dictionary<string, object>();
            req["sceneName"] = RecSceneName;
            req["inputName"] = name;
            req["inputKind"] = kind;
            req["inputSettings"] = settings;
            req["sceneItemEnabled"] = true;
            obs.Request("CreateInput", req);
            Log.Write("并行：已创建源 " + name);
        }
        static Dictionary<string, object> AudioInputSettings(string window)
        {
            Dictionary<string, object> a = new Dictionary<string, object>();
            a["window"] = window;
            a["priority"] = 1;
            return a;
        }
        static Dictionary<string, object> VideoInputSettings(string window)
        {
            Dictionary<string, object> v = new Dictionary<string, object>();
            v["window"] = window;
            v["method"] = 2;          // WGC
            v["client_area"] = true;
            v["cursor"] = false;
            v["priority"] = 1;
            return v;
        }
        void ApplyCellWindow(Dictionary<string, string> applied, string vName, string aName, string window)
        {
            string old;
            if (applied.TryGetValue(vName, out old) && old == window) return;
            SetInputSettingsRaw(vName, VideoInputSettings(window));
            SetInputSettingsRaw(aName, AudioInputSettings(window));
            applied[vName] = window;
            applied[aName] = window;
            // 走到这里说明窗口串真的变了（= OBS 会重建采集设备，画面会闪一帧黑）；
            // 窗口串没变时上面已经 return，不会闪 —— 这就是"第 9~10 秒一帧黑"的由来与修法。
            Log.Write("并行：重设采集源绑定 " + vName + " → " + window);
        }
        void SetInputSettingsRaw(string inputName, Dictionary<string, object> settings)
        {
            Dictionary<string, object> r = new Dictionary<string, object>();
            r["inputName"] = inputName;
            r["inputSettings"] = settings;
            obs.Request("SetInputSettings", r);
        }
        void SetTransformRaw(string scene, int itemId, int x, int y, int cw, int ch)
        {
            if (itemId < 0) return;
            Dictionary<string, object> t = new Dictionary<string, object>();
            t["positionX"] = (double)x;
            t["positionY"] = (double)y;
            t["scaleX"] = 1.0;
            t["scaleY"] = 1.0;
            // 同样用边界框：源还没解析出尺寸时也能正确铺满格子
            t["boundsType"] = cw > 0 && ch > 0 ? "OBS_BOUNDS_SCALE_INNER" : "OBS_BOUNDS_NONE";
            t["boundsAlignment"] = 5;
            if (cw > 0) t["boundsWidth"] = (double)cw;
            if (ch > 0) t["boundsHeight"] = (double)ch;
            Dictionary<string, object> r = new Dictionary<string, object>();
            r["sceneName"] = scene;
            r["sceneItemId"] = itemId;
            r["sceneItemTransform"] = t;
            obs.Request("SetSceneItemTransform", r);
        }
        void SetSceneItemEnabled(string scene, int itemId, bool enabled)
        {
            if (itemId < 0) return;
            try
            {
                Dictionary<string, object> r = new Dictionary<string, object>();
                r["sceneName"] = scene;
                r["sceneItemId"] = itemId;
                r["sceneItemEnabled"] = enabled;
                obs.Request("SetSceneItemEnabled", r);
            }
            catch (Exception ex) { Log.Write("并行：切换元素显示状态失败：" + ex.Message); }
        }
        void SetTransformFitCell(string scene, int itemId, int x, int y, int cw, int ch, int idx)
        {
            if (itemId < 0) { Log.Write("并行：第 " + idx + " 格元素没找到（itemId<0），位置没设上"); return; }
            try
            {
                Dictionary<string, object> t = new Dictionary<string, object>();
                t["positionX"] = (double)x;
                t["positionY"] = (double)y;
                t["scaleX"] = 1.0;
                t["scaleY"] = 1.0;
                t["boundsType"] = "OBS_BOUNDS_SCALE_INNER";   // 等比缩放到正好装进边界框
                t["boundsAlignment"] = 5;                     // 对齐到左上
                t["boundsWidth"] = (double)cw;
                t["boundsHeight"] = (double)ch;
                Dictionary<string, object> req = new Dictionary<string, object>();
                req["sceneName"] = scene;
                req["sceneItemId"] = itemId;
                req["sceneItemTransform"] = t;
                obs.Request("SetSceneItemTransform", req);
                // 回读一次，记下 OBS 报的源尺寸（排查用；不影响摆放）
                double sw = 0, sh = 0;
                try
                {
                    Dictionary<string, object> q2 = new Dictionary<string, object>();
                    q2["sceneName"] = scene;
                    q2["sceneItemId"] = itemId;
                    Dictionary<string, object> r2 = obs.Request("GetSceneItemTransform", q2);
                    if (r2.ContainsKey("sceneItemTransform"))
                    {
                        Dictionary<string, object> t2 = (Dictionary<string, object>)r2["sceneItemTransform"];
                        if (t2.ContainsKey("sourceWidth")) sw = Convert.ToDouble(t2["sourceWidth"]);
                        if (t2.ContainsKey("sourceHeight")) sh = Convert.ToDouble(t2["sourceHeight"]);
                    }
                }
                catch { }
                Log.Write("并行：第 " + idx + " 格摆到 (" + x + "," + y + ")，缩放到 " + cw + "×" + ch);
            }
            catch (Exception ex) { Log.Write("并行：第 " + idx + " 格设置位置失败 " + ex.Message); }
        }
        void SetAudioTracksRaw(string inputName, int trackNo)
        {
            Dictionary<string, object> tracks = new Dictionary<string, object>();
            for (int k = 1; k <= 6; k++) tracks[k.ToString()] = (k == trackNo);
            Dictionary<string, object> r = new Dictionary<string, object>();
            r["inputName"] = inputName;
            r["inputAudioTracks"] = tracks;
            obs.Request("SetInputAudioTracks", r);
        }
        void SetInputMuteRaw(string inputName, bool muted)
        {
            try
            {
                Dictionary<string, object> s = new Dictionary<string, object>();
                s["inputName"] = inputName;
                s["inputMuted"] = muted;
                obs.Request("SetInputMute", s);
            }
            catch { }
        }

        // 设置项「音频提前」的当前值，夹在 ±2000ms 防呆。
        int AudioLeadValue()
        {
            int lead = cfg == null ? 0 : cfg.AudioLeadMs;
            if (lead > 2000) lead = 2000;
            if (lead < -2000) lead = -2000;
            return lead;
        }

        // 给一个音源设「音频提前」。⚠ OBS 那边的语义和设置项是【反的】：
        //   OBS 的 sync offset 正数 = 声音往后拖，而设置项 AudioLeadMs 正数 = 往前挪，
        //   所以这里取负号。搞反了症状会翻倍，不是"没效果"。
        bool audioLeadWarned = false;    // 只在第一次失败时吭一声，免得每格都刷屏
        void SetAudioLeadRaw(string inputName, int leadMs)
        {
            try
            {
                Dictionary<string, object> r = new Dictionary<string, object>();
                r["inputName"] = inputName;
                r["inputAudioSyncOffset"] = -leadMs;      // 毫秒；负数 = 声音提前
                obs.Request("SetInputAudioSyncOffset", r);
            }
            catch (Exception ex)
            {
                // ⚠ 别静默吞掉：这句失败 = 补偿完全没生效，而界面上照样写着"80"。
                //   老 OBS 没有这条请求的话，用户至少能在日志里看到原因。
                if (!audioLeadWarned)
                {
                    audioLeadWarned = true;
                    Log.Write("⚠ 设置「音频提前」失败（" + inputName + "）：" + ex.Message +
                              " —— 这个 OBS 可能不支持 SetInputAudioSyncOffset，声音不会做补偿");
                }
            }
        }

        // 把「音频提前」应用到所有【我们自己建的游戏音源】：
        // 单录的「RepRecorder 游戏声音」+ 并行的「RepRecorder P1~P6 声音」。
        // ⚠ 故意不碰 OBS 的「桌面音频」：那是另一条采集路径（wasapi_output_capture），
        //   延迟特性和进程回环（wasapi_process_output_capture）不是一回事，拿这个数字去调它
        //   没有依据；而且默认「只录游戏声音」时它本来就是静音的，调了也听不出来。
        void ApplyAudioLead()
        {
            int lead = AudioLeadValue();
            SetAudioLeadRaw(AudioSourceName, lead);
            for (int i = 1; i <= 6; i++) SetAudioLeadRaw("RepRecorder P" + i + " 声音", lead);
        }
        void SetProfileParamRaw(string cat, string name, string val)
        {
            Dictionary<string, object> r = new Dictionary<string, object>();
            r["parameterCategory"] = cat;
            r["parameterName"] = name;
            r["parameterValue"] = val;
            obs.Request("SetProfileParameter", r);
        }
        // internal（不是 private）：EnvReport 要问它 ffmpeg 在不在，好把结论一起报给用户。
        // 实现只有一份，在 Core.cs 的 Settings 里（那边还要做走盘 + 实跑校验，放不到这儿）。
        // 保留这个名字当转发器：调用点多，而且 EnvReport 认的就是它。
        // ⚠ 改之前这里是【一整份和 Core.cs 逐字相同的副本】—— 加一个查找位置要改两处，
        //   漏一处的表现是"旧界面找得到、新界面找不到"。2026-10-10 合并成一份。
        internal static string FindFfmpeg() { return Settings.FindFfmpeg(); }

        // 一格的「往里裁一圈 + 放大」参数。逐格切（SplitOne）和一组多路切（SplitGroup）共用这一份 ——
        // 以前只有 SplitOne 一处，分组切分如果自己再写一遍，两条路径的成品就会不一样。
        class CellCrop
        {
            public int InX, InY, Cw2, Ch2;
            public int InsetOut, OutW, OutH;      // 只在打日志时用（裁了多少成品像素、成品多大）
            public string Filter = "";      // 形如 crop=w:h:x:y[,scale=W:H:flags=...]
        }
        CellCrop CellCropOf(int cw, int ch, int x, int y)
        {
            CellCrop c = new CellCrop();
            // 往里裁一圈再缩放：并行时相邻格的采集源是"亚像素"缩放摆的（边界框），格与格之间会漏
            // 1~3 像素的邻居画面（亮）或画布黑边（暗）进来；整边裁就会把那条线带进成品。
            // 用户实测：成品最右 2~3 像素亮度 43 → 77 的断崖。往里裁 6 个成品像素后断崖消失（最大跳变 15 → 3.7）。
            int insetOut = cfg.ParallelEdgeInset;
            if (insetOut < 0) insetOut = 0;
            int outW = cfg.CanvasWidth > 0 ? cfg.CanvasWidth : 1440;
            c.InsetOut = insetOut;
            c.OutW = outW;
            c.OutH = cfg.CanvasHeight > 0 ? cfg.CanvasHeight : 1080;
            c.InX = outW > 0 ? (int)Math.Round((double)insetOut * cw / outW) : 0;
            c.InY = cw > 0 ? (int)Math.Round((double)c.InX * ch / cw) : 0;
            if (c.InX * 2 >= cw - 16) c.InX = 0;      // 裁太多就别裁了（防呆）
            if (c.InY * 2 >= ch - 16) c.InY = 0;
            c.Cw2 = cw - c.InX * 2;
            c.Ch2 = ch - c.InY * 2;
            c.Filter = "crop=" + c.Cw2 + ":" + c.Ch2 + ":" + (x + c.InX) + ":" + (y + c.InY);
            // 像素点采样（neighbor）而不是 lanczos：游戏是 640×480 的像素画，
            // 用 lanczos 放大到 1440×1080 会整体发糊（用户实测：和别人的录像一比"清晰程度差很多"）。
            // neighbor 保留硬边像素块，和游戏本身/别人录出来的观感一致。
            // ⚠ 条件要看【裁完之后】的尺寸，不能看"格子比画布小"：
            //   Alt+3 那档格子本来就正好是 1440×1080，裁掉 6 像素边之后是 1428×1072，
            //   老写法（`cfg.CanvasWidth > cw` → 1440 > 1440 为假）就不缩了 ——
            //   成品会变成 1428×1072，和别的录像尺寸不一致（2026-10-13 实测到的）。
            if (cfg.CanvasWidth != c.Cw2 || cfg.CanvasHeight != c.Ch2)
                c.Filter += ",scale=" + cfg.CanvasWidth + ":" + cfg.CanvasHeight + ":flags=lanczos";
            //   ↑ 上面三行是当初选 neighbor 的理由，但用户后来指定回到 lanczos：这是 9/27 22:55
            //     那版（用户觉得"很清晰"的那版）的放大方式。最终决定＝lanczos，别照上面那段改回 neighbor。
            return c;
        }
        bool SplitOne(string grid, Job job, int idx, int cw, int ch, int x, int y, double startSec, double maxSeconds, out string err)
        {
            err = "";
            string ff = FindFfmpeg();
            if (ff == null) { err = "找不到 ffmpeg"; return false; }
            // 「再录一次（旧的保留）」时这个名字不是 xxx.mp4 而是 xxx (2).mp4 —— 见 OutputPathFor。
            string target = OutputPathFor(job, ".mp4");
            string enc = EncName();
            // 编码器 + 各自合适的参数 + "换编码器重试"的梯子，都在 EncArgsFor 里（理由写在那边）：
            // 别人的电脑不一定是 N 卡 —— A 卡走 h264_amf，没有独显就走 libx264 软编，
            // 一路自动往下退，保证一定出得来成品。
            List<string> encArgs = EncArgsFor(enc, cfg.ParallelSplitCQ);
            if (startSec < 0) startSec = 0;
            double dur = maxSeconds - startSec;   // 这格真正要的时长
            string seek = (startSec > 0.2)
                ? " -ss " + startSec.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)
                : "";
            string baseArgs = "-hide_banner -y" + seek + " -i \"" + grid + "\" -map 0:v:0";
            CellCrop cc = CellCropOf(cw, ch, x, y);
            if (cc.InX > 0)
                Log.Write("并行切分：每格往里裁 " + cc.InX + " 像素（格子坐标）≈ 成品 " + cc.InsetOut + " 像素，" +
                          "裁后 " + cc.Cw2 + "×" + cc.Ch2 + " 再缩放到 " + cc.OutW + "×" + cc.OutH);
            // 结尾淡出：并行切分本来就要重编码，加这个不额外花钱，避免"最后一帧硬切"的观感
            string fadeV = "", fadeA = "";
            if (dur > 1.5)
            {
                string st = (dur - 0.6).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
                fadeV = ",fade=t=out:st=" + st + ":d=0.6";
                fadeA = " -af afade=t=out:st=" + st + ":d=0.6";
            }
            string vfCore = " -filter:v " + cc.Filter + fadeV;
            // 结尾截断：只要算出过"这格的画面到第几秒为止"（dur > 0；不到 5 秒的早就当不可信置 0 了），
            // 就按它截。⚠ 这里以前是 dur > 10，于是"保留 5~10 秒"那档根本不截，
            // 成品会一路放到整场结束（可能十几分钟）—— 和"截掉结尾黑屏"的本意相反。
            string trim = (dur > 0)
                ? " -t " + dur.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)
                : "";
            // ⚠ afade 必须走 -af（以前拼成 ",afade=..." 直接跟在输出参数后面，ffmpeg 解析失败 →
            //    前两次尝试都失败、退化成第三次的 -an（无声），于是并行成品全都没声音。已修）
            string aa = " -c:a aac -b:a 192k" + fadeA + trim;
            List<string> attempts = new List<string>();
            List<string> attemptWhat = new List<string>();   // 和 attempts 一一对应，只用来写日志
            for (int k = 0; k < encArgs.Count; k++)
            {
                // "-map 0:a:N?" 的"?"：这条音轨不存在也别报错，直接出无声视频（实测 rc=0）。
                // ⚠ 以前这一档写的是"不带 -map"——那样 ffmpeg 会自动挑第 0 条音轨，
                //   于是第 4 格的成品配的是【第 1 格那个玩家的声音】，比没有声音更糟。
                attempts.Add(baseArgs + " -map 0:a:" + idx + "?" + vfCore + encArgs[k] + aa);
                attemptWhat.Add(k == 0 ? EncLabel(enc) : "CPU 软编");
            }
            attempts.Add(baseArgs + vfCore + encArgs[0] + " -an" + trim);   // 最后兜底：无声
            attemptWhat.Add("无声兜底");
            int attemptNo = 0;
            foreach (string a in attempts)
            {
                attemptNo++;
                try
                {
                    ProcessStartInfo psi = new ProcessStartInfo(ff, a + " \"" + target + "\"");
                    psi.UseShellExecute = false;
                    psi.CreateNoWindow = true;
                    psi.WorkingDirectory = Path.GetDirectoryName(ff);
                    Process p = Process.Start(psi);
                    FfmpegWait w = WaitFfmpeg(p, "并行切分");
                    int code = ExitCodeAndDispose(p);
                    if (w == FfmpegWait.Cancelled) { err = "已取消"; break; }        // 用户点了停止，别再试下一种参数
                    if (w == FfmpegWait.TimedOut) { err = "ffmpeg 超时"; continue; } // 超时当作这次参数失败，让上面的循环换一种再试
                    if (code == 0 && File.Exists(target) && new FileInfo(target).Length > 4096)
                    {
                        if (attemptNo > 1)
                            Log.Write("并行切分：第 " + attemptNo + " 种参数才成功（" + attemptWhat[attemptNo - 1] + "）");
                        job.Output = target;    // 实际落点由这里说了算（见上面 OutputPathFor 那行）
                        return true;
                    }
                    err = "ffmpeg 退出码 " + code;
                    if (code != 0 && attemptNo == 1 && encArgs.Count > 1)
                        Log.Write("并行切分：" + EncLabel(enc) + "（" + enc + "）用不了，正在试后面的参数…");
                }
                catch (Exception ex) { err = ex.Message; }
            }
            return false;
        }

        // ── 一次解码、多路输出 ────────────────────────────────────────────────
        // 一组最多几格共用一个 ffmpeg 进程。
        // 实测（2026-10-13，本机 9MP 中继画布 + 45 秒样本，三种方案同一份素材）：
        //   6 格逐格切、3 并发 = 37 s（就是改之前的行为）
        //   3 格一组、两组并发 = 19 s，输出与逐格切分逐像素相同（PSNR=inf / SSIM=1.000000）
        //   6 格一组、一组       = 19 s（一样快，但一轮要开 6 路编码器，见下）
        //   6 格逐格切、6 并发   = 32 s（并发数拉到 6 没什么用 —— 瓶颈是编码，不是并发）
        // 省的时间来自"源只解码一遍"：逐格切是每格各自把整张画布解码一遍。
        // 为什么一组是 3 而不是一次 6：NVENC 的并发会话数在不少显卡上只有 3，
        // "一组 3 路 + 失败退回逐格"在任何卡上都能给出正确结果，只是慢一点。
        const int SplitGroupSize = 3;

        // ffmpeg 的秒数一律走这里：不变文化的两位小数（在"逗号当小数点"的地区，
        // 1.50 会被写成 1,50，ffmpeg 直接解析失败）。
        static string Sec(double v)
        {
            if (v < 0) v = 0;
            return v.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
        }

        // ── 编码器：三档（N 卡 / A 卡 / CPU 软编）各自的参数与解码前缀 ──────────────
        // 抽帧分析和切分都要用，所以放在这里、两边共用一份。
        // ⚠ 以前 SplitOne / SplitGroup 各写死了一份"编码器 → 参数"，加一档得改两处，
        //   漏一处就是"某个路径上静默少试一发"，很难发现。
        string EncName()
        {
            if (cfg == null || string.IsNullOrEmpty(cfg.ParallelSplitEncoder)) return "h264_nvenc";
            return cfg.ParallelSplitEncoder;
        }

        // 日志里对用户说的名字 —— 就用设置里那个下拉的选项名，别让人对不上。
        static string EncLabel(string enc)
        {
            if (enc == "libx264") return "CPU 软编";
            if (enc.EndsWith("_amf", StringComparison.OrdinalIgnoreCase)) return "AMD 显卡";
            return "NVIDIA 显卡";
        }

        // 这一档编码器要用的参数，按"从优到劣"排。
        // 除 CPU 软编外，最后都再跟一发 libx264 兜底：用户选了显卡但其实没那张卡 / 驱动坏了，
        // 也能出成品（只是慢），不会整批失败。
        // ⚠ 三家的参数各不相通：nvenc 认 -preset/-tune/-rc vbr/-cq，AMF 认 -usage/-quality/-rc cqp/-qp_*。
        //   把 nvenc 那套丢给 h264_amf 不会报错 —— ffmpeg 只打一句"xxx has not been used"就当没给，
        //   于是画质档位悄悄变成 AMF 自己的默认值（比报错更阴，实测 2026-10-14）。
        //   参数名是从包里那个 ffmpeg 的 `-h encoder=h264_amf` 查出来的，不是照文档猜的。
        static List<string> EncArgsFor(string enc, int cq)
        {
            List<string> a = new List<string>();
            if (enc == "libx264")
            {
                a.Add(" -c:v libx264 -preset medium -crf " + cq + " -pix_fmt yuv420p");
                return a;
            }
            if (enc.EndsWith("_amf", StringComparison.OrdinalIgnoreCase))
            {
                // AMF 用"固定 QP"（它没有 nvenc 的 -cq）：I 帧紧一点、P/B 松一点 ——
                // 观感几乎不变，文件更小，和 x264 系的老习惯一致。
                // ⚠ hevc_amf 只有 qp_i/qp_p、**没有 qp_b**：实测（2026-10-14）给它 -qp_b 不会报错，
                //   ffmpeg 只打一句 "Codec AVOption qp_b has not been used"，然后当没给 ——
                //   既然不起作用就别给（h264_amf / av1_amf 是真的吃这个参数，所以那两档照给）。
                string s = " -c:v " + enc + " -usage transcoding -quality quality -rc cqp -qp_i " + cq +
                           " -qp_p " + (cq + 2);
                if (!enc.Equals("hevc_amf", StringComparison.OrdinalIgnoreCase)) s += " -qp_b " + (cq + 4);
                a.Add(s + " -pix_fmt yuv420p");
            }
            else
            {
                a.Add(" -c:v " + enc + " -preset p5 -tune hq -rc vbr -cq " + cq + " -b:v 0 -pix_fmt yuv420p");
            }
            a.Add(" -c:v libx264 -preset medium -crf " + cq + " -pix_fmt yuv420p");   // 没有那张显卡时的兜底
            return a;
        }

        // "把解码交给显卡"的前缀（空串 = 不交）。
        // 为什么：软解 3840×1920@60 这种"6 个游戏同屏"的高动态流，ffmpeg 会把 16~24 个线程全占满
        //   —— 用户实测：录完 rep、刚开始切分那一刻 CPU 跑满，任务管理器里就是 ffmpeg。
        //   加了之后解码走显卡的硬件解码器，CPU 只剩裁切/缩放。
        //   本机实测（i7-14650HX，3840×1920@60 素材，N 卡）：
        //     抽帧分析 2.0 核 → 0.3 核，耗时 14.4 秒 → 10.2 秒；
        //     切分（单组） 1.7 核 → 0.2 核，耗时不变。
        // ⚠ 只交给**对应的那张卡**：N 卡走 NVDEC（cuda），A 卡走 D3D11 硬解（d3d11va，Windows 上通用）；
        //   选了 CPU 软编就别去碰显卡。
        //   两条路都有兜底 —— 加了它那次失败会自动退回不带它的写法。
        static string HwaccelPrefix(string enc)
        {
            if (enc == "libx264") return "";
            if (enc.EndsWith("_amf", StringComparison.OrdinalIgnoreCase)) return "-hwaccel d3d11va ";
            return "-hwaccel cuda ";
        }

        // 一组格子共用一个 ffmpeg。cells = 这一组在 batch 里的下标（升序）。
        // 成功才返回 true（batch[...].Output 已填好）；失败时调用方退回 SplitOne 逐格重切。
        bool SplitGroup(string grid, List<Job> batch, int[] cells, int cols, int cw, int ch,
                        double[] heads, double[] ends, out string err)
        {
            err = "";
            string ff = FindFfmpeg();
            if (ff == null) { err = "找不到 ffmpeg"; return false; }
            int n = cells.Length;
            string enc = EncName();
            List<string> encArgs = EncArgsFor(enc, cfg.ParallelSplitCQ);
            // 目标文件名先全部算好：一条命令行里要一次给全 N 个落点。
            // ⚠ 之后收尾时【不能】再算一遍（"再录一次"会算出下一个空名字，于是列表指着一个不存在的文件）。
            string[] targets = new string[n];
            for (int k = 0; k < n; k++) targets[k] = OutputPathFor(batch[cells[k]], ".mp4");

            // 滤镜图：源解一遍 → split=N → 每路各自 截头/去尾/裁边/缩放/淡出。
            // ⚠ 时间段必须写进 trim，不能用 -ss：-ss 是输入级选项，而一组里每格的秒数各不相同。
            //   实测 trim 和 -ss 出来的画面逐像素相同（PSNR=inf / SSIM=1.000000），这么换不掉画质。
            StringBuilder fc = new StringBuilder();
            fc.Append("[0:v]split=").Append(n);
            for (int k = 0; k < n; k++) fc.Append("[b").Append(k).Append("]");
            string[] aFilters = new string[n];
            for (int k = 0; k < n; k++)
            {
                int i = cells[k];
                double S = heads[i] < 0 ? 0 : heads[i];
                double E = ends[i];
                double dur = E - S;                 // 这格真正要的时长（E=0 表示"到片尾"，那时 dur<0）
                bool fade = dur > 1.5;
                CellCrop cc = CellCropOf(cw, ch, (i % cols) * cw, (i / cols) * ch);
                if (k == 0 && cc.InX > 0)
                    Log.Write("并行切分：每格往里裁 " + cc.InX + " 像素（格子坐标）≈ 成品 " + cc.InsetOut + " 像素，" +
                              "裁后 " + cc.Cw2 + "×" + cc.Ch2 + " 再缩放到 " + cc.OutW + "×" + cc.OutH);
                fc.Append(";[b").Append(k).Append("]");
                if (S > 0.2 || E > 0)
                {
                    fc.Append("trim=");
                    if (S > 0.2) fc.Append("start=").Append(Sec(S));
                    if (S > 0.2 && E > 0) fc.Append(":");
                    if (E > 0) fc.Append("end=").Append(Sec(E));
                    fc.Append(",setpts=PTS-STARTPTS,");
                }
                fc.Append(cc.Filter);
                // 结尾淡出：并行切分本来就要重编码，加这个不额外花钱，避免"最后一帧硬切"的观感
                if (fade) fc.Append(",fade=t=out:st=").Append(Sec(dur - 0.6)).Append(":d=0.6");
                fc.Append("[v").Append(k).Append("]");
                // 这一格的音轨就是第 i 条（并行录制时一格一条，见 RecTracks 那段）。
                // 音轨同样要自己截头去尾：视频用 trim 截了，音频不截就会音画不同步。
                string af = "";
                if (S > 0.2 || E > 0)
                {
                    af = "atrim=";
                    if (S > 0.2) af += "start=" + Sec(S);
                    if (S > 0.2 && E > 0) af += ":";
                    if (E > 0) af += "end=" + Sec(E);
                    af += ",asetpts=PTS-STARTPTS";
                }
                if (fade) af += (af.Length > 0 ? "," : "") + "afade=t=out:st=" + Sec(dur - 0.6) + ":d=0.6";
                aFilters[k] = af;
            }
            string head = "-hide_banner -y -i \"" + grid + "\" -filter_complex \"" + fc + "\"";
            List<string> cmds = new List<string>();
            List<string> cmdWhat = new List<string>();   // 和 cmds 一一对应，只用来写日志（"实际用的是哪一档"）
            // 第一发就带显卡解码（CPU 占用能降一个量级，见 HwaccelPrefix 的注释）；
            // 它失败会自动落到下一发（不带 hwaccel 的同参数），所以不会因此丢成品。
            string hw = HwaccelPrefix(enc);
            if (hw.Length > 0)
            {
                cmds.Add(GroupArgs(hw + head, cells, encArgs[0], true, aFilters, targets));
                cmdWhat.Add(EncLabel(enc) + "（显卡解码）");
            }
            for (int k = 0; k < encArgs.Count; k++)
            {
                cmds.Add(GroupArgs(head, cells, encArgs[k], true, aFilters, targets));
                cmdWhat.Add(k == 0 ? EncLabel(enc) : "CPU 软编");
            }
            cmds.Add(GroupArgs(head, cells, encArgs[0], false, aFilters, targets));   // 最后兜底：整组无声
            cmdWhat.Add("无声兜底");
            Log.Write("并行切分：一组 " + n + " 格（第 " + (cells[0] + 1) + "~" + (cells[n - 1] + 1) +
                      " 格，配第 " + (cells[0] + 1) + "~" + (cells[n - 1] + 1) + " 条音轨），用 " +
                      EncLabel(enc) + "（" + enc + "）");

            int attemptNo = 0;
            foreach (string a in cmds)
            {
                attemptNo++;
                try
                {
                    ProcessStartInfo psi = new ProcessStartInfo(ff, a);
                    psi.UseShellExecute = false;
                    psi.CreateNoWindow = true;
                    psi.WorkingDirectory = Path.GetDirectoryName(ff);
                    Process p = Process.Start(psi);
                    FfmpegWait w = WaitFfmpeg(p, "并行切分（一组 " + n + " 格）");
                    int code = ExitCodeAndDispose(p);
                    if (w == FfmpegWait.Cancelled) { err = "已取消"; return false; }
                    if (w == FfmpegWait.TimedOut) { err = "ffmpeg 超时"; continue; }
                    if (code == 0 && AllTargetsOk(targets))
                    {
                        if (attemptNo > 1)
                            Log.Write("并行切分：一组 " + n + " 格，退到第 " + attemptNo + "/" + cmds.Count +
                                      " 种参数才成功（" + cmdWhat[attemptNo - 1] + "）");
                        for (int k = 0; k < n; k++) batch[cells[k]].Output = targets[k];
                        return true;
                    }
                    err = "ffmpeg 退出码 " + code;
                    if (code != 0 && attemptNo == 1 && encArgs.Count > 1)
                        Log.Write("并行切分：" + EncLabel(enc) + "（" + enc + "）用不了，正在试后面的参数…");
                }
                catch (Exception ex) { err = ex.Message; }
            }
            // 一组里有格子没切出来：把这轮【新建】的半成品删掉，别留下看着像成品的残缺文件。
            // 只敢删"再录一次（旧的保留）"那种 —— 它的文件名是刚挑的空名字，删了动不到用户原来的录像；
            // 普通的 xxx.mp4 可能是用户上一次的成品，绝不能删。
            // 顺手把最后一次用的 ffmpeg 命令打进日志：这一档很少走到，真走到了就是拿来排查的。
            Log.Write("并行切分：这组没切出来（" + err + "）。最后一次用的命令是：" + cmds[cmds.Count - 1]);
            for (int k = 0; k < n; k++)
            {
                if (!batch[cells[k]].KeepOld) continue;
                try { if (File.Exists(targets[k])) File.Delete(targets[k]); } catch { }
            }
            return false;
        }

        // 拼一组多路输出的命令行。withAudio=false 时整组不带声音（最后兜底）。
        // 音轨用 0:a:N? 的"?"：那条轨不存在也不报错，直接出无声视频（实测 rc=0）——
        // 所以以前 SplitOne 里"音轨不够就退化成不带音频"那一档可以省掉。
        static string GroupArgs(string head, int[] cells, string encArg, bool withAudio,
                                string[] aFilters, string[] targets)
        {
            StringBuilder sb = new StringBuilder(head);
            for (int k = 0; k < cells.Length; k++)
            {
                sb.Append(" -map \"[v").Append(k).Append("]\"");
                if (withAudio) sb.Append(" -map 0:a:").Append(cells[k]).Append("?");
                sb.Append(encArg);
                sb.Append(withAudio ? " -c:a aac -b:a 192k" : " -an");
                if (withAudio && aFilters[k].Length > 0) sb.Append(" -af \"").Append(aFilters[k]).Append("\"");
                sb.Append(" \"").Append(targets[k]).Append("\"");
            }
            return sb.ToString();
        }

        static bool AllTargetsOk(string[] targets)
        {
            foreach (string t in targets)
            {
                try { if (!File.Exists(t) || new FileInfo(t).Length <= 4096) return false; }
                catch { return false; }
            }
            return true;
        }

        // 一格切完后的收尾（UI 状态、体积、时长、日志）。逐格路径和分组路径共用一份。
        void MarkSplitDone(Job job, int idx, bool black)
        {
            if (string.IsNullOrEmpty(job.Output)) job.Output = OutputPathFor(job, ".mp4");
            long outBytes = 0;
            try { outBytes = new FileInfo(job.Output).Length; } catch { }
            job.Size = outBytes;
            job.VideoSeconds = Mp4Duration(job.Output);
            job.Ended = DateTime.Now;
            // 整场全黑的那几格标「注意」：文件是有的、也能播，但很可能没录上。
            // 标「完成」等于骗人 —— 用户得自己点开看才知道是黑的。
            Ui.SetJob(job, black ? "注意" : "完成",
                      (outBytes / 1048576.0).ToString("0.0") + " MB（并行切分）"
                      + (black ? "  ⚠ 这一格整场全黑，建议单独重录" : ""));
            Log.Write("并行切分完成（第 " + (idx + 1) + " 格）：" + Path.GetFileName(job.Output) +
                      "  " + (outBytes / 1048576.0).ToString("0.0") + " MB  视频时长 " + FmtDur(job.VideoSeconds));
        }
        bool SourceLooksStatic(string sourceName)
        {
            try
            {
                byte[] a = ShotOf(sourceName);
                Thread.Sleep(800);
                byte[] b = ShotOf(sourceName);
                if (a == null || b == null) return false;
                if (a.Length != b.Length) return false;
                int limit = a.Length / 100 + 16;
                int diff = 0;
                for (int i = 0; i < a.Length; i++)
                {
                    if (a[i] != b[i]) { diff++; if (diff > limit) return false; }
                }
                return true;
            }
            catch { return false; }
        }
        void CaptureOriginalRecordDir()
        {
            if (originalRecordDir != null) return;
            try { originalRecordDir = Convert.ToString(obs.Request("GetRecordDirectory", null)["recordDirectory"]); }
            catch { }
        }
        void RunParallel()
        {
            int want = cfg.ParallelCount;
            if (want < 1) want = 1;
            if (want > 6) want = 6;   // 上限 6：OBS 只有 6 条音轨，再多每格就没声音了

            // 「录制选中的」：先按选中的列表跑，每批 want 个
            List<Job> sel = selectedBatch;
            selectedBatch = null;
            if (sel != null)
            {
                if (sel.Count == 0) return;
                Log.Write("==== 选中批次开始：共 " + sel.Count + " 个，每批 " + want + " 个 ====");
                forceRecord = true;
                try
                {
                    for (int off = 0; off < sel.Count; off += want)
                    {
                        if (cancel) { Log.Write("已停止：剩下的选中项不再录"); break; }
                        int take = Math.Min(want, sel.Count - off);
                        Log.Write("---- 第 " + (off / want + 1) + " 批（" + take + " 个）----");
                        obsStuckRetried = false;
                        RunParallelBatch(sel.GetRange(off, take));
                    }
                }
                finally { forceRecord = false; }
                Log.Write("==== 选中批次全部结束 ====");
                return;
            }

            List<Job> batch = new List<Job>();
            lock (jobsLock)
            {
                foreach (Job j in jobs)
                {
                    if (j.Status == "等待") { batch.Add(j); if (batch.Count >= want) break; }
                }
            }
            if (batch.Count == 0)
            {
                Log.Write("没有待录制的 rep（都录过了？点「刷新」看看）");
                return;
            }
            obsStuckRetried = false;
            RunParallelBatch(batch);
        }
        // 窗口尺寸换算用的几个 API —— 见 RunParallelBatch 里量窗口尺寸那段。
        //
        // ⚠ 这段注释 2026-10-13 改写过。当初（10-05）的结论是"非想天则是 DPI 不感知的老程序，
        //   Windows 会把它 640×480 的画面位图拉伸到物理尺寸，所以要按逻辑尺寸采"——
        //   那个结论来自一个**"假装是游戏"的探针窗口**（它自己确实是不感知的，
        //   GetDpiForWindow 报 96）。但真实的 th123 不是：整合包的 mod 加载器把它设成了
        //   PER_MONITOR_AWARE（实测 GetProcessDpiAwareness=2、GetDpiForWindow=144），
        //   它渲染出来就是 640×480 个真像素，**没有**被拉伸。
        //   所以那句"要换算成逻辑尺寸"从根上就不成立，按它做的除法一直在丢像素。
        //   现在：本进程感知 → 系数 1.0；只有真不感知时才用显示器 DPI 乘回去。
        [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint flags);
        [DllImport("shcore.dll")] static extern int GetDpiForMonitor(IntPtr mon, int type, out uint x, out uint y);
        [DllImport("shcore.dll")] static extern int GetProcessDpiAwareness(IntPtr proc, out int value);
        [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();

        // 本进程是不是 DPI 感知的（0=不感知 1=系统级 2=每显示器）。
        // 只影响"量到的窗口尺寸要不要换算"，所以只问一次、缓存住。
        static bool? selfDpiAware;
        static bool SelfIsDpiAware()
        {
            if (selfDpiAware.HasValue) return selfDpiAware.Value;
            bool aware = true;      // 兜底：app.manifest 里就是 PerMonitorV2（Ui.cs 里还再设一次）
            try
            {
                int v;
                if (GetProcessDpiAwareness(GetCurrentProcess(), out v) == 0) aware = v != 0;
            }
            catch { }               // 老系统没有 shcore.dll
            selfDpiAware = aware;
            return aware;
        }

        // 把「量到的客户区」换算成**物理像素**的系数（物理像素 = OBS 画布的单位）。
        // ⚠ 2026-10-13 修：这里以前返回 dx/96，而调用处拿它去【除】—— 方向反了，
        //   等于凭空丢掉 1/3 的边长。实测：th123 的客户区物理是 640×480，程序却按 427×320 去采。
        //   根因是当初那句假设（"非想天则是 DPI 不感知的老程序，Windows 会把它的 640×480
        //   位图拉伸到物理尺寸"）在这个整合包里**不成立**：mod 加载器把 th123 设成了
        //   PER_MONITOR_AWARE（实测 GetProcessDpiAwareness=2），它渲染出来就是 640×480 个
        //   真像素、没有被拉伸。而本进程也是 DPI 感知的，GetClientRect 给到的**本来就是物理
        //   像素** —— 和 OBS 画布同一个单位，系数就是 1.0。
        //   真要不感知的老进程才会量到"虚拟尺寸"（640 的窗口量成 426），那时才需要【乘】dx/96。
        static double WindowPixelScale(IntPtr hwnd)
        {
            if (SelfIsDpiAware()) return 1.0;      // 感知 → 量到的就是物理像素，不用换算
            try
            {
                IntPtr mon = MonitorFromWindow(hwnd, 2);   // MONITOR_DEFAULTTONEAREST
                uint dx, dy;
                if (mon != IntPtr.Zero && GetDpiForMonitor(mon, 0, out dx, out dy) == 0
                    && dx >= 48 && dx <= 480)
                    return dx / 96.0;              // 不感知 → 虚拟尺寸要乘回去
            }
            catch { }   // 老系统没有 shcore.dll 就是这个 catch
            return 1.0;
        }

        // 某个"cols 列 × rows 行"的排法下，每格最多能占 OBS 画布上限的几成（1.0 = 不用缩）。
        // 见 RunParallelBatch 里挑排法的那段。
        static double GridScaleToFit(int cols, int rows, int cw, int ch, int maxCanvas)
        {
            double s = 1.0;
            if ((long)cols * cw > maxCanvas) s = Math.Min(s, (double)maxCanvas / ((double)cols * cw));
            if ((long)rows * ch > maxCanvas) s = Math.Min(s, (double)maxCanvas / ((double)rows * ch));
            return s;
        }

        void RunParallelBatch(List<Job> batch)
        {
            // 哪几格自愈完还是黑的 —— 切分完要把它们标成「注意」而不是「完成」。
            // 黑格子照样能切出一个 mp4（内容是整段全黑），以前无条件写「完成」，用户会以为录好了
            // （2026-10-08 查实，2026-10-09 用户拍板改成「注意」）。
            // ⚠ 必须声明在方法最外层：用它的那处在切分线程里，比自检那段深 4 层花括号，
            //   声明在里层的话编译器直接报「当前上下文中不存在名称 blackCells」。
            bool[] blackCells = new bool[8];
            if (batch.Count == 0) return;
            // 在上一批的收尾里按了「停止」→ 这一批根本别开。
            // （批与批之间本来就有检查，这里是再兜一道，保证"停止"之后绝不会再冒出下一批）
            if (cancel) { Log.Write("已停止：这一批不再开始"); return; }
            // 降级成单录时，也要把采集源准备好并切到录制场景（单录流程自己不做这些）
            if (batch.Count == 1)
            {
                Log.Write("这一批只有 1 个，改成普通单录");
                if (cfg.AutoPrepareObs) PrepareObs(false);
                if (cfg.AutoPrepareObs && cfg.UseDedicatedScene) SwitchScene(true);
                CaptureOriginalRecordDir();
                // ⚠ 单录这条路以前漏了 ApplyAudioRouting：默认「只录游戏声音」时 OBS 桌面音频没被静音，
                //   系统里的其他声音会混进录像。这里补上，录完在 finally 里恢复（原本走 RunQueue 的
                //   那段恢复代码因为 parallelRequested 恒为 true 早就不执行了）。
                try { ApplyAudioRouting(true); } catch { }
                try { ProcessJob(batch[0]); }
                finally { try { ApplyAudioRouting(false); } catch { } }
                return;
            }
            if (!cfg.AutoPrepareObs) { Log.Write("并行录制需要打开「自动准备采集源并绑定游戏窗口」，已中止"); return; }

            // 队列里的「用时」要靠 Started/Ended 相减：并行这条路径以前从没设过 Started，
            // 结果列表里显示成 (现在 - 0001年) 那种天文数字（63926114748s）
            DateTime batchStart = DateTime.Now;
            for (int i = 0; i < batch.Count; i++) batch[i].Started = batchStart;
            Ui.ScrollToBatch(batch);   // 新的一批开始了，把列表挪过去看一眼（之后整批都不再动它）

            // OBS 最多 6 条音轨：超过 6 格时每格的声音没法分开，整批录成无声，避免出现"画面是这局、声音是那局"
            bool cellAudio = cfg.RecordGameAudio && batch.Count <= 6;
            if (batch.Count > 6 && cfg.RecordGameAudio)
                Log.Write("⚠ 同时录 " + batch.Count + " 个超过 6 条音轨上限，这一批会是无声视频");

            Log.Write("==== 并行录制开始：同时录 " + batch.Count + " 个 ====");
            List<Process> games2 = new List<Process>();   // 带 rep 重启后的那些实例
            List<string> vNames = new List<string>();
            List<string> aNames = new List<string>();
            List<int> cellItemIds = new List<int>();      // 每格画面元素在场景里的 itemId（绑完窗口再定位置）
            Dictionary<string, string> appliedWin = new Dictionary<string, string>();   // 已经设过的窗口串（一样就跳过重设，避免闪黑帧）
            int stdItemId = -1;                           // 单文件用的采集源元素（并行期间要藏起来）
            bool stdWasEnabled = true;
            DateTime recordStart = DateTime.MinValue;     // 开录时刻（用来算每格实际录了多久）
            double[] cellSec = null;
            // 只留"最后一次看到画面"这一个：采样是为了剪掉**结尾**的黑。
            // （曾经还有个 cellHead 记"第一次看到画面"，但头部的黑是由 AnalyzeCellHead
            //   逐帧算出来的、跟这些采样无关 —— 它从头到尾只被写、没被读过，2026-10-12 删掉。）
            double[] cellLastSeen = null;
            string gridFile = null;
            bool parMutedByUs = false;                    // 这一批是不是我们静音了系统输出（收尾要恢复）
            int CW = 0, CH = 0;
            int cols = 1, rows = 1;   // 画布网格（格子多了要折行，见下面）
            string origTracks = null;

            try
            {
                PrepareObs(false);
                // ★ 每一批开始前，把上一批可能残留的 P1~P6 采集源彻底删掉、音轨全部归零。
                //   轮录会连着跑很多批；上一批的源只要没删干净，它就会带着旧的「窗口绑定 + 音轨」
                //   活到这一批里 —— 表现就是"声音/画面跟别的 rep 串了"（用户实测）。
                for (int ci = 1; ci <= 6; ci++)
                {
                    string[] nms = new string[] { "RepRecorder P" + ci + " 画面", "RepRecorder P" + ci + " 声音" };
                    foreach (string nm in nms)
                    {
                        try { SetAudioTracksRaw(nm, 0); } catch { }   // 先摘掉音轨，残留源就算还在也没声音
                        try
                        {
                            Dictionary<string, object> rr = new Dictionary<string, object>();
                            rr["inputName"] = nm;
                            obs.Request("RemoveInput", rr);
                        }
                        catch { }
                    }
                }
                Log.Write("并行：已清理上一批可能残留的 P1~P6 采集源并把音轨归零");
                try
                {
                    Dictionary<string, object> q = new Dictionary<string, object>();
                    q["parameterCategory"] = "SimpleOutput";
                    q["parameterName"] = "RecTracks";
                    Dictionary<string, object> r = obs.Request("GetProfileParameter", q);
                    origTracks = r.ContainsKey("parameterValue") ? Convert.ToString(r["parameterValue"]) : "1";
                }
                catch { }
                try
                {
                    Dictionary<string, object> rd = new Dictionary<string, object>();
                    rd["recordDirectory"] = Path.GetDirectoryName(batch[0].Rep);
                    obs.Request("SetRecordDirectory", rd);
                }
                catch { }
                ApplyAudioRouting(true);   // 静音 OBS 桌面音频，避免把系统声音混进来
                CaptureOriginalRecordDir();

                // ★ 把「单文件录制」用的那套采集源在并行期间藏起来 + 静音。
                //   不这么做会出两个问题（实测）：
                //   1) 它的声音默认落在第 1 条音轨上 —— 会混进第 1 格的成品里，听起来像"两局的声音一起放"；
                //   2) 它的画面元素铺满整块画布（在格子底下）—— 某局先结束后，格子里没画面了，
                //      它就从空出来的地方透出来，看起来像"把另一局的画面接到了这个视频后面"。
                try
                {
                    stdItemId = CleanupDuplicateItems(RecSceneName, RecSourceName);
                    if (stdItemId >= 0)
                    {
                        try
                        {
                            Dictionary<string, object> q0 = new Dictionary<string, object>();
                            q0["sceneName"] = RecSceneName;
                            q0["sceneItemId"] = stdItemId;
                            stdWasEnabled = Convert.ToBoolean(obs.Request("GetSceneItemEnabled", q0)["sceneItemEnabled"]);
                        }
                        catch { }
                        SetSceneItemEnabled(RecSceneName, stdItemId, false);
                    }
                    SetInputMuteRaw(AudioSourceName, true);
                    Log.Write("并行：已把单文件用的采集源「" + RecSourceName + "」隐藏、并把「" + AudioSourceName + "」静音"
                              + "（否则画面/声音会漏进格子）");
                }
                catch (Exception ex) { Log.Write("并行：隐藏单文件采集源失败（可忽略）：" + ex.Message); }
                cellSec = new double[batch.Count];
                cellLastSeen = new double[batch.Count]; // 这一格"最后一次看到画面"的时刻（剪掉结尾的黑屏）

                // 准备阶段中途按了「停止」→ 别再往下开了（否则游戏照开、录像照起）
                if (cancel)
                {
                    Log.Write("已停止：这一批在准备阶段被中止，不会启动游戏、不会开录");
                    return;
                }

                // ===== 只开一次游戏 =====
                // 以前是"先空参数开 N 个实例停在标题画面 → 摆好源 → 全部杀掉 → 开录 → 再带 rep 重启"，
                // 等于把游戏开两遍（用户实测：这两个动作重复了两次，纯属浪费时间）。
                // 现在改成：带 rep 启动 → 等窗口出现（此时游戏还在读盘/黑屏）→ 量出窗口尺寸、
                // 摆好画布 → 开录 → 建源绑定。开播前那几秒黑屏由切分时按"采集到画面的时刻"剪掉。
                List<IntPtr> handles2 = new List<IntPtr>();
                List<string> wants = new List<string>();
                for (int i = 0; i < batch.Count; i++)
                {
                    Ui.SetJob(batch[i], "启动游戏", "并行 " + (i + 1) + "/" + batch.Count);
                    Process np = null;
                    try
                    {
                        ProcessStartInfo psi = new ProcessStartInfo(Path.Combine(cfg.GameDir, "th123.exe"),
                                                                    "\"" + batch[i].Rep + "\"");
                        psi.WorkingDirectory = cfg.GameDir;
                        np = Process.Start(psi);
                        Log.Write("并行：第 " + (i + 1) + " 个实例已启动 → " + Path.GetFileName(batch[i].Rep));
                    }
                    catch (Exception ex) { Log.Write("并行：第 " + (i + 1) + " 个实例启动失败 " + ex.Message); }
                    games2.Add(np);
                    wants.Add("th123 #" + (i + 1));
                    Thread.Sleep(400);
                }
                for (int i = 0; i < games2.Count; i++)
                {
                    if (games2[i] == null) { handles2.Add(IntPtr.Zero); continue; }
                    IntPtr h2 = WaitForMainWindow(games2[i], 90);
                    handles2.Add(h2);
                    if (h2 == IntPtr.Zero)
                    {
                        Log.Write("并行：第 " + (i + 1) + " 个实例的窗口没出现（这一格会是空的）");
                        // ⚠ 这一格【也会是黑的】，而且黑屏自检根本抓不到它 —— 自检看的是
                        //   `games2[i] 活着 && 采集源画面全黑`，而窗口没出现时采集源压根没绑上、
                        //   截图直接失败，IsBlank 拿不到东西 → 返回 false → 一格都不报。
                        //   2026-10-09 用假 th123.exe 实测出来的：日志明明写着"这一格会是空的"，
                        //   最后那两格却还是被标成「完成」。用户最早报的"游戏没开、那个 rep 不见了"
                        //   就是这种情况。所以这里必须**直接**把这一格记成坏格。
                        blackCells[i] = true;
                    }
                }
                // 窗口都出现了 → 开录前按设置把它们设成"640×480 × 游戏画面大小"那一档。
                // ⚠ 必须在量尺寸之前做：下面那段就是按"设完之后的真实客户区"排画布的。
                ApplyGameScale(handles2);
                // 窗口尺寸（每格按游戏客户区大小排，保持 4:3 不变形）
                for (int i = 0; i < handles2.Count; i++)
                {
                    if (handles2[i] == IntPtr.Zero) continue;
                    RECT cr;
                    if (GetClientRect(handles2[i], out cr) && cr.Right > 0 && cr.Bottom > 0 && CW == 0)
                    {
                        // 量到的就是**物理像素**（本进程 DPI 感知），OBS 画布用的也是物理像素，
                        // 所以这里【乘 1.0】，只对"不感知"的情况才做换算。
                        // ⚠ 2026-10-13 之前这里是 `cr.Right / WindowDpiScale(...)`，方向反了：
                        //   本机把 640×480 的窗口按 427×320 采，白白丢掉 1/3 边长 ——
                        //   而 427 这个数还刚好等于"不感知进程看到的虚拟尺寸"，所以一直没人看出来。
                        //   用户反馈"后面几格画质太烂"，查到的第一层原因就是这个。
                        double sc = WindowPixelScale(handles2[i]);
                        CW = (int)Math.Round(cr.Right * sc);
                        CH = (int)Math.Round(cr.Bottom * sc);
                        // 明显不合理的测量值才丢掉（4096 = OBS 画布上限）。⚠ 这个门槛原来写的是
                        // 1920/1440，那是按"除以 1.5 之后"的数值定的；换成物理像素之后，
                        // 全屏 2560×1600 是**正常**值，用老门槛会把它误判成异常、缩回 640×480。
                        // 真的偏大不用怕：下面那道"压到输出尺寸"的闸门会等比缩好。
                        if (CW > 4096 || CH > 4096) { CW = 640; CH = 480; }   // 异常大就按游戏原生尺寸
                        // 每格不需要比"切分后的输出尺寸"更大 —— 切分那一步反正会缩到
                        // CanvasWidth×CanvasHeight。这同时也是一道保险：万一上面 DPI 那一步
                        // 没测准（或者哪天换个游戏窗口特别大），画布也不会失控到把编码器压垮。
                        int capW = cfg.CanvasWidth > 0 ? cfg.CanvasWidth : 1440;
                        int capH = cfg.CanvasHeight > 0 ? cfg.CanvasHeight : 1080;
                        if (CW > capW || CH > capH)
                        {
                            double k = Math.Min((double)capW / CW, (double)capH / CH);
                            int nw = Math.Max(2, (int)Math.Floor(CW * k));
                            int nh = Math.Max(2, (int)Math.Floor(CH * k));
                            Log.Write("并行：游戏窗口 " + CW + "×" + CH + " 比输出尺寸 " + capW + "×" + capH
                                      + " 还大，每格按 " + nw + "×" + nh + " 采（切分后反正要缩到这个尺寸）");
                            CW = nw; CH = nh;
                        }
                    }
                }
                if (CW <= 0 || CH <= 0) { CW = 640; CH = 480; }

                // 网格排布：让中继画布尽量方正（6 格 = 3×2 = 1920×960，而不是 3840×480 那种 8:1 的长条，
                // 后者在 OBS 预览里看着就像"画面没摆好"）。
                // ⚠ OBS 画布上限 4096 —— 光靠"换列数"是躲不掉的。2026-10-05 用户的
                //   并行+轮录一次全废（20 个 rep，前 18 个每批都报
                //   「SetVideoSettings 失败：402 baseWidth 超过 4096」）根因就在这段：
                //   6 格 × 1920×1440 无论怎么排都超（3×2 = 5760×2880、2×3 = 3840×4320），
                //   而旧代码只会写一句警告然后照样往下走 → 整批报废。
                //   （旧版 WinForms 是 DPI 不感知的，读到的是 640×480 逻辑像素，
                //    6 格 = 3×2 = 1920×960 正好放得下，所以"老版本是好的"。详见 MakeDpiAware。）
                //   现在：装不下就把**每格等比缩小**（保持长宽比，不然游戏会被拉变形），
                //   并且在所有列数里挑"每格能留得最大"的那种排法。
                //   上面改成逻辑尺寸之后，这里基本不会再触发（6 格 640×480 = 1920×960），
                //   留着当安全网：万一哪天游戏窗口真的很大，宁可缩格子也不能让整批报废。
                {
                    int maxCanvas = 4096;
                    cols = (int)Math.Ceiling(Math.Sqrt(batch.Count * (double)CH / CW));
                    if (cols < 1) cols = 1;
                    rows = (batch.Count + cols - 1) / cols;
                    double sc = GridScaleToFit(cols, rows, CW, CH, maxCanvas);
                    if (sc < 1.0)
                    {
                        double best = -1; int bc = cols, br = rows;
                        for (int c = 1; c <= batch.Count; c++)
                        {
                            int r = (batch.Count + c - 1) / c;
                            double t = GridScaleToFit(c, r, CW, CH, maxCanvas);
                            bool better = t > best + 1e-9
                                       || (Math.Abs(t - best) <= 1e-9 && Math.Abs(c - r) < Math.Abs(bc - br));
                            if (better) { best = t; bc = c; br = r; }
                        }
                        cols = bc; rows = br; sc = best;
                    }
                    if (sc < 1.0)
                    {
                        int nw = Math.Max(2, (int)Math.Floor(CW * sc));
                        int nh = Math.Max(2, (int)Math.Floor(CH * sc));
                        Log.Write("并行：中继画布 " + (cols * CW) + "×" + (rows * CH) + " 放不进 OBS 上限 "
                                  + maxCanvas + "，每格缩到 " + nw + "×" + nh);
                        CW = nw; CH = nh;
                    }
                    if (rows * CH > maxCanvas || cols * CW > maxCanvas)
                        Log.Write("⚠ 并行：画布 " + (cols * CW) + "×" + (rows * CH) + " 仍超过 OBS 上限 " + maxCanvas +
                                  "，可能会失败（把「同时录制数量」调小一点）");
                    Dictionary<string, object> nv = new Dictionary<string, object>();
                    nv["baseWidth"] = cols * CW;
                    nv["baseHeight"] = rows * CH;
                    nv["outputWidth"] = cols * CW;
                    nv["outputHeight"] = rows * CH;
                    nv["fpsNumerator"] = 60;
                    nv["fpsDenominator"] = 1;
                    obs.Request("SetVideoSettings", nv);
                    Log.Write("并行：每格画面 " + CW + "×" + CH + "，" + cols + " 列 × " + rows + " 行，中继画布 " +
                              (cols * CW) + "×" + (rows * CH));
                }

                // 标题必须在我们开录前就改成唯一值：OBS 的采集源按标题匹配窗口，
                // 而且游戏自己在启动约 2 秒后会把标题改回中文版 —— 改晚了采集源就抓不到窗口（整段黑）。
                bool titleOk = ForceAllWindowTitles(handles2, wants, 20);
                Log.Write("并行：窗口标题改成唯一值 " + (titleOk ? "成功" : "失败（可能有多格录到同一个游戏）"));
                for (int i = 0; i < handles2.Count; i++)
                    if (handles2[i] != IntPtr.Zero) MoveWindowClientTo(handles2[i], (i % 5) * 80, (i / 5) * 60);

                // 先把每格的采集源建出来、音轨分好（都不依赖窗口，放在开录前更稳：录制中再新增源会让编码抖一下）。
                // 绑定窗口放到开录之后（那时才知道游戏窗口，而且黑屏由切分时剪掉）。
                for (int i = 0; i < batch.Count; i++)
                {
                    string vName = "RepRecorder P" + (i + 1) + " 画面";
                    string aName = "RepRecorder P" + (i + 1) + " 声音";
                    try
                    {
                        EnsureInput(vName, "window_capture", VideoInputSettings(""));
                        EnsureInput(aName, "wasapi_process_output_capture", AudioInputSettings(""));
                        vNames.Add(vName); aNames.Add(aName);
                        int itemId = CleanupDuplicateItems(RecSceneName, vName);
                        cellItemIds.Add(itemId);
                        SetTransformRaw(RecSceneName, itemId, (i % cols) * CW, (i / cols) * CH, CW, CH);
                        CleanupDuplicateItems(RecSceneName, aName);
                        SetAudioTracksRaw(aName, batch.Count <= 6 ? i + 1 : 0);
                        SetInputMuteRaw(aName, !cellAudio);
                    }
                    catch (Exception ex) { Log.Write("并行：第 " + (i + 1) + " 格建源失败 " + ex.Message); }
                }
                SetProfileParamRaw("SimpleOutput", "RecTracks", ((1 << Math.Min(batch.Count, 6)) - 1).ToString());
                try
                {
                    foreach (string n in Program.FindMicTargets(obs))
                    {
                        // ⚠ 静音之前【必须】先把原来的状态记进 micOriginalMute。
                        //   这里图省事直接 SetInputMute，等于绕过了 ApplyMicMode —— 而
                        //   micOriginalMute 只在 ApplyMicMode(true) 里填。并行这条路从来不调它，
                        //   于是收尾时 ApplyMicMode(false) 遍历的是一张空表，**一个都没恢复**：
                        //   并行录完，用户 OBS 里的麦克风就一直是哑的（2026-10-08 查实；
                        //   OBS 会把静音状态存进场景集合，重启 OBS 也还是哑的）。
                        try
                        {
                            Dictionary<string, object> q = new Dictionary<string, object>();
                            q["inputName"] = n;
                            bool was = Convert.ToBoolean(obs.Request("GetInputMute", q)["inputMuted"]);
                            if (!micOriginalMute.ContainsKey(n)) micOriginalMute[n] = was;
                        }
                        catch { }
                        SetInputMuteRaw(n, true);
                    }
                }
                catch { }

                // 开录前先确认 OBS 没有"卡住的录像"：并行这条路以前完全不做这一步，
                // 一旦 OBS 处于录制/半停止状态，StartRecord 返回成功但实际什么都没录，
                // 结果等 3 分钟、StopRecord 报 501、拿不到文件 —— 用户实测就是这样白录一批。
                StopObsRecordingIfAny();

                recordStart = DateTime.Now;
                obs.Request("StartRecord", null);
                // 再确认一次"真的在录"（这段逻辑 2026-10-12 抽成了 ConfirmRecordingStarted，
                // 单录那条路以前没有这一步，偶发白录一整局）
                bool reallyRecording = ConfirmRecordingStarted("并行");
                if (!reallyRecording)
                {
                    Log.Write("并行：❌ OBS 没真正开录（outputActive=false）。可能：有残留录像 / OBS 卡住 / 录制目录不可写");
                    // 自动救一次：杀掉卡住的 OBS → 重新拉起 → 重建场景/采集源 → 把这一批完整重跑一遍。
                    // （用户实测就是这样白录一批的；能自动救回来最好，救不回来也明确报错，不再白等。）
                    if (!obsStuckRetried)
                    {
                        obsStuckRetried = true;
                        Log.Write("并行：正在自动重启 OBS 并把这批重跑一次（第 1 次也是唯一一次自动重试）…");
                        try
                        {
                            foreach (Process g in games2) { try { if (g != null && !g.HasExited) g.Kill(); } catch { } }
                            KillGame();
                            try { obs.Dispose(); } catch { }
                            foreach (Process o in Process.GetProcessesByName("obs64")) { try { o.Kill(); } catch { } }
                            Thread.Sleep(4000);
                            if (EnsureObs())
                            {
                                if (cfg.AutoPrepareObs) PrepareObs(false);
                                if (cfg.AutoPrepareObs && cfg.UseDedicatedScene) SwitchScene(true);
                                Log.Write("并行：OBS 已重启并重新准备好，开始重跑这一批…");
                                RunParallelBatch(batch);
                                return;
                            }
                            Log.Write("并行：重启 OBS 失败");
                        }
                        catch (Exception ex) { Log.Write("并行：自动重启 OBS 出错：" + ex.Message); }
                    }
                    foreach (Job j in batch) Ui.SetJob(j, "失败", "OBS 没有真正开始录像（已中止，避免白等）");
                    return;
                }
                Log.Write("并行录制：已开始录像（" + batch.Count + " 格，已确认 OBS 真的在录）");

                // 把每格绑到各自的游戏窗口，并按源的真实尺寸摆进格子
                List<string> boundStrings = new List<string>();
                for (int i = 0; i < batch.Count && i < vNames.Count; i++)
                {
                    try
                    {
                        string ws = null;
                        if (handles2[i] != IntPtr.Zero)
                        {
                            StringBuilder tb = new StringBuilder(512);
                            GetWindowText(handles2[i], tb, 512);
                            string tt = tb.ToString();
                            // ⚠ 标题读空/被游戏改回去时，宁可用我们自己强制设的 "th123 #N"。
                            // 空标题拼出来的窗口串是 ":th123_110a:th123.exe" → OBS 匹配不到窗口 → 那一格全黑
                            //（用户实测：6 格里左边第一个全黑，日志就是 "→ :th123_110a:th123.exe"）。
                            if (tt.Length == 0 || tt.IndexOf("th123 #", StringComparison.Ordinal) < 0)
                            {
                                tt = "th123 #" + (i + 1);
                                Log.Write("并行：第 " + (i + 1) + " 格窗口标题读不到/不对，改用强制标题「" + tt + "」绑定");
                            }
                            ws = MakeWindowString(tt, "th123_110a");
                        }
                        if (ws != null)
                        {
                            boundStrings.Add(ws);
                            ApplyCellWindow(appliedWin, vNames[i], aNames[i], ws);
                        }
                        SetTransformFitCell(RecSceneName, (i < cellItemIds.Count ? cellItemIds[i] : -1),
                                            (i % cols) * CW, (i / cols) * CH, CW, CH, i + 1);
                        if (batch.Count <= 6) SetAudioTracksRaw(aNames[i], i + 1);
                        SetInputMuteRaw(aNames[i], !cellAudio);
                        SetAudioLeadRaw(aNames[i], AudioLeadValue());   // 「音频提前」逐格设一遍（源是每批新建的）
                        Log.Write("并行：第 " + (i + 1) + " 格绑定窗口串 = " + (ws == null ? "<窗口没出现>" : ws));
                    }
                    catch (Exception ex) { Log.Write("并行：第 " + (i + 1) + " 格绑定失败 " + ex.Message); }
                }
                int uniq = new HashSet<string>(boundStrings).Count;
                if (uniq < boundStrings.Count)
                    Log.Write("⚠ 并行：有 " + (boundStrings.Count - uniq + 1) + " 个格子绑到了同一个窗口（"
                              + string.Join(" | ", boundStrings.ToArray()) + "）—— 录出来会是同一场对局");
                Log.Write("并行：建源并绑定完成（" + boundStrings.Count + " 格，" + uniq + " 个不同窗口）");
                // 诊断：把每格音源的「静音 + 音轨」读回来打一行。万一以后又"声音串味"，
                // 这行日志能直接看出是哪一格混进了别的音轨（用户反馈过这个问题，当时没日志可查）。
                for (int i = 0; i < aNames.Count; i++)
                {
                    try
                    {
                        Dictionary<string, object> q2 = new Dictionary<string, object>();
                        q2["inputName"] = aNames[i];
                        bool mm = false;
                        try { mm = Convert.ToBoolean(obs.Request("GetInputMute", q2)["inputMuted"]); } catch { }
                        string tr = "";
                        try
                        {
                            Dictionary<string, object> trs = obs.Request("GetInputAudioTracks", q2);
                            if (trs.ContainsKey("inputAudioTracks"))
                            {
                                Dictionary<string, object> dd = trs["inputAudioTracks"] as Dictionary<string, object>;
                                if (dd != null)
                                    for (int k = 1; k <= 6; k++)
                                        tr += (dd.ContainsKey(k.ToString()) && Convert.ToBoolean(dd[k.ToString()]) ? "1" : "0");
                            }
                        }
                        catch { }
                        // 「音频提前」也读回来：万一这个 OBS 不认 SetInputAudioSyncOffset，
                        // 那一句是【静默】失败的，不读回来就完全看不出补偿到底生效没有。
                        string lead = "?";
                        try
                        {
                            Dictionary<string, object> lr = obs.Request("GetInputAudioSyncOffset", q2);
                            if (lr.ContainsKey("inputAudioSyncOffset"))
                                lead = Convert.ToDouble(lr["inputAudioSyncOffset"]).ToString("0") + "ms";
                        }
                        catch { }
                        Log.Write("并行：第 " + (i + 1) + " 格音源状态 —— 静音=" + (mm ? "是" : "否") +
                                  "  音轨=" + (tr == "" ? "?" : tr) + "（应只有第 " + (i + 1) + " 位是 1）" +
                                  "  同步偏移=" + lead + "（应 = -" + AudioLeadValue() + "ms）");
                    }
                    catch { }
                }
                Log.Write("并行：游戏声音进录像 = " + (cellAudio
                    ? "开（每格一条音轨）"
                    : (cfg.RecordGameAudio ? "关（超过 6 格，音轨不够分）" : "关（音源已静音）")));
                if (!cellAudio)
                {
                    // 超过 6 格时把标准采集源也静音，否则它会把其中一局的声音漏进所有格子
                    SetInputMuteRaw(AudioSourceName, true);
                    Log.Write("并行：已把「" + AudioSourceName + "」也静音，保证这批确实是无声的");
                }

                // 录制期间盯着窗口标题：游戏加载完/开播时会把标题改回去，一改采集源就抓不到窗口。
                // 1 秒一次改回来（这条看守越及时，开头那段黑屏越短、也就越不会切到对局中间）。
                titleWatch = true;
                Thread wt = new Thread(delegate()
                {
                    while (titleWatch && !cancel)
                    {
                        for (int i = 0; i < handles2.Count; i++)
                        {
                            if (handles2[i] == IntPtr.Zero) continue;
                            try
                            {
                                StringBuilder cb = new StringBuilder(512);
                                GetWindowText(handles2[i], cb, 512);
                                string wantTitle = "th123 #" + (i + 1);
                                if (cb.ToString() != wantTitle) SetWindowText(handles2[i], wantTitle);
                            }
                            catch { }
                        }
                        Thread.Sleep(1000);
                    }
                });
                wt.IsBackground = true;
                wt.Start();

                // 等游戏真的开播、标题稳定之后再把绑定刷一次：
                // OBS 的「应用程序音频采集」按窗口标题解析进程，而且只在设置变更时解析一次，
                // 解析那一刻若有两个同名窗口就会锁到同一个进程（两格声音完全一样）。
                Thread.Sleep(6000);
                for (int i = 0; i < games2.Count && i < vNames.Count; i++)
                {
                    if (games2[i] == null || handles2[i] == IntPtr.Zero) continue;
                    try
                    {
                        StringBuilder rb = new StringBuilder(512);
                        GetWindowText(handles2[i], rb, 512);
                        string tt3 = rb.ToString();
                        // 同上：标题空掉/不对就用强制标题，绝不拿空标题去绑（否则那格全黑）
                        if (tt3.Length == 0 || tt3.IndexOf("th123 #", StringComparison.Ordinal) < 0)
                            tt3 = "th123 #" + (i + 1);
                        string ws3 = MakeWindowString(tt3, "th123_110a");
                        ApplyCellWindow(appliedWin, vNames[i], aNames[i], ws3);
                        if (batch.Count <= 6) SetAudioTracksRaw(aNames[i], i + 1);
                        SetInputMuteRaw(aNames[i], !cellAudio);
                    }
                    catch { }
                }
                Log.Write("并行：开播后已再刷新一次采集绑定（音频按各格自己的窗口重新解析）");
                for (int i = 0; i < vNames.Count && i < games2.Count; i++)
                {
                    try { if (games2[i] == null || games2[i].HasExited) continue; } catch { continue; }
                    if (SourceLooksStatic(vNames[i]))
                        Log.Write("并行：第 " + (i + 1) + " 格画面看起来还是静止的（可能在读盘/开场，不影响整场录制）");
                }
                // 每格"是不是黑屏"自检 + 自愈：发现黑的就重新强制标题、重新绑一次采集源。
                // （用户实测：6 格里第一格整场全黑，就是窗口标题空掉、OBS 匹配不到窗口造成的。）
                for (int round = 0; round < 6; round++)
                {
                    Thread.Sleep(3000);
                    bool anyBlack = false;
                    for (int i = 0; i < vNames.Count && i < games2.Count; i++)
                    {
                        try
                        {
                            if (games2[i] == null || games2[i].HasExited) continue;
                            if (!IsBlank(ShotOf(vNames[i]))) continue;
                            anyBlack = true;
                            string fixT = "th123 #" + (i + 1);
                            if (handles2[i] != IntPtr.Zero) { try { SetWindowText(handles2[i], fixT); } catch { } }
                            appliedWin.Remove(vNames[i]);   // 强制重设一次（绕过"没变就跳过"）
                            ApplyCellWindow(appliedWin, vNames[i], aNames[i], MakeWindowString(fixT, "th123_110a"));
                            if (batch.Count <= 6) SetAudioTracksRaw(aNames[i], i + 1);
                            Log.Write("并行：第 " + (i + 1) + " 格画面是黑的 → 已重新绑定窗口（第 " + (round + 1) + " 次检查）");
                        }
                        catch { }
                    }
                    if (!anyBlack) break;
                }
                for (int i = 0; i < vNames.Count && i < games2.Count; i++)
                {
                    try
                    {
                        if (games2[i] == null || games2[i].HasExited) continue;
                        if (IsBlank(ShotOf(vNames[i])))
                        {
                            Log.Write("并行：⚠ 第 " + (i + 1) + " 格一直黑屏，很可能没录上 —— 建议单独重录");
                            blackCells[i] = true;
                        }
                    }
                    catch { }
                }

                DateTime t0 = DateTime.Now;
                bool timeout = false;
                while (!cancel)
                {
                    Thread.Sleep(1000);
                    bool allExited = true;
                    for (int i = 0; i < games2.Count; i++)
                    {
                        try
                        {
                            if (games2[i] == null || games2[i].HasExited)
                            {
                                // 记下这一格是什么时候结束的：切分时按它裁剪，
                                // 免得某局先结束后，视频后面跟着一大段黑屏/别人的画面
                                if (cellSec != null && i < cellSec.Length && cellSec[i] <= 0 && recordStart != DateTime.MinValue)
                                {
                                    cellSec[i] = (DateTime.Now - recordStart).TotalSeconds;
                                    if (cellSec[i] > 10)
                                        Log.Write("并行：第 " + (i + 1) + " 格对局结束（录了 " + cellSec[i].ToString("0") + " 秒），"
                                                  + "切分时按这个长度裁掉后面多余的部分");
                                }
                                continue;
                            }
                            allExited = false;
                        }
                        catch { }
                    }
                    // 各格"画面最后一次出现"是何时：切分时按它剪掉**结尾**的黑。
                    // （每秒采一次。开头的黑不靠这个 —— 那是 AnalyzeCellHead 逐帧看出来的。
                    //   ⚠ 别把间隔放宽到 2 秒以上：cellLastSeen 早 1 秒，成片结尾就多留 1 秒黑，
                    //     而这里省下来的只是每格每秒一次 320×240 截图，收益小得多。）
                    if (recordStart != DateTime.MinValue && cellLastSeen != null && vNames.Count > 0)
                    {
                        for (int i = 0; i < games2.Count && i < vNames.Count; i++)
                        {
                            try
                            {
                                if (games2[i] == null || games2[i].HasExited) continue;
                                double sec = (DateTime.Now - recordStart).TotalSeconds;
                                if (!IsBlank(ShotOf(vNames[i])))
                                    cellLastSeen[i] = sec;
                            }
                            catch { }
                        }
                    }
                    if (allExited) break;
                    // 需求：并行录制也做「窗口被最小化 → 自动还原」（和单录一样的保护）。
                    // 最小化后 Windows 不再为那个窗口做桌面合成，那一格就会拍成黑屏。
                    if (cfg.KeepGameCapturable)
                    {
                        for (int i = 0; i < games2.Count; i++)
                        {
                            try
                            {
                                if (games2[i] == null || games2[i].HasExited) continue;
                                games2[i].Refresh();
                                IntPtr wh = games2[i].MainWindowHandle;
                                if (wh == IntPtr.Zero || !IsIconic(wh)) continue;
                                ShowWindow(wh, SW_RESTORE);
                                minimizedFixCount++;
                                Log.Write("并行：⚠ 第 " + (i + 1) + " 格窗口被最小化（会录成黑屏）—— 已还原（第 "
                                          + minimizedFixCount + " 次）");
                            }
                            catch { }
                        }
                    }
                    if ((DateTime.Now - t0).TotalMinutes > cfg.MaxMinutes) { timeout = true; break; }
                    TimeSpan el = DateTime.Now - t0;
                    string txt = "并行中 " + ((int)el.TotalMinutes) + ":" + el.Seconds.ToString("00");
                    foreach (Job j in batch) Ui.SetJob(j, "录制中", txt);
                }
                titleWatch = false;
                if (timeout)
                {
                    Log.Write("并行录制超过 " + cfg.MaxMinutes + " 分钟上限，强制结束游戏");
                    foreach (Process g in games2) { try { if (g != null && !g.HasExited) g.Kill(); } catch { } }
                }
                Thread.Sleep(2000);
                try
                {
                    Dictionary<string, object> resp = obs.Request("StopRecord", null);
                    if (resp.ContainsKey("outputPath")) gridFile = Convert.ToString(resp["outputPath"]);
                }
                catch (Exception ex) { Log.Write("并行：停止录像失败 " + ex.Message); }
                // 兜底：StopRecord 失败（例如 OBS 报 501）时，自己去录像目录里找刚录出来的那个文件。
                // 以前这里直接放弃 → 用户看到的就是"录完 6 个什么都没拿到、直接报错"。
                if (string.IsNullOrEmpty(gridFile) || !File.Exists(gridFile))
                {
                    try
                    {
                        string dir = null;
                        try { dir = Convert.ToString(obs.Request("GetRecordDirectory", null)["recordDirectory"]); } catch { }
                        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) dir = Path.GetDirectoryName(batch[0].Rep);
                        if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                        {
                            FileInfo newest = null;
                            foreach (string f in Directory.GetFiles(dir))
                            {
                                string ext = Path.GetExtension(f).ToLower();
                                if (ext != ".mp4" && ext != ".mkv" && ext != ".flv" && ext != ".mov" && ext != ".ts") continue;
                                FileInfo fi = new FileInfo(f);
                                if (recordStart != DateTime.MinValue && fi.LastWriteTime < recordStart.AddSeconds(-30)) continue;
                                if (fi.Length < 1024 * 1024) continue;   // 太小的不是这次录的
                                if (newest == null || fi.LastWriteTime > newest.LastWriteTime) newest = fi;
                            }
                            if (newest != null)
                            {
                                gridFile = newest.FullName;
                                Log.Write("并行：StopRecord 没给文件，但在录像目录里找到了刚录的：" + newest.Name);
                            }
                            else Log.Write("并行：录像目录里找不到刚录的文件：" + dir + "（多半是 OBS 没在录）");
                        }
                    }
                    catch (Exception ex2) { Log.Write("并行：找合并文件失败：" + ex2.Message); }
                }
                Log.Write("并行录制结束，合并文件：" + gridFile);
            }
            catch (Exception ex) { Log.Write("并行录制出错：" + ex.Message); }
            finally
            {
                // ⚠ 异常收尾兜底：正常路径要到 L7138/L7147 才停「标题看守」和 StopRecord；
                //   中途一旦抛异常，看守线程会一直给（可能已被系统回收复用的）窗口改名，
                //   而且 OBS 会一直录下去、文件越写越大。这里统一兜住。
                try { titleWatch = false; } catch { }
                try
                {
                    if (obs != null && obs.IsConnected)
                    {
                        Dictionary<string, object> rs = obs.Request("GetRecordStatus", null);
                        if (rs.ContainsKey("outputActive") && Convert.ToBoolean(rs["outputActive"]))
                        {
                            obs.Request("StopRecord", null);
                            Log.Write("并行：异常收尾时发现 OBS 仍在录像，已停止");
                        }
                    }
                }
                catch { }
                // 画面设置（画布/输出/帧率 + 各场景里源的摆放）统一在 RestoreRecordDirectory 里还原 ——
                // 并行、单录两条路都走那个方法，所以这里不再单独写一份（以前那份还会把"输出分辨率"写成画布大小）。
                try { SetProfileParamRaw("SimpleOutput", "RecTracks", string.IsNullOrEmpty(origTracks) ? "1" : origTracks); } catch { }
                foreach (string s in vNames) { try { obs.Request("RemoveInput", new Dictionary<string,object>{{"inputName",s}}); } catch { } }
                foreach (string s in aNames) { try { obs.Request("RemoveInput", new Dictionary<string,object>{{"inputName",s}}); } catch { } }
                try { ApplyAudioRouting(false); } catch { }
                // 把这一批开录前静音掉的麦克风恢复回去（静音在下面建源那段里做的）。
                // 不补这一句，并行录完麦克风就永久哑了 —— 见那段里的注释。
                try { RestoreInputMutes(micOriginalMute, "并行：已把麦克风恢复成录制前的状态"); } catch { }
                try { ApplyGameAudioSetting(); } catch { }   // 把标准采集源的静音状态按设置还原
                if (parMutedByUs)
                {
                    string mm;
                    if (GameAudio.SetSystemMute(false, out mm)) { parMutedByUs = false; Log.Write("并行：已恢复系统声音（" + mm + "）"); MarkSystemMuted(false); }
                }
                try { if (stdItemId >= 0) SetSceneItemEnabled(RecSceneName, stdItemId, stdWasEnabled); } catch { }   // 还原单文件采集源的显示状态
                try { if (cfg.UseDedicatedScene && obs.IsConnected) SwitchScene(false); } catch { }
                try { RestoreRecordDirectory(); } catch { }
                foreach (Process g in games2) { try { if (g != null && !g.HasExited) g.Kill(); } catch { } }
            }

            // 7) 切分
            if (gridFile != null && File.Exists(gridFile))
            {
                int okCount = 0;
                // 1) 先把每格的"开头/结尾该剪到第几秒"算好。
                //    抽帧只看开头 45 秒、只要 160×120 的缩略图，所以**一次 ffmpeg 抽全部格子**
                //    （见 AnalyzeCellHeads 的注释：以前每格一个进程、各解码一遍整张画布）。
                double[] heads = new double[batch.Count];
                double[] ends = new double[batch.Count];
                double[] rawHead = AnalyzeCellHeads(gridFile, batch.Count, cols, CW, CH);
                for (int i = 0; i < batch.Count; i++)
                {
                    double head = 0;
                    try
                    {
                        double h = rawHead[i];
                        if (h > 0.2) head = h - 0.15;
                        Log.Write("并行切分（第 " + (i + 1) + " 格）：画面从第 " + h.ToString("0.0") +
                                  " 秒开始 → 开头剪掉 " + head.ToString("0.0") + " 秒");
                    }
                    catch { }
                    double end = (cellSec != null && i < cellSec.Length) ? cellSec[i] : 0;
                    if (cellLastSeen != null && i < cellLastSeen.Length && cellLastSeen[i] > 0)
                    {
                        double seen = cellLastSeen[i] + 0.25;
                        if (end <= 0 || seen < end) end = seen;
                    }
                    if (head > 0 && end > 0)
                        Log.Write("并行切分（第 " + (i + 1) + " 格）：保留 " + head.ToString("0.0") + " ~ " +
                                  end.ToString("0.0") + " 秒（共 " + (end - head).ToString("0.0") + " 秒）");
                    if (end > 0 && end - head < 5) end = 0;
                    heads[i] = head;
                    ends[i] = end;
                }
                // 2) 切分：一组最多 SplitGroupSize 格共用一个 ffmpeg（一次解码、多路输出），
                //    同时最多跑 conc 组。实测 6 格 37 秒 → 19 秒，画面逐像素相同。
                //    ⚠ conc 现在指的是"同时跑几个 ffmpeg"，而一个 ffmpeg 里最多有 SplitGroupSize 路输出：
                //      6 格 = 2 组，所以实际是 2 个进程 × 3 路。老显卡 NVENC 会话数不够时，
                //      那组会自动退到 libx264 软编（慢但结果一样），再不行退回逐格切分。
                int conc = cfg.SplitParallel;
                if (conc < 1) conc = 1;
                if (conc > 3) conc = 3;              // 上限 3（2026-10-09）；设置里也只列 1~3
                List<int[]> groups = new List<int[]>();
                for (int i0 = 0; i0 < batch.Count; i0 += SplitGroupSize)
                {
                    int len = batch.Count - i0 < SplitGroupSize ? batch.Count - i0 : SplitGroupSize;
                    int[] g = new int[len];
                    for (int k = 0; k < len; k++) g[k] = i0 + k;
                    groups.Add(g);
                }
                if (conc > groups.Count) conc = groups.Count;
                if (batch.Count > 1)
                    Log.Write("并行切分：" + batch.Count + " 格分成 " + groups.Count + " 组（每组最多 " + SplitGroupSize +
                              " 格共用一个 ffmpeg，源只解码一遍），同时跑 " + conc + " 组");
                object splitGate = new object();
                int nextGroup = 0;
                List<Thread> splitThreads = new List<Thread>();
                for (int w = 0; w < conc; w++)
                {
                    Thread st = new Thread(delegate()
                    {
                        while (true)
                        {
                            int gi;
                            lock (splitGate) { gi = nextGroup; nextGroup++; }
                            if (gi >= groups.Count) return;
                            int[] g = groups[gi];
                            string err = "";
                            if (g.Length > 1 && SplitGroup(gridFile, batch, g, cols, CW, CH, heads, ends, out err))
                            {
                                // 落点由 SplitGroup 按实际写出去的那个名字填好了。⚠ 这里【不能】再算一遍：
                                // 「再录一次」时第一次算出来是 xxx (2).mp4、而文件此刻已经存在，
                                // 再算一次会得到 xxx (3).mp4 —— 于是列表指着一个不存在的文件。
                                foreach (int i in g) MarkSplitDone(batch[i], i, blackCells[i]);
                                lock (splitGate) okCount += g.Length;
                                continue;
                            }
                            // 一组里只要有一格出问题就整组退回逐格切（SplitOne 有自己的编码器梯子）。
                            // 稳妥优先：正确性靠它兜底，速度靠上面那条路。
                            if (g.Length > 1)
                                Log.Write("并行切分：这 " + g.Length + " 格一起切失败（" + err + "），退回逐格重切…");
                            foreach (int i in g)
                            {
                                string err2;
                                if (SplitOne(gridFile, batch[i], i, CW, CH, (i % cols) * CW, (i / cols) * CH,
                                             heads[i], ends[i], out err2))
                                {
                                    MarkSplitDone(batch[i], i, blackCells[i]);
                                    lock (splitGate) okCount++;
                                }
                                else
                                {
                                    Ui.SetJob(batch[i], "失败", "切分失败");
                                    Log.Write("并行切分失败（第 " + (i + 1) + " 格）：" + err2 + "；合并文件保留在 " + gridFile);
                                }
                            }
                        }
                    });
                    st.IsBackground = true;
                    st.Start();
                    splitThreads.Add(st);
                }
                // ⚠ 无参 Join() 会把整批卡死：某个切分线程里的 ffmpeg 卡住时它永远不返回。
                //   给每个线程留"一次 ffmpeg 超时 + 收尾"的余量，到点就不再等它
                //   （那个线程自己会在 FfmpegTimeoutMs 到点后杀掉 ffmpeg 并结束）。
                int joinMs = FfmpegTimeoutMs + 60000;
                foreach (Thread st in splitThreads)
                {
                    try
                    {
                        if (!st.Join(joinMs))
                            Log.Write("并行：有切分线程超过 " + (joinMs / 60000) + " 分钟没结束，不再等它");
                    }
                    catch { }
                }
                if (okCount == batch.Count)
                {
                    try { File.Delete(gridFile); Log.Write("并行：已删除中间合并文件（切分全部成功）"); } catch { }
                }
                else Log.Write("并行：有切分失败，中间合并文件保留在 " + gridFile + "（可用 ffmpeg 手动裁）");

                // 录完问用户要把视频放哪 —— 并行这条路以前从来不问（用户实测就是在等这个弹窗）
                if (okCount > 0)
                {
                    if (!queueSaveDecided)
                    {
                        string choice = AskSaveLocationDialog(batch[0], okCount);
                        if (!string.IsNullOrEmpty(choice) && choice != "keep")
                            foreach (Job j in batch)
                            {
                                if (j.Output == null || !File.Exists(j.Output)) continue;
                                string moved = MoveOutputTo(j, choice);
                                if (moved != null) { j.Output = moved; j.Size = new FileInfo(moved).Length; Ui.SetJob(j, "完成", "已放到 " + Path.GetDirectoryName(moved)); }
                            }
                    }
                    else if (!string.IsNullOrEmpty(queueSaveFolder))
                    {
                        foreach (Job j in batch)
                        {
                            if (j.Output == null || !File.Exists(j.Output)) continue;
                            string moved = MoveOutputTo(j, queueSaveFolder);
                            if (moved != null) { j.Output = moved; j.Size = new FileInfo(moved).Length; Ui.SetJob(j, "完成", "已放到 " + queueSaveFolder); }
                        }
                    }
                }
            }
            else
            {
                Log.Write("并行录制：没有拿到合并视频文件，无法切分");
                foreach (Job j in batch) Ui.SetJob(j, "失败", "并行录制没拿到文件");
            }
            Log.Write("==== 并行录制结束 ====");
        }
        void StartRecording()
        {
            // 「准备 OBS」正在后台跑的时候不许开录：那会儿它正在改画布/建采集源，
            // 两边同时动 OBS 会把那一批搞成黑屏或直接失败（按钮已经被置灰，
            // 这一句挡的是快捷键那条路）。
            if (prepareBusy) { Log.Write("正在准备 OBS，稍等一下再开始"); return; }

            // ⚠ 游戏目录不对就别开录（2026-10-08 查实）：以前这里什么都不拦，
            //   结果每个游戏实例启动失败、录出一整批黑视频，**而且任务还标成「完成」** ——
            //   用户以为录好了。宁可不录，也要当场说清楚。
            if (cfg == null || string.IsNullOrEmpty(cfg.GameDir) || !File.Exists(Path.Combine(cfg.GameDir, "th123.exe")))
            {
                Log.Write("游戏目录不对（" + (cfg == null || string.IsNullOrEmpty(cfg.GameDir) ? "空" : cfg.GameDir) +
                          "），先配好再录");
                Ui.Status("游戏目录不对 —— 先点「检查环境」");
                try { Ui.ShowEnvDialog(EnvReport.Build(cfg)); RefreshEnvLabel(); } catch { }
                return;
            }
            // 并行（同时录 >=2 个）每一格都要调 ffmpeg 去切分；缺了它整批都会「切分失败」。
            // 别让用户等到录完才发现 —— 开录前就弹窗，让他当场补。
            if (cfg.ParallelCount >= 2 && FindFfmpeg() == null)
            {
                Log.Write("没有 ffmpeg —— 并行录制需要它，先把环境补齐");
                Ui.Status("缺 ffmpeg，并行录制需要它");
                try { Ui.ShowEnvDialog(EnvReport.Build(cfg)); RefreshEnvLabel(); } catch { }
                if (FindFfmpeg() == null) return;
            }
            List<Job> sel = new List<Job>();
            bool manualSel = false;
            // ★ 轮录优先：只要勾了「轮录」，就【从队列第一个 rep 开始数】，按每批 N 个一路录完，
            //   完全忽略当前的蓝色选中 / 绿色标记 —— 否则"选中下面 6 个、上面留 2 个"就会从第 3 个开始算
            //   （用户实测的 bug）。不勾轮录时，选中/标记仍然是"只录这几个"。
            bool round = cfg.RoundRecord;
            lock (jobsLock)
            {
                if (round)
                {
                    foreach (Job j in jobs) if (j.Status == "等待") sel.Add(j);   // 队列顺序 = 列表顺序
                }
                else
                {
                    foreach (Job it in Ui.SelectedJobs())
                    {
                        Job j = it;
                        if (j != null && !sel.Contains(j)) sel.Add(j);
                    }
                    if (sel.Count == 0 && pinnedBatch != null && pinnedBatch.Count > 0)
                        foreach (Job pj in pinnedBatch) if (!sel.Contains(pj)) sel.Add(pj);
                }
                manualSel = !round && sel.Count > 0;
                if (round && sel.Count == 0)
                {
                    Log.Write("轮录：没有待录的 rep（都录过了？点「刷新」或选中几行要重录的）");
                    return;
                }
                if (!round && sel.Count == 0 && jobs.Count == 0) { Log.Write("队列是空的，先把 rep 拖进来"); return; }
                if (round) pinnedBatch.Clear();   // 轮录不看标记，顺手清干净，免得界面上还绿着
            }
            cfg.FillDefaults();
            int per = Math.Max(1, Math.Min(6, cfg.ParallelCount));

            int pending = 0;
            lock (jobsLock) foreach (Job j in jobs) if (j.Status == "等待") pending++;

            if (round)
            {
                Log.Write("==== 轮录开始：共 " + sel.Count + " 个，每批 " + per + " 个"
                          + (per == 1 ? "（依次单录）" : "") + "，从队列最前面数 ====");
            }

            // 需求 0：保存位置提前问，选好了再开录。
            // 2026-10-05 用户要求：**不论单录 / 并行 / 并行+轮录，一律先问**，
            // 不再受设置里那个「AskSaveLocation」开关和「以后不再询问」的影响
            // （以前点过一次「以后不再询问」就再也不问了，用户找不到地方改回来）。
            {
                int n = manualSel ? sel.Count : (round ? sel.Count : pending);
                if (!AskSaveLocationUpFront(n)) return;
            }

            if (sel.Count > 0)
            {
                selectedBatch = sel;
                if (manualSel) forceRecord = true;    // 明确选中 → 忽略「已有录像就跳过」（一律重录）
                if (manualSel)
                    Log.Write("开始录制：你选中了 " + sel.Count + " 个，同时录 " + per + " 个" +
                              (sel.Count > per ? "，分 " + ((sel.Count + per - 1) / per) + " 批" : "") + "（选中的一律重录）");
            }
            else
            {
                selectedBatch = null;
                if (pending == 0) { Log.Write("没有待录制的 rep（都录过了？点「刷新」重新检查，或先在列表里选中要重录的）"); return; }
                Log.Write("开始录制：待录 " + pending + " 个，同时录 " + per + " 个"
                          + (pending > per ? "（只录这一批，想全录完请勾「轮录」）" : "")
                          + (pending == 1 ? "（只有 1 个，走单录）" : ""));
            }
            parallelRequested = true;
            Ui.Post(delegate { btnStart.Enabled = false; btnStop.Enabled = true; });
            StartQueue();
        }
        static double Mp4Duration(string path)
        {
            try
            {
                using (FileStream fs = File.OpenRead(path))
                {
                    long len = fs.Length;
                    byte[] b8 = new byte[8];
                    long pos = 0;
                    while (pos + 8 <= len)
                    {
                        fs.Position = pos;
                        if (fs.Read(b8, 0, 8) != 8) break;
                        long size = ((long)b8[0] << 24) | ((long)b8[1] << 16) | ((long)b8[2] << 8) | b8[3];
                        string type = Encoding.ASCII.GetString(b8, 4, 4);
                        long hdr = 8;
                        if (size == 1)
                        {
                            byte[] e = new byte[8];
                            if (fs.Read(e, 0, 8) != 8) break;
                            size = 0; for (int k = 0; k < 8; k++) size = (size << 8) | e[k];
                            hdr = 16;
                        }
                        else if (size == 0) size = len - pos;
                        if (size < hdr) break;
                        if (type == "moov")
                        {
                            long end = Math.Min(pos + size, len);
                            long p = pos + hdr;
                            while (p + 8 <= end)
                            {
                                fs.Position = p;
                                if (fs.Read(b8, 0, 8) != 8) break;
                                long sz = ((long)b8[0] << 24) | ((long)b8[1] << 16) | ((long)b8[2] << 8) | b8[3];
                                string t2 = Encoding.ASCII.GetString(b8, 4, 4);
                                long h2 = 8;
                                if (sz == 1)
                                {
                                    byte[] e = new byte[8];
                                    if (fs.Read(e, 0, 8) != 8) break;
                                    sz = 0; for (int k = 0; k < 8; k++) sz = (sz << 8) | e[k];
                                    h2 = 16;
                                }
                                else if (sz == 0) sz = end - p;
                                if (t2 == "mvhd")
                                {
                                    byte[] body = new byte[32];
                                    fs.Position = p + h2;
                                    int got = fs.Read(body, 0, 32);
                                    if (got < 20) return 0;
                                    if (body[0] == 1)
                                    {
                                        long ts = ((long)body[20] << 24) | ((long)body[21] << 16) | ((long)body[22] << 8) | body[23];
                                        long dur = 0; for (int k = 24; k < 32; k++) dur = (dur << 8) | body[k];
                                        return ts > 0 ? (double)dur / ts : 0;
                                    }
                                    long ts0 = ((long)body[12] << 24) | ((long)body[13] << 16) | ((long)body[14] << 8) | body[15];
                                    long dur0 = ((long)body[16] << 24) | ((long)body[17] << 16) | ((long)body[18] << 8) | body[19];
                                    return ts0 > 0 ? (double)dur0 / ts0 : 0;
                                }
                                if (sz <= 0) break;
                                p += sz;
                            }
                            return 0;
                        }
                        if (size <= 0) break;
                        pos += size;
                    }
                }
            }
            catch { }
            return 0;
        }
        public static string FmtDur(double seconds)
        {
            if (double.IsNaN(seconds) || seconds < 0) seconds = 0;
            long t = (long)Math.Round(seconds);
            long h = t / 3600, m = (t % 3600) / 60, s = t % 60;
            if (h > 0) return h + "小时" + m.ToString("00") + "分" + s.ToString("00") + "秒";
            if (m > 0) return m + "分" + s.ToString("00") + "秒";
            return s + "秒";
        }
        bool EnsureObs()
        {
            string obsExe = Settings.FindObsExe();
            if (!string.IsNullOrEmpty(cfg.ObsExe) && File.Exists(cfg.ObsExe)) obsExe = cfg.ObsExe;
            if (string.IsNullOrEmpty(obsExe))
            {
                Log.Write("找不到 obs64.exe —— 先安装 OBS：  winget install --id OBSProject.OBSStudio -e");
                return false;
            }
            cfg.ObsExe = obsExe;

            if (Process.GetProcessesByName("obs64").Length == 0)
            {
                string wsMsg;
                Settings.EnsureWebSocketEnabled(out wsMsg);
                Log.Write(wsMsg);
                Log.Write("OBS 未运行，正在启动：" + obsExe);
                ProcessStartInfo psi = new ProcessStartInfo(obsExe, "--minimize-to-tray");
                psi.UseShellExecute = false;
                psi.WorkingDirectory = Path.GetDirectoryName(obsExe);
                Process.Start(psi);
                Thread.Sleep(3000);
            }

            DateTime deadline = DateTime.Now.AddSeconds(60);
            Exception last = null;
            while (DateTime.Now < deadline)
            {
                if (cancel) return false;
                try
                {
                    obs.MetersEnabled = true;   // 订阅音频电平事件（用于录制时自检有没有声音）
                    obs.Connect(cfg.Port, cfg.Password);
                    // 连上之后先补一次账：设置里若还留着上次没还的快照（被强杀/断电），现在就还给用户。
                    // 这样"下次开程序"就等于补还原了，用户不用自己去 OBS 里改回来。
                    if (!string.IsNullOrEmpty(cfg.ObsVideoSnapshot)) RestoreObsVideo();
                    return true;
                }
                catch (Exception ex)
                {
                    last = ex;
                    Thread.Sleep(2000);
                }
            }
            Log.Write("连接 obs-websocket 失败：" + (last == null ? "超时" : last.Message));
            Log.Write("请检查：OBS → 工具 → WebSocket 服务器设置 → 启用（端口 " + cfg.Port + "）");
            return false;
        }
        void PrepareObs(bool verbose)
        {
            try
            {
                StopObsRecordingIfAny();   // 上次被强杀留下的"没停的录像"要先停掉，否则后面全报 500

                // ⚠ 必须在改画布【之前】拍快照：并行那条路随后还会再改一次画布（改成 6 格那么大），
                //   但都是这一批的事 —— 拍一张就够，这一张里含着用户自己的画布/输出缩放/帧率和各源的摆放。
                SnapshotObsVideo();

                if (cfg.AutoCanvas)
                {
                    Dictionary<string, object> v = new Dictionary<string, object>();
                    v["baseWidth"] = cfg.CanvasWidth;
                    v["baseHeight"] = cfg.CanvasHeight;
                    v["outputWidth"] = cfg.CanvasWidth;
                    v["outputHeight"] = cfg.CanvasHeight;
                    v["fpsNumerator"] = cfg.CanvasFps;
                    v["fpsDenominator"] = 1;
                    obs.Request("SetVideoSettings", v);
                    Log.Write("OBS 画布/输出已设为 " + cfg.CanvasWidth + "×" + cfg.CanvasHeight + "@" + cfg.CanvasFps);
                }

                // 记下原来的节目场景，录完要还回去
                try
                {
                    originalScene = Convert.ToString(obs.Request("GetSceneList", null)["currentProgramSceneName"]);
                }
                catch { originalScene = null; }

                List<object> inputs = ObsClient.ToList(obs.Request("GetInputList", null)["inputs"]);

                if (!cfg.UseDedicatedScene)
                {
                    // 旧行为：用当前场景里已有的采集源（可能是显示器采集，会录到整个桌面）
                    string existing = null;
                    foreach (object o in inputs)
                    {
                        Dictionary<string, object> m = (Dictionary<string, object>)o;
                        string kind = Convert.ToString(m["unversionedInputKind"]);
                        if (kind == "game_capture" || kind == "window_capture" ||
                            kind == "monitor_capture" || kind == "screen_capture")
                        {
                            existing = Convert.ToString(m["inputName"]);
                            break;
                        }
                    }
                    if (existing != null)
                    {
                        captureSource = existing;
                        captureSourceIsOurs = false;
                        try { captureScene = Convert.ToString(obs.Request("GetSceneList", null)["currentProgramSceneName"]); }
                        catch { captureScene = null; }
                        Log.Write("使用当前场景里已有的采集源：" + existing);
                        return;
                    }
                }

                // 专用录制场景 + 只放一个绑定游戏窗口的窗口采集源
                bool sceneExists = false;
                try
                {
                    List<object> scenes = ObsClient.ToList(obs.Request("GetSceneList", null)["scenes"]);
                    foreach (object s in scenes)
                    {
                        Dictionary<string, object> sm = (Dictionary<string, object>)s;
                        if (Convert.ToString(sm["sceneName"]) == RecSceneName) { sceneExists = true; break; }
                    }
                }
                catch { }
                if (!sceneExists)
                {
                    Dictionary<string, object> cr = new Dictionary<string, object>();
                    cr["sceneName"] = RecSceneName;
                    obs.Request("CreateScene", cr);
                    Log.Write("已创建录制专用场景：" + RecSceneName);
                }

                bool sourceExists = false;
                foreach (object o in inputs)
                {
                    Dictionary<string, object> m = (Dictionary<string, object>)o;
                    if (Convert.ToString(m["inputName"]) == RecSourceName) { sourceExists = true; break; }
                }

                if (!sourceExists)
                {
                    Dictionary<string, object> settings = new Dictionary<string, object>();
                    settings["method"] = 0;
                    settings["client_area"] = true;
                    settings["cursor"] = false;
                    Dictionary<string, object> req = new Dictionary<string, object>();
                    req["sceneName"] = RecSceneName;
                    req["inputName"] = RecSourceName;
                    req["inputKind"] = "window_capture";
                    req["inputSettings"] = settings;
                    req["sceneItemEnabled"] = true;
                    obs.Request("CreateInput", req);
                    Log.Write("已创建窗口采集源：" + RecSourceName);
                }

                // 同一个源可以被同一个场景添加多次（OBS 允许），
                // 所以这里必须"只保留一个元素、把多余的删掉"，否则画面上会叠出好几个游戏画面。
                // （这段以前是内联写在这里的，和 CleanupDuplicateItems 是同一份逻辑 —— 2026-10-12 合并成一句。）
                int keepItemId = CleanupDuplicateItems(RecSceneName, RecSourceName);

                // 场景元素可能是「隐藏」状态：并行录制期间会把单文件采集源临时隐藏，如果那次程序被强制结束
                // （任务管理器结束/崩溃/断电），隐藏状态就留在 OBS 里了 —— 之后所有单录都会录成全黑
                // （音频却正常，因为声音是另一个源）。这里强制显示一次。
                if (keepItemId >= 0)
                {
                    try
                    {
                        Dictionary<string, object> q0 = new Dictionary<string, object>();
                        q0["sceneName"] = RecSceneName;
                        q0["sceneItemId"] = keepItemId;
                        if (!Convert.ToBoolean(obs.Request("GetSceneItemEnabled", q0)["sceneItemEnabled"]))
                        {
                            SetSceneItemEnabled(RecSceneName, keepItemId, true);
                            Log.Write("画面采集源原本处于隐藏状态（多半是上次并行录制被强制结束留下的），已重新显示");
                        }
                    }
                    catch { }
                }

                if (keepItemId < 0)
                {
                    try
                    {
                        Dictionary<string, object> req = new Dictionary<string, object>();
                        req["sceneName"] = RecSceneName;
                        req["sourceName"] = RecSourceName;
                        req["sceneItemEnabled"] = true;
                        Dictionary<string, object> resp = obs.Request("CreateSceneItem", req);
                        if (resp.ContainsKey("sceneItemId")) keepItemId = Convert.ToInt32(resp["sceneItemId"]);
                    }
                    catch (Exception ex) { Log.Write("添加采集源到场景失败：" + ex.Message); }
                }

                // 只要游戏声音：再加一个「应用程序音频采集(th123.exe)」，录制时会把 OBS 桌面音频静音避免重复
                if (cfg.GameAudioOnly)
                {
                    bool audioExists = false;
                    foreach (object o in inputs)
                    {
                        Dictionary<string, object> m = (Dictionary<string, object>)o;
                        if (Convert.ToString(m["inputName"]) == AudioSourceName) { audioExists = true; break; }
                    }
                    if (!audioExists)
                    {
                        Dictionary<string, object> aset = new Dictionary<string, object>();
                        aset["window"] = "";
                        Dictionary<string, object> areq = new Dictionary<string, object>();
                        areq["sceneName"] = RecSceneName;
                        areq["inputName"] = AudioSourceName;
                        areq["inputKind"] = "wasapi_process_output_capture";
                        areq["inputSettings"] = aset;
                        areq["sceneItemEnabled"] = true;
                        obs.Request("CreateInput", areq);
                        Log.Write("已创建应用程序音频采集源（只录游戏声音）：" + AudioSourceName);
                    }
                    else
                    {
                        // 已存在：只确保它在场景里，绝不重复添加（否则源列表/混音器会出现一堆同名项）
                        try
                        {
                            Dictionary<string, object> req = new Dictionary<string, object>();
                            req["sceneName"] = RecSceneName;
                            req["sourceName"] = AudioSourceName;
                            req["sceneItemEnabled"] = true;
                            obs.Request("CreateSceneItem", req);
                        }
                        catch { }
                    }
                    // 清理历史遗留的重复元素（同名音频源多个 scene item）
                    CleanupDuplicateItems(RecSceneName, AudioSourceName);
                    ApplyGameAudioSetting();   // 按「录打击音效」开关决定这个源要不要静音
                    // 单录这条路也要设「音频提前」：这个源是【常驻】的（建过一次就一直留着），
                    // 所以每次都按当前设置刷一遍，改过设置、或换了设置文件都能跟上。
                    SetAudioLeadRaw(AudioSourceName, AudioLeadValue());
                }

                captureSource = RecSourceName;
                captureSourceIsOurs = true;
                captureScene = RecSceneName;
                FitSourceToCanvas(RecSceneName, keepItemId);
            }
            catch (Exception ex)
            {
                Log.Write("准备 OBS 时出错：" + ex.Message);
            }
        }
        void SwitchScene(bool toOurs)
        {
            try
            {
                Dictionary<string, object> r = new Dictionary<string, object>();
                r["sceneName"] = toOurs ? RecSceneName : (originalScene == null ? RecSceneName : originalScene);
                obs.Request("SetCurrentProgramScene", r);
                if (!toOurs) Log.Write("已切回原来的场景：" + originalScene);
            }
            catch (Exception ex) { Log.Write("切换场景失败：" + ex.Message); }
        }
        // ── OBS 画面设置快照 / 还原 ──────────────────────────────────────────────
        // 为什么要有（2026-10-14 用户实测）：并行录制会把画布改成"6 格那么大"、单录会改成 1440×1080，
        //   而 OBS 在画布变化时会**顺带重算场景里每个源的摆放** —— 于是用户自己那个「显示器采集」
        //   被挪到画布外、录屏变成全黑（他以为程序把 OBS 弄坏了）。
        // 以前的实现有三个洞：① 只有并行那条路收尾还原，**单录不还原**；
        //   ② 写回时把"输出分辨率"也写成了画布大小 —— 用户设的输出缩放（比如 1152×648）就这样没了；
        //   ③ 包在 catch{} 里、异常收尾还会跳过 —— 失败一句话都不写，用户只能自己发现画面被改了。
        // 现在：开录前**整份**快照（画布+输出+帧率+缩放算法，以及各场景里每个源的摆放），
        //   收尾**整份**写回；快照同时写进设置文件 —— 被强杀时下次一连上 OBS 就补还原（见 EnsureObs）。
        static readonly string[] ObsTransformKeys = new string[] {
            "positionX", "positionY", "rotation", "scaleX", "scaleY", "alignment",
            "boundsType", "boundsAlignment", "boundsWidth", "boundsHeight",
            "cropLeft", "cropRight", "cropTop", "cropBottom" };

        // 开录前调（在 PrepareObs 里、改画布之前）。⚠ 设置里若还留着上一批的快照，先还掉再拍 ——
        //   否则会把"已经被改坏的状态"当成用户的原样拍下来。
        void SnapshotObsVideo()
        {
            if (!string.IsNullOrEmpty(cfg.ObsVideoSnapshot))
            {
                Log.Write("OBS 快照：发现上一批没收尾（多半被强杀过），先还原再重新记录");
                RestoreObsVideo();
            }
            Dictionary<string, object> shot = new Dictionary<string, object>();
            try { shot["video"] = obs.Request("GetVideoSettings", null); }
            catch (Exception ex) { Log.Write("OBS 快照：读画面设置失败（这一批不还原画布）：" + ex.Message); }
            // 节目场景也要记：程序录制时会把节目场景切到它自己的「RepRecorder 录制场景」，
            // 正常收尾 SwitchScene(false) 会切回来；但被强杀就不会 —— 用户打开 OBS 只看到黑乎乎的游戏窗口场景
            // （2026-10-18 用户实际撞到）。记进快照就能在"下次连上 OBS"时一起还回来。
            try { shot["scene"] = Convert.ToString(obs.Request("GetSceneList", null)["currentProgramSceneName"]); }
            catch { }
            List<object> items = new List<object>();
            try
            {
                foreach (object so in ObsClient.ToList(obs.Request("GetSceneList", null)["scenes"]))
                {
                    Dictionary<string, object> sc = (Dictionary<string, object>)so;
                    string sceneName = Convert.ToString(sc["sceneName"]);
                    List<object> list;
                    try
                    {
                        list = ObsClient.ToList(obs.Request("GetSceneItemList",
                            new Dictionary<string, object> { { "sceneName", sceneName } })["sceneItems"]);
                    }
                    catch { continue; }
                    foreach (object io in list)
                    {
                        Dictionary<string, object> it = (Dictionary<string, object>)io;
                        int sid = Convert.ToInt32(it["sceneItemId"]);
                        Dictionary<string, object> tr;
                        try
                        {
                            tr = (Dictionary<string, object>)obs.Request("GetSceneItemTransform",
                                new Dictionary<string, object> { { "sceneName", sceneName }, { "sceneItemId", sid } })["sceneItemTransform"];
                        }
                        catch { continue; }
                        Dictionary<string, object> keep = new Dictionary<string, object>();
                        keep["sceneName"] = sceneName;
                        keep["sceneItemId"] = sid;
                        keep["sourceName"] = Convert.ToString(it["sourceName"]);
                        foreach (string k in ObsTransformKeys) if (tr.ContainsKey(k)) keep[k] = tr[k];
                        items.Add(keep);
                    }
                }
            }
            catch (Exception ex) { Log.Write("OBS 快照：读场景里源的摆放失败（这一批不还原摆放）：" + ex.Message); }
            shot["items"] = items;
            try
            {
                cfg.ObsVideoSnapshot = new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(shot);
                cfg.Save();
            }
            catch (Exception ex) { Log.Write("OBS 快照：写进设置文件失败（被强杀就补不还原了）：" + ex.Message); }
            if (shot["video"] != null)
            {
                Dictionary<string, object> v = (Dictionary<string, object>)shot["video"];
                Log.Write("OBS 快照：已记下画布 " + v["baseWidth"] + "×" + v["baseHeight"] +
                          "（输出 " + v["outputWidth"] + "×" + v["outputHeight"] + "）和 " + items.Count + " 个源的摆放");
            }
        }

        // 收尾调（正常/异常都要走，见 RestoreRecordDirectory）。失败一律写日志 —— 别再静默。
        void RestoreObsVideo()
        {
            string raw = cfg.ObsVideoSnapshot;
            if (string.IsNullOrEmpty(raw)) return;
            System.Web.Script.Serialization.JavaScriptSerializer js =
                new System.Web.Script.Serialization.JavaScriptSerializer();
            Dictionary<string, object> shot = null;
            try { shot = js.DeserializeObject(raw) as Dictionary<string, object>; } catch { }
            if (shot == null)
            {
                Log.Write("OBS 还原：快照读不出来（跳过），已清掉");
                cfg.ObsVideoSnapshot = ""; try { cfg.Save(); } catch { }
                return;
            }
            try
            {
                Dictionary<string, object> v = shot.ContainsKey("video") ? shot["video"] as Dictionary<string, object> : null;
                if (v != null)
                {
                    obs.Request("SetVideoSettings", v);
                    Log.Write("OBS 还原：画布 " + v["baseWidth"] + "×" + v["baseHeight"] +
                              "、输出 " + v["outputWidth"] + "×" + v["outputHeight"]);
                }
            }
            catch (Exception ex) { Log.Write("OBS 还原：画面设置没还成 —— " + ex.Message); }
            try
            {
                string scene = shot.ContainsKey("scene") ? Convert.ToString(shot["scene"]) : null;
                if (!string.IsNullOrEmpty(scene))
                {
                    obs.Request("SetCurrentProgramScene", new Dictionary<string, object> { { "sceneName", scene } });
                    Log.Write("OBS 还原：节目场景回「" + scene + "」");
                }
            }
            catch (Exception ex) { Log.Write("OBS 还原：节目场景没还成 —— " + ex.Message); }
            int ok = 0, miss = 0;
            try
            {
                System.Collections.IEnumerable list = shot.ContainsKey("items") ? shot["items"] as System.Collections.IEnumerable : null;
                if (list != null)
                    foreach (object o in list)
                    {
                        Dictionary<string, object> it = o as Dictionary<string, object>;
                        if (it == null) continue;
                        try
                        {
                            Dictionary<string, object> req = new Dictionary<string, object>();
                            req["sceneName"] = it["sceneName"];
                            req["sceneItemId"] = Convert.ToInt32(it["sceneItemId"]);
                            Dictionary<string, object> t = new Dictionary<string, object>();
                            foreach (string k in ObsTransformKeys) if (it.ContainsKey(k)) t[k] = it[k];
                            req["sceneItemTransform"] = t;
                            obs.Request("SetSceneItemTransform", req);
                            ok++;
                        }
                        catch { miss++; }   // 场景/元素已经不在了（比如本程序自己建的那个场景），跳过
                    }
            }
            catch (Exception ex) { Log.Write("OBS 还原：源的摆放没还成 —— " + ex.Message); }
            if (ok + miss > 0)
                Log.Write("OBS 还原：源的位置还回 " + ok + " 个" + (miss > 0 ? "（" + miss + " 个已不存在，跳过）" : ""));
            cfg.ObsVideoSnapshot = "";
            try { cfg.Save(); } catch { }
        }

        void RestoreRecordDirectory()
        {
            // 顺手把 OBS 的画面设置也还回去（这一批改过的画布/输出分辨率 + 场景里源的摆放）。
            // ⚠ 放在最前面：下面那个 return 是"录像目录没记过"时才走的，画面设置照样要还。
            RestoreObsVideo();
            if (originalRecordDir == null || !obs.IsConnected) return;
            if (string.IsNullOrEmpty(originalRecordDir)) return;
            Dictionary<string, object> r = new Dictionary<string, object>();
            r["recordDirectory"] = originalRecordDir;
            obs.Request("SetRecordDirectory", r);
            Log.Write("已还原 OBS 录像目录：" + originalRecordDir);
        }
        void RestoreInputMutes(Dictionary<string, bool> saved, string logMessage)
        {
            foreach (KeyValuePair<string, bool> kv in saved)
            {
                SetInputMuteRaw(kv.Key, kv.Value);
            }
            if (saved.Count > 0) Log.Write(logMessage);
            saved.Clear();
        }
        void ApplyMicMode(bool beforeRecording)
        {
            try
            {
                if (beforeRecording)
                {
                    // 用【有效】模式，不是 cfg.MicMode：并行（数量≥2）时一律当 gameOnly，
                    // 哪怕设置里存着「游戏+麦克风」—— 界面那个下拉在并行时也是锁成
                    // 「只录游戏声音」的（ShellWindow.ApplyMicLock），两边必须同一套规矩。
                    bool wantMuted = (cfg.EffectiveMicMode != "gameAndMic");
                    List<string> targets = Program.FindMicTargets(obs);
                    if (targets.Count == 0)
                    {
                        Log.Write("没找到麦克风设备，跳过");
                        return;
                    }
                    foreach (string name in targets)
                    {
                        try
                        {
                            Dictionary<string, object> q = new Dictionary<string, object>();
                            q["inputName"] = name;
                            bool was = Convert.ToBoolean(obs.Request("GetInputMute", q)["inputMuted"]);
                            if (!micOriginalMute.ContainsKey(name)) micOriginalMute[name] = was;
                            if (was == wantMuted)
                            {
                                Log.Write("麦克风 " + name + "：" + (was ? "已静音" : "已开启") + "（符合当前录音模式，不用改）");
                                continue;
                            }
                            Dictionary<string, object> s = new Dictionary<string, object>();
                            s["inputName"] = name;
                            s["inputMuted"] = wantMuted;
                            obs.Request("SetInputMute", s);
                            Log.Write(wantMuted
                                ? ("已静音麦克风（录制期间只留游戏声音）：" + name)
                                : ("已开启麦克风（录制期间会录进解说）：" + name));
                        }
                        catch (Exception ex) { Log.Write("设置麦克风 " + name + " 失败：" + ex.Message); }
                    }
                }
                else
                {
                    RestoreInputMutes(micOriginalMute, "已把麦克风恢复成录制前的状态");
                }
            }
            catch (Exception ex) { Log.Write("处理麦克风失败（可忽略）：" + ex.Message); }
        }
        void ApplyAudioRouting(bool beforeRecording)
        {
            // 两种情况都要临时静音 OBS 的桌面音频：
            //   1) GameAudioOnly：只录游戏声音，避免把系统里的其他声音混进来；
            //   2) 「录打击音效」被关掉：录像必须是真的无声，桌面音频也不能漏进去。
            if (!cfg.GameAudioOnly && cfg.RecordGameAudio) return;
            try
            {
                if (beforeRecording)
                {
                    List<string> desks = Program.FindDesktopTargets(obs);
                    foreach (string name in desks)
                    {
                        try
                        {
                            Dictionary<string, object> q = new Dictionary<string, object>();
                            q["inputName"] = name;
                            bool was = Convert.ToBoolean(obs.Request("GetInputMute", q)["inputMuted"]);
                            if (!deskOriginalMute.ContainsKey(name)) deskOriginalMute[name] = was;
                            if (was) continue;
                            Dictionary<string, object> s = new Dictionary<string, object>();
                            s["inputName"] = name;
                            s["inputMuted"] = true;
                            obs.Request("SetInputMute", s);
                            Log.Write("已静音 OBS 桌面音频：" + name);
                        }
                        catch (Exception ex) { Log.Write("静音桌面音频 " + name + " 失败：" + ex.Message); }
                    }
                }
                else
                {
                    RestoreInputMutes(deskOriginalMute, "已恢复 OBS 桌面音频");
                }
            }
            catch (Exception ex) { Log.Write("处理音频路由失败（可忽略）：" + ex.Message); }
        }
        void KeepGameWindowCapturable(Process game)
        {
            if (!cfg.KeepGameCapturable || game == null) return;
            try
            {
                game.Refresh();
                if (game.HasExited) return;
                IntPtr h = game.MainWindowHandle;
                if (h == IntPtr.Zero) return;
                if (!IsIconic(h)) return;

                ShowWindow(h, SW_RESTORE);
                minimizedFixCount++;
                Log.Write("⚠ 游戏窗口被最小化（会录成黑屏）—— 已还原（第 " + minimizedFixCount + " 次）。录制期间请【盖住】它，别最小化");
            }
            catch { }
        }
        void LogAudioLevels(int round)
        {
            if (!obs.MetersFresh)
            {
                Log.Write("音频电平第" + round + "次：还没收到电平事件（可能 OBS 没在推，或没订阅）");
                return;
            }
            string s = "";
            foreach (string n in obs.LevelNames())
            {
                if (n != AudioSourceName && n.IndexOf("桌面音频") < 0 && n.IndexOf("麦克风") < 0) continue;
                double v = obs.GetLevel(n);
                bool muted = false;
                try
                {
                    Dictionary<string, object> q = new Dictionary<string, object>();
                    q["inputName"] = n;
                    muted = Convert.ToBoolean(obs.Request("GetInputMute", q)["inputMuted"]);
                }
                catch { }
                s += (s.Length > 0 ? "   |   " : "") + n + " = " + v.ToString("0.000") +
                     (muted ? "（源已静音→不进录像）" : (v > 0.001 ? "（有声音，会进录像）" : "（无信号）"));
            }
            Log.Write("音频电平第" + round + "次：" + (s.Length == 0 ? "<没有相关的音频源>" : s));
        }
        int CleanupDuplicateItems(string scene, string source)
        {
            int keep = -1;
            int removed = 0;
            try
            {
                Dictionary<string, object> q = new Dictionary<string, object>();
                q["sceneName"] = scene;
                List<object> items = ObsClient.ToList(obs.Request("GetSceneItemList", q)["sceneItems"]);
                foreach (object o in items)
                {
                    Dictionary<string, object> m = (Dictionary<string, object>)o;
                    if (Convert.ToString(m["sourceName"]) != source) continue;
                    int id = Convert.ToInt32(m["sceneItemId"]);
                    if (keep < 0) { keep = id; continue; }
                    try
                    {
                        Dictionary<string, object> d = new Dictionary<string, object>();
                        d["sceneName"] = scene;
                        d["sceneItemId"] = id;
                        obs.Request("RemoveSceneItem", d);
                        removed++;
                    }
                    catch { }
                }
            }
            catch (Exception ex) { Log.Write("读取场景元素失败：" + ex.Message); }
            if (removed > 0) Log.Write("清理了 " + removed + " 个重复的「" + source + "」元素");
            return keep;
        }
        void FitSourceToCanvas(string scene, int itemId)
        {
            if (itemId < 0) return;
            try
            {
                // 用 OBS 当前真实画布尺寸（不是设置里写的值），让 4:3 画面居中留黑边
                int cw = cfg.CanvasWidth;
                int ch = cfg.CanvasHeight;
                try
                {
                    Dictionary<string, object> v = obs.Request("GetVideoSettings", null);
                    if (v.ContainsKey("baseWidth") && v["baseWidth"] != null) cw = Convert.ToInt32(v["baseWidth"]);
                    if (v.ContainsKey("baseHeight") && v["baseHeight"] != null) ch = Convert.ToInt32(v["baseHeight"]);
                }
                catch { }

                // 单录这里用"正好等于画布"的边界框（等比缩放、居中），并先把源最外圈裁掉几像素：
                // 实测 WGC 抓 640×480 游戏窗口时最左/最右 2~3 像素带一条亮边（整段列平均：最左列 82.6、
                // 第 3 列起 45.6）；用 OBS 的源裁剪等比裁掉这一圈后，缩放铺满画布，画面不变形、也不用重编码。
                int edge = cfg.SingleEdgeInset;
                if (edge < 0) edge = 0;
                if (edge * 2 > 60) edge = 0;      // 防呆
                Dictionary<string, object> t = new Dictionary<string, object>();
                t["positionX"] = 0.0;
                t["positionY"] = 0.0;
                t["boundsType"] = "OBS_BOUNDS_SCALE_INNER";
                t["boundsAlignment"] = 0;
                t["boundsWidth"] = (double)cw;
                t["boundsHeight"] = (double)ch;
                if (edge > 0)
                {
                    t["cropLeft"] = edge;
                    t["cropRight"] = edge;
                    t["cropTop"] = edge;
                    t["cropBottom"] = edge;
                }
                Dictionary<string, object> r = new Dictionary<string, object>();
                r["sceneName"] = scene;
                r["sceneItemId"] = itemId;
                r["sceneItemTransform"] = t;
                obs.Request("SetSceneItemTransform", r);
                Log.Write("采集源已按画布 " + cw + "×" + ch + " 居中适配" +
                          (edge > 0 ? "（并裁掉源最外圈 " + edge + " 像素，去掉边缘那条亮边）" : ""));
            }
            catch (Exception ex) { Log.Write("缩放采集源失败（可忽略）：" + ex.Message); }
        }
        bool BindGameWindow(int pid)
        {
            if (captureSource == null) return false;
            string title = null, cls = null;
            EnumWindows(delegate(IntPtr h, IntPtr l)
            {
                uint p;
                GetWindowThreadProcessId(h, out p);
                if (p == (uint)pid && IsWindowVisible(h))
                {
                    StringBuilder sb = new StringBuilder(512);
                    GetWindowText(h, sb, 512);
                    if (sb.Length > 0)
                    {
                        StringBuilder cb = new StringBuilder(256);
                        GetClassName(h, cb, 256);
                        title = sb.ToString();
                        cls = cb.ToString();
                        return false;
                    }
                }
                return true;
            }, IntPtr.Zero);
            if (title == null) return false;

            string windowValue = MakeWindowString(title, cls);
            return ApplyWindowBinding(windowValue);
        }
        bool ApplyWindowBinding(string windowValue)
        {
            if (captureSource == null) return false;
            try
            {
                Dictionary<string, object> s = new Dictionary<string, object>();
                s["window"] = windowValue;
                s["method"] = 0;
                s["client_area"] = true;
                s["cursor"] = false;
                Dictionary<string, object> r = new Dictionary<string, object>();
                r["inputName"] = captureSource;
                r["inputSettings"] = s;
                obs.Request("SetInputSettings", r);
                Log.Write("已把采集源绑定到窗口：" + windowValue);

                // 音频源（应用程序音频采集）也要绑到同一个窗口，否则抓不到游戏声音
                if (cfg.GameAudioOnly)
                {
                    try
                    {
                        Dictionary<string, object> a = new Dictionary<string, object>();
                        a["window"] = windowValue;
                        Dictionary<string, object> ar = new Dictionary<string, object>();
                        ar["inputName"] = AudioSourceName;
                        ar["inputSettings"] = a;
                        obs.Request("SetInputSettings", ar);
                        Log.Write("已把音频采集源绑定到同一个窗口");
                    }
                    catch (Exception ex) { Log.Write("绑定音频采集源失败：" + ex.Message); }
                }
                return true;
            }
            catch (Exception ex)
            {
                Log.Write("绑定窗口失败：" + ex.Message);
                return false;
            }
        }
        bool BindSingleGameWindow(Process game, out IntPtr hwnd)
        {
            hwnd = WaitForMainWindow(game, 30);
            if (hwnd == IntPtr.Zero) { Log.Write("没等到游戏窗口，仍按进程标题绑定"); return BindGameWindow(game.Id); }
            try { SetWindowText(hwnd, SingleWinTitle); } catch { }
            StringBuilder cb = new StringBuilder(256);
            GetClassName(hwnd, cb, 256);
            string cls = cb.ToString();
            if (cls == "") cls = "th123_110a";
            return ApplyWindowBinding(MakeWindowString(SingleWinTitle, cls));
        }
        void KeepSingleTitleFixed(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return;
            try
            {
                StringBuilder sb = new StringBuilder(512);
                GetWindowText(hwnd, sb, 512);
                if (sb.ToString() != SingleWinTitle)
                {
                    SetWindowText(hwnd, SingleWinTitle);
                    Log.Write("游戏把窗口标题改掉了，已改回「" + SingleWinTitle + "」（否则采集源会抓不到窗口变全黑）");
                }
            }
            catch { }
        }
        static string MethodName(int m)
        {
            if (m == 1) return "BitBlt";
            if (m == 2) return "Windows 图形捕获(WGC)";
            return "自动";
        }
        void ApplyMethod(int m)
        {
            Dictionary<string, object> s = new Dictionary<string, object>();
            s["method"] = m;
            Dictionary<string, object> r = new Dictionary<string, object>();
            r["inputName"] = captureSource;
            r["inputSettings"] = s;
            obs.Request("SetInputSettings", r);
            Thread.Sleep(m == 2 ? 1200 : 800);
        }
        bool EnsureNotBlank()
        {
            if (captureSource == null) return true;
            int[] methods = new int[] { 2, 0, 1 };
            foreach (int m in methods)
            {
                try
                {
                    ApplyMethod(m);
                    byte[] shot = Shot();
                    try { File.WriteAllBytes(LastFramePath, shot); } catch { }
                    if (!IsBlank(shot))
                    {
                        currentMethod = m;
                        Log.Write("采集画面正常（捕获方式 " + MethodName(m) + "，源 " + captureSource + "）；已存一份截图：" + LastFramePath);
                        return true;
                    }
                    Log.Write("采集画面全黑（捕获方式 " + MethodName(m) + "），换一种再试…");
                }
                catch (Exception ex)
                {
                    Log.Write("检测采集画面失败（可忽略）：" + ex.Message);
                    return true;
                }
            }
            Log.Write("采集画面还是黑的，录像可能是黑屏 —— 请在 OBS 里换捕获方式；截图：" + LastFramePath);
            return false;
        }
        // 发完 StartRecord 之后，OBS 到底有没有**真的**在录？
        //
        // 为什么必须查：OBS 处于"录制中/半停止"那种卡住的状态时，StartRecord 会返回成功，
        // 但实际什么都没录 —— 于是白等一整局，最后 StopRecord 报 501、拿不到文件。
        // 并行那条路 2026-10-08 加了这个确认；单录这条路一直到 2026-10-12 才发现没有，
        // 所以偶发"白录一局"（用户报的就是这个）。
        //
        // 复核 → 不在就重发一次 StartRecord → 再复核；两次都不在才返回 false。
        // 调用方自己决定失败之后怎么办（并行会去自动重启 OBS 并重跑这一批，单录直接判这一局失败）。
        bool ConfirmRecordingStarted(string who)
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                Thread.Sleep(1500);
                bool really = false;
                try
                {
                    Dictionary<string, object> rs = obs.Request("GetRecordStatus", null);
                    really = rs.ContainsKey("outputActive") && rs["outputActive"] != null &&
                             Convert.ToBoolean(rs["outputActive"]);
                }
                catch { }
                if (really) return true;
                if (attempt == 0)
                {
                    Log.Write(who + "：StartRecord 之后 OBS 说没在录，重试一次…");
                    try { obs.Request("StartRecord", null); }
                    catch (Exception ex) { Log.Write(who + "：重试 StartRecord 失败：" + ex.Message); }
                }
            }
            return false;
        }

        // ================================================================ ffmpeg 的等待与兜底超时
        //
        // 为什么要有这两样（2026-10-12 补）：三处 ffmpeg 调用以前都是裸 `p.WaitForExit()`，
        // 它有两个毛病，凑在一起就是"点停止没用、只能去任务管理器杀进程"：
        //   · 没有超时 —— ffmpeg 卡住（磁盘满 / 输入的 mp4 坏了 / 编码器挂了）时永远不返回，
        //     整批卡死；切分线程那边的无参 `Join()` 也跟着一起卡。
        //   · 不看 cancel —— 点了「停止」也得等 ffmpeg 自己跑完。
        // 现在的做法：每 200ms 查一次，取消就杀、超时就杀；调用方按返回值决定走哪条路。
        enum FfmpegWait { Exited, TimedOut, Cancelled }

        // 单次 ffmpeg 调用的兜底超时。
        // 实测（2026-10-12 本机，用 45 秒 1440×1080@60 的样本，就是"一局"的规格）：
        //   切一格 crop+scale：nvenc 4.1 s / libx264 veryfast 4.5 s；抽 225 帧 1.3 s；-c copy 裁尾 0.08 s。
        // 最坏情形是单格要切满 MaxMinutes（默认 12 分钟）那么长的源 —— 按 45 秒样本线性外推约 70 秒，
        // 再乘"用户机器可能比我这儿慢几倍"的余量。取 5 分钟 ≈ 实测的 60 倍、外推最坏值的 4 倍：
        // 正常切分绝不可能碰到，它只为"ffmpeg 卡死"兜底。
        // ⚠ 万一将来在很慢的机器上真的误杀了，就是改这一个数（同时会放宽下面那个 Join 的超时）。
        const int FfmpegTimeoutMs = 5 * 60 * 1000;

        FfmpegWait WaitFfmpeg(Process p, string what)
        {
            int waited = 0;
            for (;;)
            {
                bool exited;
                try { exited = p.HasExited; }
                catch { return FfmpegWait.Exited; }        // 进程对象已经没了，当作已结束
                if (exited) return FfmpegWait.Exited;

                if (cancel)
                {
                    try { p.Kill(); } catch { }
                    Log.Write(what + "：已按「停止」终止 ffmpeg");
                    return FfmpegWait.Cancelled;
                }
                if (waited >= FfmpegTimeoutMs)
                {
                    try { p.Kill(); } catch { }
                    Log.Write(what + "：ffmpeg 超过 " + (FfmpegTimeoutMs / 60000) +
                              " 分钟没结束，已终止（磁盘满 / 输入的 mp4 损坏 / 编码器卡住都可能）");
                    return FfmpegWait.TimedOut;
                }
                Thread.Sleep(200);
                waited += 200;
            }
        }

        // 取退出码并释放进程对象。⚠ 必须在 Dispose 之前读 —— Dispose 之后再读 ExitCode 不可靠。
        static int ExitCodeAndDispose(Process p)
        {
            int code = -1;
            try { if (p.HasExited) code = p.ExitCode; } catch { }
            try { p.Dispose(); } catch { }
            return code;
        }

        void StopObsRecordingIfAny()
        {
            try
            {
                Dictionary<string, object> st = obs.Request("GetRecordStatus", null);
                if (st != null && st.ContainsKey("outputActive") && Convert.ToBoolean(st["outputActive"]))
                {
                    obs.Request("StopRecord", null);
                    Log.Write("发现 OBS 里还留着一段没停的录像（上次程序被强制结束留下的），已停止它");
                    Thread.Sleep(2000);   // StopRecord 是异步的，等它收尾完，否则后面改设置会报 500
                }
            }
            catch { }
        }
        void EnsureStandardSourceEnabled()
        {
            try
            {
                int id = CleanupDuplicateItems(RecSceneName, RecSourceName);
                if (id < 0) return;
                Dictionary<string, object> q = new Dictionary<string, object>();
                q["sceneName"] = RecSceneName;
                q["sceneItemId"] = id;
                if (!Convert.ToBoolean(obs.Request("GetSceneItemEnabled", q)["sceneItemEnabled"]))
                {
                    SetSceneItemEnabled(RecSceneName, id, true);
                    Log.Write("采集源上次被隐藏了（并行被强杀留下），已恢复显示");
                }
            }
            catch { }
        }
        void CheckCaptureHealth()
        {
            try
            {
                byte[] shot = Shot();
                if (!IsBlank(shot)) { blackStreak = 0; return; }
                blackStreak++;
                Log.Write("录制中检测到画面变黑（第 " + blackStreak + " 次，当前捕获方式 " + MethodName(currentMethod) +
                          "）—— 通常是游戏窗口被别的窗口挡住了");
                if (blackStreak < 2) return;
                int[] order = new int[] { 2, 0, 1 };
                foreach (int m in order)
                {
                    if (m == currentMethod) continue;
                    ApplyMethod(m);
                    if (!IsBlank(Shot()))
                    {
                        currentMethod = m;
                        blackStreak = 0;
                        Log.Write("画面已恢复（捕获方式切换为 " + MethodName(m) + "）");
                        return;
                    }
                }
                Log.Write("画面仍然是黑的：这段录像可能是黑屏。录制期间请不要让其他窗口挡住游戏窗口。");
                blackStreak = 0;
            }
            catch { }
        }
        byte[] Shot()
        {
            if (captureScene != null && captureScene != captureSource)
            {
                try { return ShotOf(captureScene); }
                catch { }
            }
            return ShotOf(captureSource);
        }
        byte[] ShotOf(string sourceName)
        {
            Dictionary<string, object> q = new Dictionary<string, object>();
            q["sourceName"] = sourceName;
            q["imageFormat"] = "jpg";
            q["imageWidth"] = 320;
            q["imageHeight"] = 240;
            q["imageCompressionQuality"] = 60;
            string data = Convert.ToString(obs.Request("GetSourceScreenshot", q)["imageData"]);
            int k = data.IndexOf("base64,");
            if (k >= 0) data = data.Substring(k + 7);
            return Convert.FromBase64String(data);
        }
        bool IsBlank(byte[] jpg)
        {
            try
            {
                using (MemoryStream ms = new MemoryStream(jpg))
                using (Bitmap bmp = new Bitmap(ms))
                {
                    double max = 0;
                    for (int y = 0; y < bmp.Height; y += 8)
                        for (int x = 0; x < bmp.Width; x += 8)
                        {
                            Color c = bmp.GetPixel(x, y);
                            double l = (c.R + c.G + c.B) / 3.0;
                            if (l > max) max = l;
                        }
                    return max < 20;
                }
            }
            catch { return false; }
        }
        void ProcessJob(Job job)
        {
            job.Started = DateTime.Now;
            try
            {
                if (cfg.SkipExisting && !forceRecord && !job.Force)
                {
                    string a = Path.ChangeExtension(job.Rep, ".mp4");
                    string b = Path.ChangeExtension(job.Rep, ".mkv");
                    if (File.Exists(a) || File.Exists(b))
                    {
                        Ui.SetJob(job, "跳过", "已有录像文件（可在设置里关掉“跳过已录制”）");
                        Log.Write("跳过（已有录像）：" + Path.GetFileName(job.Rep));
                        return;
                    }
                }
                if ((forceRecord || job.Force) && cfg.SkipExisting)
                    Log.Write("你明确选了重录它 → 即使已有录像也重录：" + Path.GetFileName(job.Rep));
                Ui.SetJob(job, "准备", "");
                try { ApplyMicMode(true); } catch { }   // 每个文件开录前都按当前模式再确认一次麦克风状态
                KillGame();
                try
                {
                    Dictionary<string, object> r = new Dictionary<string, object>();
                    r["recordDirectory"] = Path.GetDirectoryName(job.Rep);
                    obs.Request("SetRecordDirectory", r);
                    Log.Write("OBS 录像目录已设为：" + Path.GetDirectoryName(job.Rep));
                }
                catch (Exception ex) { Log.Write("设置 OBS 录像目录失败（会录到 OBS 默认目录再移动）：" + ex.Message); }



                Ui.SetJob(job, "启动游戏", "");
                ProcessStartInfo psi = new ProcessStartInfo(Path.Combine(cfg.GameDir, "th123.exe"), "\"" + job.Rep + "\"");
                psi.WorkingDirectory = cfg.GameDir;
                Process game = Process.Start(psi);

                bool shown = false;
                DateTime wDeadline = DateTime.Now.AddSeconds(60);
                while (DateTime.Now < wDeadline && !cancel)
                {
                    Thread.Sleep(500);
                    try
                    {
                        game.Refresh();
                        if (game.HasExited) break;
                        if (game.MainWindowHandle != IntPtr.Zero) { shown = true; break; }
                    }
                    catch { break; }
                }
                if (!shown && !cancel) Log.Write("警告：没等到游戏窗口，仍继续尝试");

                try { if (game.HasExited) { Ui.SetJob(job, "失败", "游戏启动后立刻退出"); return; } }
                catch { }

                // 兜底：单录前确认画面采集源是显示的（并行录制会临时隐藏它；上次被强杀就会一直隐藏 → 全黑）
                EnsureStandardSourceEnabled();

                IntPtr singleWin = IntPtr.Zero;
                if (captureSource != null && captureSourceIsOurs) BindSingleGameWindow(game, out singleWin);
                // 清晰度：把 OBS 画布/输出设成【游戏窗口的原生尺寸】（640×480），让 OBS 完全不缩放，
                // 录完再用 ffmpeg 以 neighbor（点采样）放大到目标尺寸 —— 否则 OBS 用双线性把 640×480 拉到
                // 1440×1080，整体发糊（用户实测：和别人的录像一比"清晰程度差很多"）。
                for (int i = 0; i < cfg.PreRollSeconds && !cancel; i++) Thread.Sleep(1000);
                // 说明：单录这条路径开头的黑屏只有 0.3 秒（实测样本），不需要额外等画面。
                // 曾经在这里加过"等游戏画出画面再开录"，结果反而卡死：这台机器上录制开始前
                // GetSourceScreenshot 一直返回全黑（节目输出还没切到录制场景），等不到就白等 45 秒。
                // 5~8 秒的黑头是并行路径特有的（开录后才重启游戏实例），那边已在切分时按各格
                // "画面出现的时刻" 用 -ss 精确剪掉。
                if (captureSource != null) EnsureNotBlank();

                obs.Request("StartRecord", null);
                // 单录这条路以前发完 StartRecord 就直接往下走，不确认 OBS 真的在录 ——
                // OBS 卡住时就是白录一整局（等满 MaxMinutes，最后拿不到文件）。
                // 2026-10-12 补上和并行同一道确认。
                if (!ConfirmRecordingStarted("单录"))
                {
                    Log.Write("单录：❌ OBS 没真正开录（outputActive=false）。可能：有残留录像 / OBS 卡住 / 录制目录不可写");
                    try { if (obs.IsConnected) obs.Request("StopRecord", null); } catch { }   // 别把半开的状态留给下一个
                    KillGame();
                    Ui.SetJob(job, "失败", "OBS 没真正开录");
                    return;
                }
                Ui.SetJob(job, "录制中", "0:00");
                jobMaxGameLevel = 0;
                try { ApplyGameAudioSetting(); } catch { }   // 再确认一次"录不录打击音效"
                Log.Write("开始录像：" + Path.GetFileName(job.Rep));

                bool timeout = false;
                DateTime t0 = DateTime.Now;
                int ticks = 0;
                blackStreak = 0;
                double lastVisibleSec = 0;   // 最后一次"画面还在"的时刻（相对开录），用来剪掉结尾的黑
                while (!cancel)
                {
                    Thread.Sleep(500);
                    bool exited = false;
                    try { exited = game.HasExited; } catch { exited = true; }
                    if (exited) break;
                    if ((DateTime.Now - t0).TotalMinutes > cfg.MaxMinutes) { timeout = true; break; }
                    ticks++;
                    if (captureSource != null && ticks % 10 == 0) CheckCaptureHealth();
                    // 每秒记一次"画面还在不在"：游戏退出时会先黑一下，收尾时按这个把结尾的黑剪掉
                    // （ticks 是 500ms 一格，所以 % 2 = 每秒一次）。
                    // ⚠ 别放宽到 2 秒：lastVisibleSec 早 1 秒，成片结尾就多留 1 秒黑；
                    //   而省下的只是每秒一次 320×240 截图，收益小得多。
                    if (captureSource != null && ticks % 2 == 0)
                    {
                        try { if (!IsBlank(ShotOf(captureSource))) lastVisibleSec = (DateTime.Now - t0).TotalSeconds; }
                        catch { }
                    }
                    if (ticks % 4 == 0) KeepGameWindowCapturable(game);   // 防止被最小化导致录不到画面
                    if (ticks % 4 == 0) KeepSingleTitleFixed(singleWin);  // 标题被游戏改回去就改回来（否则采集源断）
                    if (ticks % 60 == 0) LogAudioLevels(ticks / 60);
                    if (cfg.GameAudioOnly)
                    {
                        double lv = obs.GetLevel(AudioSourceName);
                        if (lv > jobMaxGameLevel) jobMaxGameLevel = lv;
                    }
                    TimeSpan el = DateTime.Now - t0;
                    Ui.SetJob(job, "录制中", string.Format("{0}:{1:00}", (int)el.TotalMinutes, el.Seconds));
                }

                if (timeout)
                {
                    Log.Write("超过 " + cfg.MaxMinutes + " 分钟上限，强制结束");
                    KillGame();
                }
                if (gameMutedByUs)
                {
                    string m;
                    if (GameAudio.SetSystemMute(false, out m)) { Log.Write("已恢复系统声音（" + m + "）"); MarkSystemMuted(false); }
                    gameMutedByUs = false;
                }
                double recTotalSec = 0;
                // 画面已经黑了就别再等收尾秒数了，否则结尾又多一段纯黑
                bool picGone = false;
                try { picGone = captureSource != null && IsBlank(ShotOf(captureSource)); } catch { }
                if (picGone && cfg.PostRollSeconds > 0)
                    Log.Write("游戏已退出，不再等 " + cfg.PostRollSeconds + " 秒收尾");
                for (int i = 0; i < cfg.PostRollSeconds && !picGone; i++) Thread.Sleep(1000);
                recTotalSec = (DateTime.Now - t0).TotalSeconds;   // 必须在收尾等待之后算，否则漏掉这段黑

                string recPath = null;
                try
                {
                    Dictionary<string, object> resp = obs.Request("StopRecord", null);
                    if (resp.ContainsKey("outputPath")) recPath = Convert.ToString(resp["outputPath"]);
                }
                catch (Exception ex) { Log.Write("停止录像出错：" + ex.Message); }

                if (string.IsNullOrEmpty(recPath) || !File.Exists(recPath))
                {
                    try
                    {
                        string dir = Convert.ToString(obs.Request("GetRecordDirectory", null)["recordDirectory"]);
                        if (Directory.Exists(dir))
                        {
                            DateTime since = DateTime.Now.AddMinutes(-3);
                            foreach (string f in Directory.GetFiles(dir))
                            {
                                string ext = Path.GetExtension(f).ToLower();
                                if ((ext == ".mp4" || ext == ".mkv" || ext == ".flv" || ext == ".mov") &&
                                    File.GetLastWriteTime(f) > since)
                                {
                                    recPath = f;
                                }
                            }
                        }
                    }
                    catch { }
                }

                if (string.IsNullOrEmpty(recPath) || !File.Exists(recPath))
                {
                    Ui.SetJob(job, "失败", "拿不到录像文件");
                    Log.Write("失败：拿不到录像文件 —— " + Path.GetFileName(job.Rep));
                    return;
                }

                string target = OutputPathFor(job, Path.GetExtension(recPath));
                if (!string.Equals(Path.GetFullPath(recPath), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
                {
                    for (int i = 0; i < 10; i++)
                    {
                        try
                        {
                            if (File.Exists(target)) File.Delete(target);
                            File.Move(recPath, target);
                            break;
                        }
                        catch { Thread.Sleep(500); }
                    }
                }
                job.Output = target;
                TrimTrailingBlack(job, recTotalSec, lastVisibleSec);   // 剪掉结尾那段黑屏
                if (File.Exists(target)) job.Size = new FileInfo(target).Length;
                job.VideoSeconds = Mp4Duration(target);                // 列表「用时」显示视频真实时长
                job.Ended = DateTime.Now;

                string note = "";
                if (target.ToLower().EndsWith(".mp4") && !HasMoov(target)) note = "缺少 moov 索引，可能不可播";
                else if (!target.ToLower().EndsWith(".mp4")) note = "格式 " + Path.GetExtension(target) + "，需要转封装";

                // 音频自检：既看电平，也看源有没有被静音（OBS 的电平表在源被静音时照样跳）
                if (!cfg.RecordGameAudio)
                    Log.Write("音频自检：你关掉了「录打击音效」，这段视频按预期是无声的");
                else if (cfg.GameAudioOnly)
                {
                    bool srcMuted = false;
                    try
                    {
                        Dictionary<string, object> q = new Dictionary<string, object>();
                        q["inputName"] = AudioSourceName;
                        srcMuted = Convert.ToBoolean(obs.Request("GetInputMute", q)["inputMuted"]);
                    }
                    catch { }

                    if (srcMuted)
                    {
                        Log.Write("⚠ 音频自检：「" + AudioSourceName + "」源处于静音状态，这段录像里没有游戏声音");
                        if (note == "") note = "音频源被静音，录像无声";
                    }
                    else if (!obs.MetersFresh)
                        Log.Write("音频自检：没有收到电平数据，无法判断（不影响录制）");
                    else if (jobMaxGameLevel < 0.002)
                    {
                        Log.Write("⚠ 音频自检：整段几乎没声音（最大电平 " + jobMaxGameLevel.ToString("0.000")
                                  + "），检查 OBS 里「" + AudioSourceName + "」有没有绑上游戏窗口");
                        if (note == "") note = "录成了静音？(音频自检)";
                    }
                    else
                        Log.Write("音频自检：游戏声音正常（最大电平 " + jobMaxGameLevel.ToString("0.000") + "）");
                }

                Ui.SetJob(job, note == "" ? "完成" : "注意", note);
                Log.Write((note == "" ? "完成：" : "完成（" + note + "）：") + Path.GetFileName(target) +
                          "  " + (job.Size / 1048576.0).ToString("0.0") + " MB  视频时长 " + FmtDur(job.VideoSeconds) +
                          "（任务耗时 " + FmtDur((job.Ended - job.Started).TotalSeconds) + "）");
                if (note.StartsWith("格式")) Log.Write("转封装：ffmpeg -i \"" + target + "\" -c copy \"" + Path.ChangeExtension(target, ".mp4") + "\"");

                // 需求：录完问用户要把视频放哪
                string saveNote = "";
                if (!queueSaveDecided)
                {
                    string choice = AskSaveLocationDialog(job);
                    if (!string.IsNullOrEmpty(choice) && choice != "keep")
                    {
                        string moved = MoveOutputTo(job, choice);
                        if (moved != null) { job.Output = moved; saveNote = "已放到 " + Path.GetDirectoryName(moved); }
                    }
                }
                else if (!string.IsNullOrEmpty(queueSaveFolder))
                {
                    string moved = MoveOutputTo(job, queueSaveFolder);
                    if (moved != null) { job.Output = moved; saveNote = "已放到 " + queueSaveFolder; }
                }
                if (saveNote != "")
                {
                    if (File.Exists(job.Output)) job.Size = new FileInfo(job.Output).Length;
                    Log.Write("视频位置：" + job.Output);
                    Ui.SetJob(job, note == "" ? "完成" : "注意", saveNote);
                }
            }
            catch (Exception ex)
            {
                Ui.SetJob(job, "失败", ex.Message);
                Log.Write("任务失败：" + Path.GetFileName(job.Rep) + " —— " + ex.Message);
                try { if (obs.IsConnected) obs.Request("StopRecord", null); } catch { }
                KillGame();
            }
        }
        void TrimTrailingBlack(Job job, double totalSec, double lastVisibleSec)
        {
            string tmp = null;
            try
            {
                string path = job.Output;
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
                if (!path.ToLower().EndsWith(".mp4")) return;
                if (lastVisibleSec <= 1 || totalSec <= lastVisibleSec) return;
                double keep = lastVisibleSec + 0.25;
                double cut = totalSec - keep;
                if (cut < 0.4) return;                     // 黑的不多，不值得动文件
                string ff = FindFfmpeg();
                if (ff == null) return;
                tmp = path + ".trim.mp4";
                if (File.Exists(tmp)) File.Delete(tmp);
                string args = "-hide_banner -y -i \"" + path + "\" -c copy -t " +
                              keep.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture) +
                              " -movflags +faststart \"" + tmp + "\"";
                ProcessStartInfo psi = new ProcessStartInfo(ff, args);
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.WorkingDirectory = Path.GetDirectoryName(ff);
                Process p = Process.Start(psi);
                FfmpegWait w = WaitFfmpeg(p, "修剪结尾黑屏");
                int code = ExitCodeAndDispose(p);
                if (w != FfmpegWait.Exited || code != 0 || !File.Exists(tmp) || new FileInfo(tmp).Length < 4096 || !HasMoov(tmp))
                {
                    Log.Write("结尾黑屏修剪没成功（ffmpeg 退出码 " + code
                              + (w == FfmpegWait.TimedOut ? "，超时" : w == FfmpegWait.Cancelled ? "，已取消" : "")
                              + "），保留原文件");
                    try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                    return;
                }
                try { File.Replace(tmp, path, null); }
                catch
                {
                    File.Delete(path);
                    File.Move(tmp, path);
                }
                Log.Write("已剪掉结尾 " + FmtDur(cut) + " 的黑屏（" + FmtDur(totalSec) + " → " + FmtDur(keep) + "）");
            }
            catch (Exception ex)
            {
                Log.Write("结尾黑屏修剪出错（已忽略）：" + ex.Message);
                try { if (tmp != null && File.Exists(tmp)) File.Delete(tmp); } catch { }
            }
        }
        string MoveOutputTo(Job job, string folder)
        {
            try
            {
                if (string.IsNullOrEmpty(job.Output) || !File.Exists(job.Output)) return null;
                if (!Directory.Exists(folder))
                {
                    Log.Write("目标文件夹不存在：" + folder);
                    return null;
                }
                string target = Path.Combine(folder, Path.GetFileName(job.Output));
                if (string.Equals(Path.GetFullPath(target), Path.GetFullPath(job.Output), StringComparison.OrdinalIgnoreCase))
                    return job.Output;
                string baseName = Path.GetFileNameWithoutExtension(job.Output);
                string ext = Path.GetExtension(job.Output);
                for (int i = 2; File.Exists(target) && i < 500; i++)
                    target = Path.Combine(folder, baseName + " (" + i + ")" + ext);
                File.Move(job.Output, target);
                return target;
            }
            catch (Exception ex)
            {
                Log.Write("移动视频失败：" + ex.Message);
                return null;
            }
        }
        static bool HasMoov(string path)
        {
            try
            {
                using (FileStream fs = File.OpenRead(path))
                {
                    int n = (int)Math.Min(1048576, fs.Length);
                    byte[] head = new byte[n];
                    fs.Read(head, 0, n);
                    fs.Seek(Math.Max(0, fs.Length - 1048576), SeekOrigin.Begin);
                    int n2 = (int)Math.Min(1048576, fs.Length);
                    byte[] tail = new byte[n2];
                    fs.Read(tail, 0, n2);
                    string s = Encoding.ASCII.GetString(head) + Encoding.ASCII.GetString(tail);
                    return s.Contains("moov");
                }
            }
            catch { return false; }
        }
        static void KillGame()
        {
            try
            {
                foreach (Process p in Process.GetProcessesByName("th123"))
                {
                    try { p.Kill(); } catch { }
                }
            }
            catch { }
        }
        void RunQueue()
        {
            try
            {
                Ui.Status("准备 OBS…");
                if (!EnsureObs())
                {
                    MarkPendingFailed("无法使用 OBS");
                    return;
                }
                if (parallelRequested)
                {
                    parallelRequested = false;
                    RunParallel();
                    return;
                }
                if (cfg.AutoPrepareObs) PrepareObs(false);
                if (cfg.AutoPrepareObs && cfg.UseDedicatedScene) SwitchScene(true);
                try
                {
                    if (originalRecordDir == null) CaptureOriginalRecordDir();
                }
                catch { }
                ApplyMicMode(true);
                ApplyAudioRouting(true);

                while (!cancel)
                {
                    Job job = null;
                    lock (jobsLock)
                    {
                        foreach (Job j in jobs) if (j.Status == "等待") { job = j; break; }
                    }
                    if (job == null) break;
                    ProcessJob(job);
                }
            }
            catch (Exception ex)
            {
                Log.Write("队列异常终止：" + ex.Message);
            }
            finally
            {
                // 录完恢复系统声音：你在录制时按过【静音】的话，这里自动帮你打开
                if (weMutedSystem)
                {
                    try
                    {
                        string m;
                        if (GameAudio.SetSystemMute(false, out m))
                        {
                            weMutedSystem = false;
                            Log.Write("录制结束：已自动恢复系统声音（录制时你按过静音）");
                        }
                    }
                    catch { }
                }
                try { ApplyMicMode(false); } catch { }
                try { ApplyAudioRouting(false); } catch { }
                try { if (cfg.AutoPrepareObs && cfg.UseDedicatedScene && obs.IsConnected) SwitchScene(false); } catch { }
                try { RestoreRecordDirectory(); } catch { }
                try { obs.Dispose(); } catch { }
                Ui.Post(delegate
                {
                    btnStart.Enabled = true;
                    btnStop.Enabled = false;
                });
                if (cancel) Ui.Status("已停止");
                else Ui.Status("队列结束");
                Log.Write("==== 队列结束 ====");
                // 默认不关 OBS；只有勾了界面上的「录完关闭 OBS」才会关
                if (!cancel && cfg.CloseObsAfter)
                {
                    try
                    {
                        foreach (Process p in Process.GetProcessesByName("obs64"))
                            p.CloseMainWindow();
                        Log.Write("已关闭 OBS（你勾选了「录完关闭 OBS」）");
                    }
                    catch { }
                }
            }
        }
        void StartQueue()
        {
            if (worker != null && worker.IsAlive) { Log.Write("队列已在运行，新加入的会排在后面"); return; }
            cancel = false;
            worker = new Thread(RunQueue);
            worker.IsBackground = true;
            worker.Start();
        }
        void MarkPendingFailed(string reason)
        {
            Ui.Post(delegate
            {
                lock (jobsLock)
                {
                    foreach (Job j in jobs)
                        if (j.Status == "等待") Ui.SetJob(j, "失败", reason);
                }
            });
        }
        void AddPaths(string[] paths)
        {
            // 先把参数分成"文件"和"文件夹"两类。
            // 文件（拖进来的 .rep）同步加 —— 那本来就快，拖文件时用户希望立刻看到。
            // 文件夹要 Directory.GetFiles(..., AllDirectories) 整棵递归，拖进一个装满 rep 的大文件夹
            // （甚至整合包根目录）时会把 UI 线程占住、界面冻住 —— 2026-10-12 挪到后台线程。
            List<string> direct = new List<string>();
            List<string> dirs = new List<string>();
            foreach (string p in paths)
            {
                try
                {
                    if (Directory.Exists(p)) dirs.Add(p);
                    else if (File.Exists(p) && p.ToLower().EndsWith(".rep")) direct.Add(p);
                }
                catch (Exception ex) { Log.Write("读取 " + p + " 失败：" + ex.Message); }
            }

            if (dirs.Count == 0) { EnqueueReps(direct); return; }

            Ui.Status("正在扫描文件夹…");
            Thread t = new Thread(delegate()
            {
                List<string> reps = new List<string>(direct);
                foreach (string p in dirs)
                {
                    try { reps.AddRange(Directory.GetFiles(p, "*.rep", SearchOption.AllDirectories)); }
                    catch (Exception ex) { Log.Write("读取 " + p + " 失败：" + ex.Message); }
                }
                Ui.Post(delegate { EnqueueReps(reps); });   // 加条目要回 UI 线程（它碰控件）
            });
            t.IsBackground = true;
            t.Name = "扫描rep";
            t.Start();
        }

        // 把（可能刚扫完的）一批 rep 加进队列 —— 原来这段是 AddPaths 的后半截，原样搬过来。
        // ⚠ 只在 UI 线程上调（它碰控件）。
        void EnqueueReps(List<string> reps)
        {
            reps.Sort(StringComparer.OrdinalIgnoreCase);
            int added = 0;
            List<Job> already = new List<Job>();
            lock (jobsLock)
            {
                // 去重：以前每加一个 rep 都对整个 jobs 线性扫一遍（O(n²)，拖几百个就明显）。
                // 这里按当前 jobs 建一次 HashSet（O(n)）。
                // ⚠ 刻意**不**另外维护一份"已入队路径"的索引：那种索引要和增/删/清空三处同步维护，
                //   漏一处就会漂移（比 O(n) 贵得多的 bug）。每次重建它反而是最不容易错的写法。
                HashSet<string> known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (Job j in jobs) known.Add(j.Rep);
                foreach (string r in reps)
                {
                    if (!known.Add(r)) continue;     // Add 返回 false = 队列里已经有了
                    Job job = new Job(r);
                    // 已经有录好的视频 → 先标成「已录制」，然后统一问一次用户"要不要重录"
                    string have = FindExistingOutput(job);
                    if (have != null)
                    {
                        job.Output = have;
                        try { job.Size = new FileInfo(have).Length; } catch { }
                        job.Status = "已录制";
                        job.Detail = "已有视频，点开始会跳过";
                        already.Add(job);
                    }
                    jobs.Add(job);
                    Ui.AddRow(job);
                    added++;
                }
            }
            Ui.SyncScroll();     // 条目数变了，自绘滚动条要跟着更新
            if (added > 0) Log.Write("加入队列 " + added + " 个 rep");
            else Log.Write("没有可加入的 rep（重复或不是 .rep）");
            // 需求：检测到"已经录制过"的文件 → 问一次是否重录
            if (already.Count > 0) AskReRecord(already);
            // 少一步操作：数量自动跟着"放进来几个"走，并把这一批要录的高亮出来
            AfterQueueChanged(added);
        }
        void AfterQueueChanged(int added)
        {
            try
            {
                if (added > 0)
                {
                    int want = Math.Min(6, added);
                    if (cfg.ParallelCount != want)
                    {
                        cfg.ParallelCount = want;
                        cfg.Save();
                        if (Ui.ParallelCount != want) Ui.SetParallel(want);
                        Log.Write("放进来 " + added + " 个 rep → 同时录制数量设为 " + want + (added > 6 ? "（上限 6）" : ""));
                    }
                }
                HighlightBatchRows();
                try { Ui.Status("就绪 · 本批 " + Math.Max(1, Math.Min(6, cfg.ParallelCount)) + " 个（列表里浅绿高亮的就是）"); } catch { }
            }
            catch { }
        }
        void AskReRecord(List<Job> already)
        {
            try
            {
                string sample = already.Count == 1
                    ? Path.GetFileName(already[0].Rep)
                    : (Path.GetFileName(already[0].Rep) + " 等 " + already.Count + " 个");
                string msg = "检测到 " + already.Count + " 个 rep 已经录制过了（旁边/上次的文件夹里已有同名视频）：\r\n\r\n" +
                             sample + "\r\n\r\n" +
                             "怎么办？";
                Log.Write("检测到 " + already.Count + " 个 rep 已经录制过，正在询问怎么处理……");
                int pick = Ui.ShowReRecordDialog(msg);
                if (pick == 0)
                {
                    foreach (Job j in already)
                    {
                        j.Force = true;
                        j.KeepOld = false;
                        Ui.SetJob(j, "等待", "你选了重录（会覆盖原视频）");
                    }
                    Log.Write("你选择重录：" + already.Count + " 个（状态已改成「等待」，会覆盖原来的视频）");
                }
                else if (pick == 1)
                {
                    foreach (Job j in already)
                    {
                        j.Force = true;        // 已有录像也要录
                        j.KeepOld = true;      // 但录出来不许盖掉原来那份
                        Ui.SetJob(j, "等待", "你选了再录一次（旧的保留，新的另存）");
                    }
                    Log.Write("你选择再录一次：" + already.Count + " 个（旧的保留，新的会另存成「原名 (2)」）");
                }
                else
                {
                    // pick == 2 是「跳过」；pick == -1（直接把窗口关掉）也走这里 ——
                    // 关窗口等于"什么都没选"，最保守的处理就是不录，绝不能把原来的视频盖掉。
                    foreach (Job j in already) Ui.SetJob(j, "已录制", "已有视频（你选择跳过）");
                    Log.Write("你选择跳过" + (pick < 0 ? "（窗口被直接关掉）" : "") + "：" + already.Count + " 个已录制过的 rep");
                }
            }
            catch (Exception ex) { Log.Write("询问重录失败：" + ex.Message); }
        }
        void SyncParallelCountToSelection()
        {
            try
            {
                int n = Ui.SelectedJobCount;
                bool fromPin = false;
                if (n <= 0 && pinnedBatch != null && pinnedBatch.Count > 0) { n = pinnedBatch.Count; fromPin = true; }
                if (n <= 0) { if (lblParHint != null) lblParHint.Text = ""; return; }
                int want = Math.Min(6, n);
                if (Ui.ParallelCount != want)
                    Ui.SetParallel(want);     // 走 SelectedIndexChanged 里的保存/日志
                if (lblParHint != null)
                    lblParHint.Text = n > 6
                        ? "（你" + (fromPin ? "标记" : "选中") + "了 " + n + " 个，上限 6 → 会分 " + ((n + 5) / 6) + " 批）"
                        : "（= 你" + (fromPin ? "标记" : "选中") + "的 " + n + " 个）";
                HighlightBatchRows();
            }
            catch { }
        }
        void RestoreSoundIfLeftMuted()
        {
            try
            {
                if (!File.Exists(MuteFlagPath)) return;
                bool muted;
                string msg;
                if (!GameAudio.GetSystemMute(out muted, out msg)) return;
                if (muted)
                {
                    string m2;
                    if (GameAudio.SetSystemMute(false, out m2))
                        Log.Write("上次静音后没恢复（多半被强杀），已自动把声音打开");
                    else
                        Log.Write("想恢复上次的静音但失败了：" + m2);
                }
                File.Delete(MuteFlagPath);
                RefreshSoundButtons();
            }
            catch { }
        }
        void RefreshSoundButtons()
        {
            if (btnSound == null) return;
            bool muted;
            string msg;
            if (!GameAudio.GetSystemMute(out muted, out msg))
            {
                btnSound.Text = "🔊 声音：未知";
                btnSound.Width = 148;
                btnSound.BackColor = HudOff;
                btnSound.ForeColor = HudDim;
                btnSound.FlatAppearance.BorderColor = HudLine;
                return;
            }
            if (muted)
            {
                btnSound.Text = "🔊 声音：静音 ✕（录完自动恢复）";
                btnSound.Width = 260;
                Ui.ToggleLook(btnSound, false);
            }
            else
            {
                btnSound.Text = "🔊 声音：开 ✓";
                btnSound.Width = 148;
                Ui.ToggleLook(btnSound, true);
            }
        }
        void ApplyGameAudioSetting()
        {
            if (obs == null || !obs.IsConnected) return;
            try
            {
                Dictionary<string, object> q = new Dictionary<string, object>();
                q["inputName"] = AudioSourceName;
                bool muted = false;
                try { muted = Convert.ToBoolean(obs.Request("GetInputMute", q)["inputMuted"]); }
                catch { }
                bool want = !cfg.RecordGameAudio;
                if (muted != want)
                {
                    Dictionary<string, object> s = new Dictionary<string, object>();
                    s["inputName"] = AudioSourceName;
                    s["inputMuted"] = want;
                    obs.Request("SetInputMute", s);
                }
                Log.Write("游戏声音录制：" + (cfg.RecordGameAudio ? "开（会录进视频）" : "关（视频无声）") +
                          "；OBS 音频源「" + AudioSourceName + "」现在" + (want ? "已静音" : "未静音"));
                ApplyRecordVolume(AudioSourceName);
            }
            catch (Exception ex) { Log.Write("设置游戏声音录制失败：" + ex.Message); }
        }
        void ApplyRecordVolume(string inputName)
        {
            if (obs == null || !obs.IsConnected) return;
            try
            {
                double mul = cfg.RecordVolume;
                if (mul <= 0) mul = 1.0;
                if (mul > 20) mul = 20;
                if (Math.Abs(mul - 1.0) < 0.001) return;   // 1.0 就是原样，不用动
                Dictionary<string, object> s = new Dictionary<string, object>();
                s["inputName"] = inputName;
                s["inputVolumeMul"] = mul;
                obs.Request("SetInputVolume", s);
                Log.Write("录像音量：" + inputName + " 设为 " + mul.ToString("0.##") + " 倍");
            }
            catch (Exception ex) { Log.Write("设置录像音量失败（忽略）：" + ex.Message); }
        }
        void RefreshGameAudioButton()
        {
            if (btnGameAudio == null) return;
            if (cfg.RecordGameAudio)
            {
                btnGameAudio.Text = "🔊 录打击音效：开 ✓";
                Ui.ToggleLook(btnGameAudio, true);
            }
            else
            {
                btnGameAudio.Text = "🔊 录打击音效：关 ✕";
                Ui.ToggleLook(btnGameAudio, false);
            }
            // 「录打击音效」关掉 = 录出来完全无声 → 顺手把「录BGM」锁成关（连游戏里也不放 BGM）
            if (btnBgm != null)
            {
                btnBgm.Enabled = cfg.RecordGameAudio;
                if (!cfg.RecordGameAudio)
                {
                    btnBgm.Text = "录BGM：关 ✕（已锁）";
                    btnBgm.BackColor = HudOff;
                    btnBgm.ForeColor = HudDim;
                }
                else RefreshBgmButton();
            }
        }
        List<string> BgmIniPaths()
        {
            List<string> list = new List<string>();
            if (string.IsNullOrEmpty(cfg.GameDir)) return list;
            foreach (string sub in BgmIniSubPaths)
            {
                string p = Path.Combine(cfg.GameDir, sub);
                if (File.Exists(p)) list.Add(p);
            }
            return list;
        }
        void ApplyBgmSetting(bool verbose)
        {
            int want = cfg.RecordBgm ? 0 : 1;
            List<string> paths = BgmIniPaths();
            if (paths.Count == 0)
            {
                if (verbose) Log.Write("找不到 ReplayDnD.ini，BGM 开关没生效（检查「设置…」里的游戏目录）");
                return;
            }
            foreach (string p in paths)
            {
                try
                {
                    string t = File.ReadAllText(p);
                    string nt;
                    if (System.Text.RegularExpressions.Regex.IsMatch(t, @"(?im)^\s*MuteMusic\s*=\s*\d+\s*$"))
                        nt = System.Text.RegularExpressions.Regex.Replace(t, @"(?im)^\s*MuteMusic\s*=\s*\d+\s*$", "MuteMusic=" + want);
                    else
                        nt = t.TrimEnd() + Environment.NewLine + "MuteMusic=" + want + Environment.NewLine;
                    if (nt != t) File.WriteAllText(p, nt);
                    if (verbose)
                        Log.Write("BGM：" + Path.GetFileName(Path.GetDirectoryName(p)) + @" → MuteMusic=" + want +
                                  (want == 0 ? "（播放 rep 时会放 BGM）" : "（不放 BGM，只有音效）"));
                }
                catch (Exception ex) { Log.Write("写 ReplayDnD.ini 失败（" + p + "）：" + ex.Message); }
            }
        }
        void RefreshBgmButton()
        {
            if (btnBgm == null) return;
            if (cfg.RecordBgm)
            {
                btnBgm.Text = "录BGM：开 ✓";
                Ui.ToggleLook(btnBgm, true);
            }
            else
            {
                btnBgm.Text = "录BGM：关 ✕";
                Ui.ToggleLook(btnBgm, false);
            }
        }
        void SyncBgmFromIni()
        {
            bool? muted = ReadBgmMuted();
            if (muted.HasValue)
            {
                cfg.RecordBgm = !muted.Value;
                // 「录打击音效」关着的话，BGM 一律锁成关（连游戏里也不放），保证"完全无声视频"
                if (!cfg.RecordGameAudio && cfg.RecordBgm)
                {
                    cfg.RecordBgm = false;
                    ApplyBgmSetting(true);
                    Log.Write("录打击音效关了 → BGM 一并关掉（MuteMusic=1）");
                }
                RefreshBgmButton();
                Log.Write("BGM：" + (cfg.RecordBgm ? "开（MuteMusic=0，带 BGM）" : "关（MuteMusic=1，只有音效）"));
            }
            else
            {
                RefreshBgmButton();
                Log.Write("找不到 ReplayDnD.ini，BGM 开关读不到（检查「设置…」里的游戏目录）");
            }
        }
        void SetSystemMuteUi(bool mute)
        {
            string msg;
            bool ok = GameAudio.SetSystemMute(mute, out msg);
            Log.Write((mute ? "【静音】" : "【恢复声音】") + "：" + msg + (ok ? "" : "  失败"));
            if (ok)
            {
                weMutedSystem = mute;   // 记住这个静音是我们按的，录制结束自动恢复
                MarkSystemMuted(mute);
                if (mute)
                    Log.Write("现在你自己听不到任何系统声音；录像里仍然有游戏原声。录制结束后会【自动恢复】声音");
                else
                    Log.Write("系统声音已恢复");
            }
            RefreshSoundButtons();
        }
        static void MarkSystemMuted(bool muted)
        {
            try
            {
                if (muted) File.WriteAllText(MuteFlagPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), Encoding.UTF8);
                else if (File.Exists(MuteFlagPath)) File.Delete(MuteFlagPath);
            }
            catch { }
        }
        void OnSoundToggle(object sender, EventArgs e)
        {
            bool muted;
            string msg;
            if (!GameAudio.GetSystemMute(out muted, out msg))
            {
                Log.Write("读取系统静音状态失败：" + msg);
                return;
            }
            SetSystemMuteUi(!muted);   // 已静音 → 恢复；未静音 → 静音
        }
        void OnGameAudioToggle(object sender, EventArgs e)
        {
            cfg.RecordGameAudio = !cfg.RecordGameAudio;
            // 关闭「录打击音效」→ 直接锁住「录BGM」并强制关掉它（MuteMusic=1），做到"完全无声视频"
            if (!cfg.RecordGameAudio)
            {
                if (cfg.RecordBgm)
                {
                    cfg.RecordBgm = false;
                    ApplyBgmSetting(true);
                    Log.Write("录打击音效关了 → 录BGM 一并关掉（保证完全无声）");
                }
                else ApplyBgmSetting(false);
            }
            cfg.Save();
            Log.Write(cfg.RecordGameAudio
                ? "录打击音效：开（视频含游戏声音）"
                : "录打击音效：关（视频完全无声，录BGM 被锁）");
            if (obs != null && obs.IsConnected)
            {
                Thread t = new Thread(delegate()
                {
                    try { ApplyGameAudioSetting(); } catch { }
                    // 并行录制正在跑的话，把每一格的音源也一起切掉/打开（不然按钮要等下一次录制才生效）
                    try
                    {
                        for (int i = 1; i <= 6; i++)   // 并行上限是 6 格，P5/P6 也要跟着切（和 765 行那处一致）
                            SetInputMuteRaw("RepRecorder P" + i + " 声音", !cfg.RecordGameAudio);
                    }
                    catch { }
                });
                t.IsBackground = true;
                t.Start();
            }
            RefreshGameAudioButton();
        }
        void OnBgmToggle(object sender, EventArgs e)
        {
            cfg.RecordBgm = !cfg.RecordBgm;
            cfg.Save();
            ApplyBgmSetting(true);
            RefreshBgmButton();
            Log.Write(cfg.RecordBgm
                ? "录BGM：开（MuteMusic 改成 0，带背景音乐）"
                : "录BGM：关（MuteMusic 改成 1，只有音效）");
            if (cfg.RecordBgm)
                Log.Write("提醒：BGM 有版权风险（B 站等平台会检测），用不用你自己判断。");
        }
        void OnStart(object sender, EventArgs e)
        {
            StartRecording();
        }
        void OnStop(object sender, EventArgs e)
        {
            // 队列/批次根本没在跑的时候点「停止」什么都不该做。
            // 以前无条件走停止流程：空队列点一下也会写「已请求停止……」并把状态卡在「正在停止…」，
            // 看起来像把界面弄坏了（用户实测）。
            bool running = worker != null && worker.IsAlive;
            if (!running)
            {
                Log.Write("现在没有在录，不需要停止" + (Ui.RowCount == 0 ? "（队列也是空的）" : ""));
                cancel = false;
                Ui.Status("就绪");
                Ui.Post(delegate { btnStop.Enabled = false; btnStart.Enabled = true; });
                return;
            }
            cancel = true;
            Log.Write("已请求停止……");
            Ui.Status("正在停止…");
            Ui.Post(delegate { btnStop.Enabled = false; });   // 已经在停了，别让它被连点
        }
        void OnClear(object sender, EventArgs e)
        {
            if (worker != null && worker.IsAlive) { Log.Write("队列运行中，先停止再清空"); return; }
            lock (jobsLock) { jobs.Clear(); }
            // ⚠ 「钉住的本批」（pinnedBatch）存的是 Job 对象，队列清空后它们已经不在 jobs 里了。
            //   不同步清掉的话，下次放进新 rep 时 HighlightBatchRows 会拿这批**已经不存在的 job**
            //   去比对（pinnedBatch 非空就只认它），结果一行都不高亮 —— 用户实测：清空队列后
            //   再拖进来，状态栏写着「本批 6 个」，列表里却一条绿的都没有。
            pinnedBatch.Clear();
            Ui.ClearRows();
            Ui.SyncScroll();
            Log.Write("队列已清空");
        }
        void OnRemove(object sender, EventArgs e)        {
            if (worker != null && worker.IsAlive) { Log.Write("队列运行中，不能移除"); return; }
            List<Job> del = new List<Job>();
            foreach (Job it in Ui.SelectedJobs()) del.Add(it);
            // ⚠ 没有蓝色选中时，删「本批」那批绿色的 —— 跟「开始录制」一套逻辑。
            //   用户实测：钉子钉成绿色之后再点「移除选中」什么都不删（因为 green 不是 ListView 的选中态）。
            if (del.Count == 0 && pinnedBatch != null && pinnedBatch.Count > 0) del.AddRange(pinnedBatch);
            if (del.Count == 0)
            {
                Log.Write("没得移除：先在列表里选中几行（蓝色），或者用「标记本批」把它们标成绿色再移除");
                return;
            }
            lock (jobsLock)
            {
                foreach (Job j in del) { jobs.Remove(j); Ui.RemoveRow(j); }
                Ui.RenumberRows();
            }
            foreach (Job dj in del) pinnedBatch.Remove(dj);
            HighlightBatchRows();   // 移除后本批高亮要跟着挪
            Ui.SyncScroll();
            Log.Write("已从队列移除 " + del.Count + " 个 rep");
        }
        void OnMarkBatch(object sender, EventArgs e)
        {
            try
            {
                List<Job> sel = new List<Job>();
                foreach (Job it in Ui.SelectedJobs())
                {
                    Job j = it;
                    if (j != null && !sel.Contains(j)) sel.Add(j);
                }
                if (sel.Count == 0)
                {
                    Log.Write("还没选：先在列表里选中几行（蓝色），再点「标记本批」");
                    return;
                }
                pinnedBatch.Clear();
                foreach (Job j in sel) pinnedBatch.Add(j);
                suppressSelSync = true;                     // 取消蓝色选中时别让"数量跟随"连锁乱跳
                try { Ui.ClearSelection(); }
                catch { }
                finally { suppressSelSync = false; }
                HighlightBatchRows();
                SyncParallelCountToSelection();
                Log.Write("已把选中的 " + sel.Count + " 个标记为「本批」（绿色），数量也跟着改成 " +
                          Math.Min(6, sel.Count) + " 个");
            }
            catch (Exception ex) { Log.Write("标记本批失败：" + ex.Message); }
        }
        void OnRefreshQueue(object sender, EventArgs e)
        {
            int done = 0, waiting = 0, skip = 0;
            lock (jobsLock)
            {
                foreach (Job j in jobs)
                {
                    if (j.Status == "录制中" || j.Status == "准备" || j.Status == "启动游戏" || j.Status == "收尾")
                    {
                        skip++;
                        continue;   // 正在录的不动
                    }
                    string have = FindExistingOutput(j);
                    if (have != null)
                    {
                        j.Output = have;
                        try { j.Size = new FileInfo(have).Length; } catch { j.Size = 0; }
                        Ui.SetJob(j, "已录制", "已有视频，点开始会跳过");
                        done++;
                    }
                    else
                    {
                        j.Output = "";
                        j.Size = 0;
                        // ⚠ 也要清掉"用时"：VideoSeconds 是上一次录出来那条视频的真实时长，
                        //   视频删了它还留着，列表就会一直显示一个不存在的时长（用户实测的 bug）。
                        j.VideoSeconds = 0;
                        j.Started = DateTime.MinValue;
                        j.Ended = DateTime.MinValue;
                        Ui.SetJob(j, "等待", "还没有视频，会被录制");
                        waiting++;
                    }
                }
            }
            Log.Write("已刷新队列：有视频 " + done + " 个，待录制 " + waiting + " 个" +
                      (skip > 0 ? "，正在处理中 " + skip + " 个（未改动）" : ""));
        }
        void OnOpenLog(object sender, EventArgs e)
        {
            try
            {
                if (File.Exists(Log.FilePath)) Process.Start("notepad.exe", "\"" + Log.FilePath + "\"");
                else Log.Write("日志文件还没生成");
            }
            catch { }
        }
        // 「检查更新」：手动查一次（2026-10-10 用户要求，放在环境卡里）。
        // 和启动时那条后台检查的三点区别，都是有意的：
        //   ① 无视 6 小时节流 —— 手动点就是要【现在】查（那条节流是防"每次启动都敲服务器"）。
        //   ② 无视「以后再说」的跳过标记 —— 那个标记只用来挡【自动】弹窗。
        //   ③ 每种结果都弹窗（用户选的）：有新版走 UpdateBox，其余三种走 InfoBox。
        // ⚠ 查在后台线程、结果回 UI 线程处理；设置仍然只在 UI 线程落盘（这个项目的不变量）。
        void OnCheckUpdate(object sender, EventArgs e)
        {
            if (checkingUpdate) return;
            checkingUpdate = true;
            Ui.SetCheckUpdateBusy(true);

            Thread t = new Thread(delegate ()
            {
                string err = "";
                UpdateInfo info = null;
                string result = "invalid";
                try
                {
                    // ⚠ 只取一次清单（别写成"先 CheckOnce 再 FetchManifest"，那是两次 HTTP）。
                    info = UpdateCheck.FetchManifest(UpdateCheck.ManifestUrl, out err);
                    if (info == null) { result = "invalid"; }
                    else
                    {
                        int c = UpdateCheck.CompareVersions(info.Version, AppInfo.Version);
                        if (c == int.MinValue)
                        { err = "清单里的版本号看不懂：" + info.Version; info = null; result = "invalid"; }
                        else result = c > 0 ? "newer" : (c == 0 ? "same" : "older");
                    }
                }
                catch (Exception ex) { err = ex.Message; info = null; result = "invalid"; }

                Ui.Post(delegate
                {
                    checkingUpdate = false;
                    Ui.SetCheckUpdateBusy(false);

                    // 时间戳不管成败都记，而且【在 UI 线程落盘】：失败还死缠着查会一直敲人家的服务器；
                    // 记上之后启动那次后台检查也不会紧跟着再查一遍。设置只在 UI 线程写 —— 不变量。
                    try
                    {
                        if (cfg != null) { cfg.LastUpdateCheckUtc = DateTime.UtcNow.ToString("o"); cfg.Save(); }
                    }
                    catch (Exception ex) { Log.Write("检查更新：时间戳没写进去（不影响使用）：" + ex.Message); }

                    Log.Write("手动检查更新：" + result +
                              (string.IsNullOrEmpty(err) ? "" : "（" + err + "）"));

                    if (result == "newer") Ui.ShowUpdateDialog(info);
                    else if (result == "same")
                        Ui.ShowInfo("检查更新", "已是最新版本（v" + AppInfo.Version + "），不用更新。");
                    else if (result == "older")
                        Ui.ShowInfo("检查更新", "服务器上是 v" + info.Version + "，比你这版旧 —— 你可能在用测试版。");
                    else
                        Ui.ShowInfo("检查更新", "检查失败：" +
                                    (string.IsNullOrEmpty(err) ? "拿不到更新清单" : err) +
                                    "。\n\n不影响录制，稍后再试就行。");
                });
            });
            t.IsBackground = true;
            t.Name = "检查更新（手动）";
            t.Start();
        }

        // 「检查环境」：把五项报给用户看。
        // 2026-10-08 之前这里【只写日志】—— 用户点完一个窗都不弹，得自己开终端去翻，
        // 而左下角那张环境卡又只报两项。现在先弹窗，日志照写（脚本和排查都还要看它）。
        void OnCheck(object sender, EventArgs e)
        {
            Settings.RestartSearchesIfNeeded(cfg);   // 点「检查环境」= 当场重新找一次游戏和 ffmpeg
            cfg.FillDefaults();
            cfg.Save();
            RefreshEnvLabel();
            Log.Write("---- 环境检查 ----");
            Log.Write("游戏目录：" + (string.IsNullOrEmpty(cfg.GameDir) ? "未找到！" : cfg.GameDir));
            Log.Write("OBS     ：" + (string.IsNullOrEmpty(cfg.ObsExe) ? "未安装！" : cfg.ObsExe));
            Log.Write("WebSocket：127.0.0.1:" + cfg.Port + "（密码" + (string.IsNullOrEmpty(cfg.Password) ? "为空" : "已获取") + "）");
            // ffmpeg：并行录制必需（放在程序旁边最省事），别人电脑上最常缺的就是它
            try
            {
                string ff = FindFfmpeg();
                Log.Write("ffmpeg  ：" + (ff == null
                    ? "找不到！并行录制会失败 —— 把 ffmpeg.exe 放到程序同目录"
                    : ff));
            }
            catch { }
            // 换台电脑最容易漏的一步：整合包里的 ReplayDnD 没启用 → 游戏会开在标题画面，录出来根本不是对局。
            // 这里只【读】ModLoaderSettings.json 判断一下，绝不写它（写它会让 mod loader 崩，见 README 第十六节）。
            try
            {
                if (!string.IsNullOrEmpty(cfg.GameDir))
                {
                    string mls = Path.Combine(cfg.GameDir, "ModLoaderSettings.json");
                    if (File.Exists(mls))
                    {
                        // 用 JSON 解析（键名里是反斜杠，正则容易写错）
                        Dictionary<string, object> root = (Dictionary<string, object>)
                            new JavaScriptSerializer().DeserializeObject(File.ReadAllText(mls, Encoding.UTF8));
                        bool on = false, found = false;
                        object modsObj = (root != null && root.ContainsKey("modules")) ? root["modules"] : null;
                        Dictionary<string, object> mods = modsObj as Dictionary<string, object>;
                        if (mods != null)
                        {
                            foreach (KeyValuePair<string, object> kv in mods)
                            {
                                if (kv.Key.IndexOf("ReplayDnD", StringComparison.OrdinalIgnoreCase) < 0) continue;
                                found = true;
                                Dictionary<string, object> v = kv.Value as Dictionary<string, object>;
                                if (v != null && v.ContainsKey("enabled") && v["enabled"] != null && Convert.ToBoolean(v["enabled"]))
                                    on = true;
                            }
                        }
                        Log.Write("ReplayDnD mod：" + (on ? "已启用 ✓" : (found ? "没启用 ⚠" : "整合包里没有 ⚠")));
                        if (!on)
                            Log.Write("  ⚠ 请在 mod 配置菜单里启用 ReplayDnD，否则录出来的不是对局");
                    }
                    else Log.Write("ReplayDnD mod：找不到 " + mls + "（游戏目录可能不对）");
                }
            }
            catch (Exception ex) { Log.Write("检查 ReplayDnD 失败（不影响使用）：" + ex.Message); }
            try
            {
                if (!obs.IsConnected) obs.Connect(cfg.Port, cfg.Password);
                Log.Write("OBS 连接：成功");
                Log.Write("当前场景：" + Convert.ToString(obs.Request("GetSceneList", null)["currentProgramSceneName"]));
                List<object> inputs = ObsClient.ToList(obs.Request("GetInputList", null)["inputs"]);
                if (inputs.Count == 0) Log.Write("场景里还没有任何源（可用「准备 OBS」自动创建）");
                foreach (object o in inputs)
                {
                    Dictionary<string, object> m = (Dictionary<string, object>)o;
                    Log.Write("  源：" + Convert.ToString(m["inputName"]) + "  (" + Convert.ToString(m["unversionedInputKind"]) + ")");
                }
            }
            catch (Exception ex) { Log.Write("OBS 连接：失败 —— " + ex.Message); }

            // 最后把结论弹出来给用户看（日志上面已经写全了）。
            try
            {
                Ui.ShowEnvDialog(EnvReport.Build(cfg));
                RefreshEnvLabel();
            }
            catch (Exception ex) { Log.Write("环境窗弹出失败（不影响使用）：" + ex.Message); }
        }
        void OnPrepare(object sender, EventArgs e)
        {
            // ⚠ 录制中不许「准备 OBS」（2026-10-08 查实）：PrepareObs 第一句就是
            //   StopObsRecordingIfAny()，而且 AutoCanvas 会顺手把画布改成 1440×1080 ——
            //   正在跑的那一批当场被掐断、画布也被改错，而 worker 完全不知道：
            //   它继续等游戏退出，最后 StopRecord 报 501、退回去捡那个残缺文件，
            //   然后每一格都标成「完成」。界面上只有 开始/停止 两个键在录制时被锁，
            //   所以这个键点得到 —— 这里补上同一道闸。
            if (worker != null && worker.IsAlive)
            {
                Log.Write("正在录制，先点「停止」再准备 OBS");
                return;
            }
            if (prepareBusy) return;          // 正在准备就别再来一遍（连点）

            // 2026-10-12：整段丢到后台线程。
            //   以前是在 UI 线程上同步跑的 —— EnsureObs 里有 Sleep(3000)，后面还跟着最长 60 秒
            //   的"每 2 秒重连一次"。OBS 没开、或者起着但 WebSocket 连不上时，这几十秒里界面
            //   完全不动、连一句"正在准备"都不显示，用户会以为程序崩了（实测卡 7.6 秒；
            //   OBS 起不来/连不上时能到 60 秒出头）。
            //   挪得动的前提：PrepareObs 全程不碰任何控件（只发 obs-websocket 请求 + 写日志），
            //   已经逐行确认过。
            prepareBusy = true;
            Ui.Status("正在准备 OBS…");
            Ui.Post(delegate { btnStart.Enabled = false; });   // 照 OnStop 里那个写法直接改影子按钮
            Thread t = new Thread(delegate()
            {
                try
                {
                    if (!EnsureObs()) return;
                    PrepareObs(true);
                    Log.Write("OBS 准备完毕");
                    Ui.Status("就绪");
                }
                catch (Exception ex) { Log.Write("准备 OBS 失败：" + ex.Message); }
                finally
                {
                    prepareBusy = false;
                    // 只有还停在"准备中"时才把状态改回就绪 —— 免得盖掉录制流程写的状态字。
                    Ui.Post(delegate { if (!(worker != null && worker.IsAlive)) btnStart.Enabled = true; });
                }
            });
            t.IsBackground = true;
            t.Name = "准备OBS";
            t.Start();
        }
        // 设置窗口整个搬去界面层了（旧版是个 PropertyGrid 对话框，WPF 里没有现成控件，
        // 用反射列字段自己搭了一个，见 Ui.ShowSettings）
        void OnSettings(object sender, EventArgs e)
        {
            Ui.ShowSettings();
        }
        // ---- Win32 声明与状态 ----
        delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr hWnd, StringBuilder s, int n);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hWnd, StringBuilder s, int n);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool SetWindowText(IntPtr hWnd, string text);
        [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr hWnd, out RECT r);
        [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr hWnd, ref POINT p);
        struct POINT { public int X, Y; }
        [DllImport("user32.dll")] static extern bool IsIconic(IntPtr hWnd);
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hWnd, int cmd);
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
        // 屏幕工作区（物理像素）—— 给 GrowGameWindows 判断"窗口撑到多大还放得下"。见那儿的注释。
        [DllImport("user32.dll")] static extern bool SystemParametersInfo(uint action, uint param, out RECT data, uint winIni);
        struct RECT { public int Left, Top, Right, Bottom; }
        const int SW_RESTORE = 9;
        const uint SPI_GETWORKAREA = 0x0030;
        const uint SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010;
        const int CAPBTN_W = 46;   // 窗口按钮宽度（三个并排贴在右上角）
                    const int arm = 12;                         // 角标每条臂的长度
        const int MaxRaw = 2000;                            // 只留最近这么多原始行，免得越跑越慢

        // ---- extra logic members ----
        List<Job> pinnedBatch = new List<Job>();   // 点空白处"钉"下来的本批（绿色标记），当作手动选中用

        List<Job> selectedBatch = null;   // 「录制选中的」交给队列线程的那批 rep

        volatile bool forceRecord = false; // 「录制选中的」期间忽略「已有录像就跳过」

        const string SingleWinTitle = "th123 录制中";

        bool obsStuckRetried = false;      // 这一批是否已经因为「OBS 没真正开录」自动重启过 OBS（只重试一次）

        int minimizedFixCount = 0;

        bool parallelRequested = false;

        bool suppressSelSync = false;      // 恢复列表选择期间抑制"数量跟随选择"（否则会连锁乱跳）

        static readonly string LastFramePath =
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "RepRecorder.lastframe.jpg");

        readonly Dictionary<string, bool> micOriginalMute = new Dictionary<string, bool>();

        readonly Dictionary<string, bool> deskOriginalMute = new Dictionary<string, bool>();

        static readonly string[] BgmIniSubPaths = new string[] { @"modules\ReplayDnD\ReplayDnD.ini", @"modules\ReplayDnD-old\ReplayDnD.ini" };

        // 一次抽完所有格子的"画面从第几秒开始"（0 = 整段没画面 / 分析失败 → 不往前剪）。
        // ⚠ 2026-10-13 改成一次 ffmpeg 抽全部：以前是**每格单独开一个进程**、各自把整张中继
        //   画布解码一遍。画布从 1281×640 涨到 3840×1920（每格按真实像素采）之后，这一条
        //   从 16 秒变成 111 秒 —— 而它要的只是 160×120、5fps 的缩略图，白白解码 6 遍没道理。
        //   现在：一次解码 → split=N → 每格各自 crop+scale 出自己那串 jpg（实测回到 20 秒上下）。
        double[] AnalyzeCellHeads(string grid, int count, int cols, int cw, int ch)
        {
            double[] heads = new double[count];
            string ff = FindFfmpeg();
            if (ff == null || count <= 0) return heads;
            string tmpDir = Path.Combine(Path.GetTempPath(), "RepRecorder_scan_" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(tmpDir);
                StringBuilder fc = new StringBuilder();
                StringBuilder outArgs = new StringBuilder();
                fc.Append("[0:v]split=").Append(count);
                for (int i = 0; i < count; i++) fc.Append("[b").Append(i).Append("]");
                for (int i = 0; i < count; i++)
                {
                    fc.Append(";[b").Append(i).Append("]crop=").Append(cw).Append(":").Append(ch).Append(":")
                      .Append((i % cols) * cw).Append(":").Append((i / cols) * ch)
                      .Append(",scale=160:120,fps=5[o").Append(i).Append("]");
                    outArgs.Append(" -map \"[o").Append(i).Append("]\" -q:v 6 \"")
                           .Append(Path.Combine(tmpDir, "h" + i + "_%04d.jpg")).Append("\"");
                }
                ProcessStartInfo psi = new ProcessStartInfo(ff,
                    HwaccelPrefix(EncName()) +
                    "-hide_banner -loglevel error -y -t 45 -i \"" + grid + "\" -filter_complex \"" + fc + "\"" + outArgs);
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.WorkingDirectory = Path.GetDirectoryName(ff);
                Process p = Process.Start(psi);
                FfmpegWait w = WaitFfmpeg(p, "抽帧分析");
                int rc = ExitCodeAndDispose(p);
                // 显卡解码这条路失败（老驱动 / 没那张卡）→ 退回软解重来一次。
                // 只在"设置里选的就是显卡编码"时才试过显卡（见 HwaccelPrefix）；A 卡机器这里走 d3d11va，也有这条兜底。
                if (HwaccelPrefix(EncName()).Length > 0 && (rc != 0 || w != FfmpegWait.Exited))
                {
                    Log.Write("并行：显卡解码抽帧没成（退出码 " + rc + "），退回软解重试");
                    foreach (string s in Directory.GetFiles(tmpDir)) { try { File.Delete(s); } catch { } }
                    psi = new ProcessStartInfo(ff,
                        "-hide_banner -loglevel error -y -t 45 -i \"" + grid + "\" -filter_complex \"" + fc + "\"" + outArgs);
                    psi.UseShellExecute = false;
                    psi.CreateNoWindow = true;
                    psi.WorkingDirectory = Path.GetDirectoryName(ff);
                    p = Process.Start(psi);
                    w = WaitFfmpeg(p, "抽帧分析");
                    ExitCodeAndDispose(p);
                }
                // 超时或取消：当作"分析不出来"，全部留在 0（也就是不往前剪）——
                // 拿半截帧去判断只会得出错的头位置，比不剪更糟。只解码一遍，所有格子一起作废。
                if (w != FfmpegWait.Exited) return heads;
                for (int i = 0; i < count; i++)
                {
                    string[] files = Directory.GetFiles(tmpDir, "h" + i + "_*.jpg");
                    Array.Sort(files, StringComparer.Ordinal);
                    for (int k = 0; k < files.Length; k++)
                    {
                        try
                        {
                            if (!IsBlank(File.ReadAllBytes(files[k])))
                            {
                                heads[i] = k / 5.0;      // 第 k 帧（5 帧/秒）
                                break;
                            }
                        }
                        catch { }
                    }
                }
            }
            catch { }
            finally
            {
                try { if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, true); } catch { }
            }
            return heads;
        }

        // MISSING: ReadBgmMuted

        // MISSING: MuteFlagPath

    }
}
