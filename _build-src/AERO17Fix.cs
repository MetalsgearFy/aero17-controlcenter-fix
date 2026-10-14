// ============================================================
// AERO 17-XA 控制中心一键修复安装器 (C# WinForms, .NET Framework 4.x)
// 内嵌数据布局: [exe] [CC包] [XTU包] [manifest: A17FIXPK ...]
// 修复流程: 解压 -> 检测XTU -> (卸载不配套) -> 静默安装配套XTU -> 运行控制中心安装 -> 完成/重启
// ============================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace Aero17Fix
{
    // ---------------- 附加数据解析 ----------------
    static class PackageExtractor
    {
        const string MAGIC = "A17FIXPK";

        public class Entry
        {
            public string Name;
            public long Offset;
            public long Length;
        }

        public static List<Entry> ParseManifest(string self)
        {
            using (FileStream fs = File.Open(self, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                long len = fs.Length;
                if (len < 1024) throw new InvalidDataException("file too small");
                int tail = (int)Math.Min(len, 4 * 1024 * 1024);
                byte[] buf = new byte[tail];
                fs.Position = len - tail;
                int got = fs.Read(buf, 0, tail);
                string s = Encoding.ASCII.GetString(buf, 0, got);
                int idx = s.LastIndexOf(MAGIC, StringComparison.Ordinal);
                if (idx < 0) throw new InvalidDataException("package manifest not found");
                fs.Position = len - tail + idx + MAGIC.Length;
                BinaryReader br = new BinaryReader(fs, Encoding.UTF8);
                int count = br.ReadInt32();
                if (count <= 0 || count > 64) throw new InvalidDataException("bad entry count");
                List<Entry> list = new List<Entry>();
                for (int i = 0; i < count; i++)
                {
                    int nameLen = br.ReadInt32();
                    if (nameLen <= 0 || nameLen > 512) throw new InvalidDataException("bad name len");
                    byte[] nb = br.ReadBytes(nameLen);
                    string name = Encoding.UTF8.GetString(nb);
                    long off = br.ReadInt64();
                    long length = br.ReadInt64();
                    if (off < 0 || length <= 0 || off + length > len) throw new InvalidDataException("bad offset/len");
                    list.Add(new Entry { Name = name, Offset = off, Length = length });
                }
                return list;
            }
        }

        public static List<string> ExtractAll(string self, string outDir)
        {
            List<string> result = new List<string>();
            List<Entry> entries = ParseManifest(self);
            using (FileStream fs = File.Open(self, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                byte[] buf = new byte[1 << 20];
                foreach (Entry e in entries)
                {
                    string target = Path.Combine(outDir, e.Name);
                    using (FileStream os = File.Create(target))
                    {
                        fs.Position = e.Offset;
                        long remain = e.Length;
                        while (remain > 0)
                        {
                            int n = fs.Read(buf, 0, (int)Math.Min(buf.Length, remain));
                            if (n <= 0) throw new InvalidDataException("unexpected eof");
                            os.Write(buf, 0, n);
                            remain -= n;
                        }
                    }
                    result.Add(target);
                }
            }
            return result;
        }
    }

    // ---------------- 修复引擎 ----------------
    class FixerEngine
    {
        public const string TARGET_VER = "7.5.3.3";
        public const string CC_NAME = "ControlCenter_Ver_22.12.20.01.exe";
        public const string XTU_NAME = "XTUSetup_7.5.3.3_OEM.exe";

        public bool XTUFound = false;
        public string XTUInstalledVer = "";
        public string XTUUninstallStr = "";
        public string XTUQuietUninstallStr = "";
        public bool NeedReboot = false;
        public string WorkDir = "";

        public void DetectXTU()
        {
            XTUFound = false;
            XTUInstalledVer = "";
            XTUUninstallStr = "";
            XTUQuietUninstallStr = "";
            string[] roots = {
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
                @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
            };
            foreach (string root in roots)
            {
                using (RegistryKey key = Registry.LocalMachine.OpenSubKey(root))
                {
                    if (key == null) continue;
                    foreach (string sub in key.GetSubKeyNames())
                    {
                        using (RegistryKey sk = key.OpenSubKey(sub))
                        {
                            if (sk == null) continue;
                            string dn = sk.GetValue("DisplayName") as string;
                            if (string.IsNullOrEmpty(dn)) continue;
                            if (dn.IndexOf("Extreme Tuning Utility", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                XTUFound = true;
                                XTUInstalledVer = (sk.GetValue("DisplayVersion") as string) ?? "";
                                XTUUninstallStr = (sk.GetValue("UninstallString") as string) ?? "";
                                XTUQuietUninstallStr = (sk.GetValue("QuietUninstallString") as string) ?? "";
                                return;
                            }
                        }
                    }
                }
            }
            // 文件版本兜底
            string[] svc = {
                @"C:\Windows\SysWOW64\XtuService.exe",
                @"C:\Program Files\Intel\Intel(R) Extreme Tuning Utility\Service\XtuService.exe",
                @"C:\Program Files (x86)\Intel\Intel(R) Extreme Tuning Utility\Service\XtuService.exe"
            };
            foreach (string p in svc)
            {
                if (File.Exists(p))
                {
                    try
                    {
                        string v = FileVersionInfo.GetVersionInfo(p).FileVersion;
                        if (!string.IsNullOrEmpty(v)) { XTUFound = true; XTUInstalledVer = v; return; }
                    }
                    catch { }
                }
            }
        }

        public static bool VersionEquals(string installed, string target)
        {
            string n = NormalizeVer(installed);
            string t = NormalizeVer(target);
            Version a, b;
            if (Version.TryParse(n, out a) && Version.TryParse(t, out b)) return a == b;
            return n == t;
        }

        static string NormalizeVer(string v)
        {
            if (string.IsNullOrEmpty(v)) return "";
            StringBuilder sb = new StringBuilder();
            int dots = 0;
            foreach (char c in v)
            {
                if (char.IsDigit(c)) sb.Append(c);
                else if (c == '.' && dots < 3) { sb.Append(c); dots++; }
                else break;
            }
            return sb.ToString();
        }

        // 解析命令行: "C:\path\app.exe" /args -> (cmd, args)
        static void SplitCommand(string line, out string cmd, out string args)
        {
            line = line.Trim();
            if (line.Length > 0 && line[0] == '"')
            {
                int end = line.IndexOf('"', 1);
                if (end > 0) { cmd = line.Substring(1, end - 1); args = line.Substring(end + 1).Trim(); return; }
            }
            int sp = line.IndexOf(' ');
            if (sp > 0) { cmd = line.Substring(0, sp); args = line.Substring(sp + 1).Trim(); }
            else { cmd = line; args = ""; }
        }

        public bool UninstallXTU(Action<string> log)
        {
            string cmd, args;
            if (!string.IsNullOrEmpty(XTUQuietUninstallStr))
            {
                SplitCommand(XTUQuietUninstallStr, out cmd, out args);
            }
            else if (!string.IsNullOrEmpty(XTUUninstallStr))
            {
                SplitCommand(XTUUninstallStr, out cmd, out args);
                string lower = cmd.ToLowerInvariant();
                if (lower.Contains("msiexec"))
                {
                    args = args.Replace("/I{", "/X{").Replace("/I ", "/X ").Replace("/i{", "/X{").Replace("/i ", "/X ");
                    args += " /quiet /norestart";
                }
                else
                {
                    args = args + " /S";
                }
            }
            else
            {
                log("未找到已注册的 XTU 卸载程序。");
                return false;
            }
            log("执行卸载命令: " + cmd + " " + args);
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(cmd, args);
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                Process p = Process.Start(psi);
                if (!p.WaitForExit(10 * 60 * 1000))
                {
                    try { p.Kill(); } catch { }
                    log("卸载超时（10 分钟），已终止。");
                    return false;
                }
                int code = p.ExitCode;
                log("卸载返回码: " + code);
                if (code == 0 || code == 3010)
                {
                    if (code == 3010) NeedReboot = true;
                    return true;
                }
                return false;
            }
            catch (Exception ex)
            {
                log("卸载异常: " + ex.Message);
                return false;
            }
        }

        public int InstallXTU(string xtuPath, Action<string> log)
        {
            string xtuLog = Path.Combine(WorkDir, "xtu_install.log");
            string args = string.Format("/quiet /norestart /log \"{0}\"", xtuLog);
            log("正在静默安装配套 XTU " + TARGET_VER + " ...");
            log("命令: \"" + xtuPath + "\" " + args);
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(xtuPath, args);
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                Process p = Process.Start(psi);
                if (!p.WaitForExit(15 * 60 * 1000))
                {
                    try { p.Kill(); } catch { }
                    log("XTU 静默安装超时（15 分钟）。");
                    return -1;
                }
                return p.ExitCode;
            }
            catch (Exception ex)
            {
                log("XTU 静默安装启动失败: " + ex.Message);
                return -1;
            }
        }

        public int RunCC(string ccPath, Action<string> log)
        {
            log("正在启动技嘉原厂控制中心安装包（请在向导中完成安装）...");
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(ccPath);
                psi.UseShellExecute = true;
                Process p = Process.Start(psi);
                if (!p.WaitForExit(30 * 60 * 1000))
                {
                    log("控制中心安装等待超时（30 分钟），请手动检查。");
                    return -1;
                }
                return p.ExitCode;
            }
            catch (Exception ex)
            {
                log("控制中心安装启动失败: " + ex.Message);
                return -1;
            }
        }
    }

    // ---------------- 向导主窗体 ----------------
    class MainForm : Form
    {
        Panel pnlWelcome, pnlDetect, pnlProgress, pnlFinish;
        Label lblDetectResult;
        Label lblStep;
        ProgressBar progressBar;
        TextBox txtLog;
        Button btnBack, btnNext, btnCancel;
        Label lblWelcomeText;
        Label lblFinishText;

        FixerEngine engine = new FixerEngine();
        System.ComponentModel.BackgroundWorker worker;
        bool fixing = false;
        string workDir = "";

        public MainForm()
        {
            Text = "AERO 17 控制中心一键修复安装器";
            Font = new Font("Microsoft YaHei UI", 9F);
            ClientSize = new Size(680, 480);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = true;
            StartPosition = FormStartPosition.CenterScreen;

            BuildPanels();
            ShowPanel(pnlWelcome);
        }

        void BuildPanels()
        {
            // 顶部标题
            Label title = new Label();
            title.Text = "AERO 17-XA 控制中心一键修复安装器";
            title.Font = new Font("Microsoft YaHei UI", 14F, FontStyle.Bold);
            title.Dock = DockStyle.Top;
            title.Height = 44;
            title.TextAlign = ContentAlignment.MiddleCenter;
            Controls.Add(title);

            // 底部按钮
            FlowLayoutPanel btns = new FlowLayoutPanel();
            btns.Dock = DockStyle.Bottom;
            btns.Height = 52;
            btns.FlowDirection = FlowDirection.RightToLeft;
            btns.Padding = new Padding(8);

            btnCancel = new Button(); btnCancel.Text = "关闭"; btnCancel.Width = 90; btnCancel.Margin = new Padding(6, 8, 6, 8);
            btnCancel.Click += delegate { Close(); };
            btnNext = new Button(); btnNext.Text = "下一步 >"; btnNext.Width = 100; btnNext.Margin = new Padding(6, 8, 6, 8);
            btnNext.Click += delegate { NextClicked(); };
            btnBack = new Button(); btnBack.Text = "< 上一步"; btnBack.Width = 100; btnBack.Margin = new Padding(6, 8, 6, 8);
            btnBack.Click += delegate { BackClicked(); };
            btns.Controls.Add(btnCancel);
            btns.Controls.Add(btnNext);
            btns.Controls.Add(btnBack);
            Controls.Add(btns);

            // 内容区容器
            Panel host = new Panel();
            host.Dock = DockStyle.Fill;
            Controls.Add(host);

            // ---- 欢迎页 ----
            pnlWelcome = new Panel(); pnlWelcome.Dock = DockStyle.Fill;
            lblWelcomeText = new Label();
            lblWelcomeText.Dock = DockStyle.Fill;
            lblWelcomeText.Padding = new Padding(24, 30, 24, 10);
            lblWelcomeText.Text =
                "本安装器用于修复技嘉 AERO 17-XA 控制中心无法打开 / 键盘卡顿问题。\r\n\r\n" +
                "故障根因：系统里安装了与技嘉原厂控制中心不配套的 Intel XTU（超频工具）版本，\r\n" +
                "导致控制中心与 XTU 服务通信解析异常。\r\n\r\n" +
                "本安装器将自动完成：\r\n" +
                "  1) 检测系统内 Intel XTU 版本\r\n" +
                "  2) 卸载不配套的旧版本（如 7.14.x）\r\n" +
                "  3) 静默安装配套版本 7.5.3.3\r\n" +
                "  4) 运行技嘉原厂控制中心 22.12.20.01 安装包\r\n" +
                "  5) 提示重启，使全部组件生效\r\n\r\n" +
                "内置组件均为技嘉/Intel 原厂签名文件，本安装器不修改任何系统安全设置。";
            lblWelcomeText.TextAlign = ContentAlignment.TopLeft;
            pnlWelcome.Controls.Add(lblWelcomeText);
            host.Controls.Add(pnlWelcome);

            // ---- 检测页 ----
            pnlDetect = new Panel(); pnlDetect.Dock = DockStyle.Fill;
            Label l1 = new Label();
            l1.Text = "检测系统内 Intel XTU（只读检查，不修改系统）";
            l1.Location = new Point(24, 22); l1.AutoSize = true; l1.Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold);
            pnlDetect.Controls.Add(l1);
            lblDetectResult = new Label();
            lblDetectResult.Location = new Point(24, 60);
            lblDetectResult.Size = new Size(620, 160);
            lblDetectResult.Text = "正在检测...";
            pnlDetect.Controls.Add(lblDetectResult);
            host.Controls.Add(pnlDetect);

            // ---- 进度页 ----
            pnlProgress = new Panel(); pnlProgress.Dock = DockStyle.Fill;
            lblStep = new Label();
            lblStep.Location = new Point(24, 18); lblStep.Size = new Size(620, 30);
            lblStep.Text = "准备中...";
            pnlProgress.Controls.Add(lblStep);
            progressBar = new ProgressBar();
            progressBar.Location = new Point(24, 54); progressBar.Size = new Size(620, 24);
            progressBar.Minimum = 0; progressBar.Maximum = 100;
            pnlProgress.Controls.Add(progressBar);
            Label l2 = new Label(); l2.Text = "执行日志：";
            l2.Location = new Point(24, 88); l2.AutoSize = true;
            pnlProgress.Controls.Add(l2);
            txtLog = new TextBox();
            txtLog.Multiline = true;
            txtLog.ReadOnly = true;
            txtLog.ScrollBars = ScrollBars.Both;
            txtLog.Location = new Point(24, 114); txtLog.Size = new Size(620, 300);
            pnlProgress.Controls.Add(txtLog);
            host.Controls.Add(pnlProgress);

            // ---- 完成页 ----
            pnlFinish = new Panel(); pnlFinish.Dock = DockStyle.Fill;
            lblFinishText = new Label();
            lblFinishText.Dock = DockStyle.Fill;
            lblFinishText.Padding = new Padding(24, 40, 24, 10);
            lblFinishText.Text = "";
            lblFinishText.TextAlign = ContentAlignment.TopLeft;
            pnlFinish.Controls.Add(lblFinishText);
            host.Controls.Add(pnlFinish);

            // worker
            worker = new System.ComponentModel.BackgroundWorker();
            worker.WorkerReportsProgress = true;
            worker.DoWork += Worker_DoWork;
            worker.ProgressChanged += Worker_ProgressChanged;
            worker.RunWorkerCompleted += Worker_RunWorkerCompleted;
        }

        void ShowPanel(Panel p)
        {
            pnlWelcome.Visible = (p == pnlWelcome);
            pnlDetect.Visible = (p == pnlDetect);
            pnlProgress.Visible = (p == pnlProgress);
            pnlFinish.Visible = (p == pnlFinish);
        }

        int pageIndex = 0; // 0 welcome, 1 detect, 2 progress, 3 finish

        void NextClicked()
        {
            if (fixing) return;
            if (pageIndex == 0)
            {
                pageIndex = 1;
                ShowPanel(pnlDetect);
                lblDetectResult.Text = "正在检测...";
                btnBack.Enabled = true;
                btnNext.Enabled = false;
                engine.DetectXTU();
                if (engine.XTUFound)
                {
                    if (FixerEngine.VersionEquals(engine.XTUInstalledVer, FixerEngine.TARGET_VER))
                        lblDetectResult.Text = "检测结果：\r\n\r\n已安装 Intel XTU 版本：" + engine.XTUInstalledVer + "\r\n状态：与配套版本 7.5.3.3 一致，无需替换。";
                    else
                        lblDetectResult.Text = "检测结果：\r\n\r\n已安装 Intel XTU 版本：" + (string.IsNullOrEmpty(engine.XTUInstalledVer) ? "(未知)" : engine.XTUInstalledVer) + "\r\n状态：与配套版本 7.5.3.3 不一致，下一步将自动替换。";
                }
                else
                {
                    lblDetectResult.Text = "检测结果：\r\n\r\n未检测到已安装的 Intel XTU（或注册信息缺失）。\r\n下一步将直接安装配套版本 7.5.3.3。";
                }
                btnNext.Enabled = true;
            }
            else if (pageIndex == 1)
            {
                pageIndex = 2;
                ShowPanel(pnlProgress);
                btnBack.Enabled = false;
                btnNext.Enabled = false;
                btnCancel.Enabled = false;
                btnNext.Text = "下一步 >";
                fixing = true;
                worker.RunWorkerAsync();
            }
            else if (pageIndex == 3)
            {
                Close();
            }
        }

        void BackClicked()
        {
            if (fixing) return;
            if (pageIndex == 1)
            {
                pageIndex = 0;
                ShowPanel(pnlWelcome);
                btnBack.Enabled = false;
            }
        }

        void AppendLog(string msg)
        {
            if (txtLog.IsHandleCreated)
            {
                txtLog.BeginInvoke((Action)(delegate
                {
                    txtLog.AppendText(msg + Environment.NewLine);
                    txtLog.ScrollToCaret();
                }));
            }
        }

        void Worker_DoWork(object sender, System.ComponentModel.DoWorkEventArgs e)
        {
            System.ComponentModel.BackgroundWorker w = (System.ComponentModel.BackgroundWorker)sender;
            try
            {
                workDir = Path.Combine(Path.GetTempPath(), "AERO17-Fix-Repair");
                Directory.CreateDirectory(workDir);

                w.ReportProgress(2, "步骤 0/5：解压内置组件 ...");
                string self = Assembly.GetExecutingAssembly().Location;
                List<string> files = PackageExtractor.ExtractAll(self, workDir);
                string ccPath = null, xtuPath = null;
                foreach (string f in files)
                {
                    string n = Path.GetFileName(f);
                    if (n == FixerEngine.CC_NAME) ccPath = f;
                    else if (n == FixerEngine.XTU_NAME) xtuPath = f;
                }
                if (ccPath == null || xtuPath == null) throw new InvalidDataException("内置组件缺失");
                engine.WorkDir = workDir;
                AppendLog("已解压内置组件到 " + workDir);
                AppendLog("  控制中心: " + ccPath);
                AppendLog("  XTU: " + xtuPath);

                w.ReportProgress(25, "步骤 1/5：检测系统 XTU 版本 ...");
                engine.DetectXTU();
                if (engine.XTUFound)
                {
                    AppendLog("检测到已安装 XTU 版本: " + (string.IsNullOrEmpty(engine.XTUInstalledVer) ? "(未知)" : engine.XTUInstalledVer));
                    if (FixerEngine.VersionEquals(engine.XTUInstalledVer, FixerEngine.TARGET_VER))
                    {
                        AppendLog("版本匹配（" + FixerEngine.TARGET_VER + "），无需卸载。");
                    }
                    else
                    {
                        AppendLog("版本不匹配，需要替换为 " + FixerEngine.TARGET_VER + "。");
                        DialogResult dlg = (DialogResult)lblStep.Invoke((Func<DialogResult>)(delegate
                        {
                            return MessageBox.Show(this,
                                "检测到系统已安装 Intel XTU 版本：" + engine.XTUInstalledVer +
                                "\r\n\r\n它与技嘉原厂控制中心配套的 7.5.3.3 不匹配（这是导致控制中心打不开、键盘异常的直接原因）。\r\n\r\n是否立即卸载该版本并安装配套版本？",
                                "XTU 版本不匹配", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                        }));
                        if (dlg == DialogResult.Yes)
                        {
                            w.ReportProgress(50, "步骤 2/5：卸载旧版 XTU ...");
                            bool ok = engine.UninstallXTU(AppendLog);
                            if (ok) AppendLog("旧版 XTU 卸载完成。");
                            else AppendLog("旧版 XTU 卸载未完全成功，继续尝试安装配套版本。");
                        }
                        else
                        {
                            AppendLog("用户选择跳过卸载，直接安装配套版本。");
                        }
                    }
                }
                else
                {
                    AppendLog("未检测到已安装 XTU（全新安装场景）。");
                }

                w.ReportProgress(62, "步骤 3/5：静默安装配套 XTU " + FixerEngine.TARGET_VER + " ...");
                int xtuCode = engine.InstallXTU(xtuPath, AppendLog);
                if (xtuCode == 0) AppendLog("XTU 安装成功（返回码 0）。");
                else if (xtuCode == 3010) { AppendLog("XTU 安装成功（返回码 3010，重启后完全生效）。"); engine.NeedReboot = true; }
                else
                {
                    AppendLog("XTU 静默安装返回码: " + xtuCode + "，改用手动向导安装。");
                    lblStep.BeginInvoke((Action)(delegate
                    {
                        MessageBox.Show(this, "XTU 静默安装返回码为 " + xtuCode + "。\r\n将打开配套 XTU 安装向导，请手动完成安装。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }));
                    ProcessStartInfo psi = new ProcessStartInfo(xtuPath);
                    psi.UseShellExecute = true;
                    Process p = Process.Start(psi);
                    p.WaitForExit(15 * 60 * 1000);
                    if (p.HasExited && p.ExitCode == 3010) engine.NeedReboot = true;
                }

                w.ReportProgress(85, "步骤 4/5：安装技嘉原厂控制中心 ...");
                int ccCode = engine.RunCC(ccPath, AppendLog);
                if (ccCode == 3010) engine.NeedReboot = true;
                AppendLog("控制中心安装结束（返回码 " + ccCode + "）。");

                w.ReportProgress(100, "步骤 5/5：完成");
                AppendLog("全部步骤执行完毕。");
            }
            catch (Exception ex)
            {
                AppendLog("发生错误: " + ex.Message);
                AppendLog(ex.ToString());
                e.Result = "error";
                return;
            }
            e.Result = "ok";
        }

        void Worker_ProgressChanged(object sender, System.ComponentModel.ProgressChangedEventArgs e)
        {
            lblStep.Text = (e.UserState ?? "").ToString();
            progressBar.Value = e.ProgressPercentage;
        }

        void Worker_RunWorkerCompleted(object sender, System.ComponentModel.RunWorkerCompletedEventArgs e)
        {
            fixing = false;
            btnCancel.Enabled = true;
            if (e.Result != null && e.Result.ToString() == "error")
            {
                lblFinishText.Text = "修复过程中出现错误。\r\n\r\n请查看上方日志，或手动操作：\r\n" +
                    "  1) 打开“设置 → 应用 → 已安装的应用”，卸载不配套的 Intel Extreme Tuning Utility\r\n" +
                    "  2) 重新运行本安装器重试";
                btnNext.Text = "关闭";
                btnNext.Enabled = true;
                pageIndex = 3;
                ShowPanel(pnlFinish);
                return;
            }
            string msg = "修复安装完成。\r\n\r\n";
            if (engine.NeedReboot)
                msg += "系统需要重启才能让 XTU 服务与控制中心正常协作。\r\n请保存工作后重启 Windows。\r\n\r\n重启后从开始菜单打开 Control Center，验证风扇、键盘背光与 CPU 调参功能。";
            else
                msg += "请从开始菜单打开 Control Center，验证风扇、键盘背光与 CPU 调参功能。";
            lblFinishText.Text = msg;
            btnNext.Text = "完成";
            btnNext.Enabled = true;
            pageIndex = 3;
            ShowPanel(pnlFinish);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (fixing) { e.Cancel = true; return; }
            base.OnFormClosing(e);
        }
    }

    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            if (args.Length > 0 && args[0] == "--extract-only")
            {
                string outDir = (args.Length > 1) ? args[1] : Path.Combine(Path.GetTempPath(), "AERO17-Fix-Test");
                string logFile = Path.Combine(Path.GetTempPath(), "AERO17_fix_extract.log");
                try
                {
                    Directory.CreateDirectory(outDir);
                    string self = Assembly.GetExecutingAssembly().Location;
                    List<string> files = PackageExtractor.ExtractAll(self, outDir);
                    StringBuilder sb = new StringBuilder();
                    sb.AppendLine("EXTRACT_OK");
                    foreach (string f in files)
                    {
                        FileInfo fi = new FileInfo(f);
                        sb.AppendLine(f + " | " + fi.Length);
                    }
                    File.WriteAllText(logFile, sb.ToString(), Encoding.UTF8);
                }
                catch (Exception ex)
                {
                    File.WriteAllText(logFile, "EXTRACT_FAIL: " + ex.ToString(), Encoding.UTF8);
                    Environment.Exit(1);
                }
                Environment.Exit(0);
            }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }
}
