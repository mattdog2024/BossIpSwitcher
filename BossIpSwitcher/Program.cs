using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.ServiceProcess;
using System.Text.Json;

namespace BossIpSwitcher;

internal static class Program
{
    internal const string ServiceName = "BossIpProtectionService";
    internal static readonly string DataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "BossIpSwitcher");
    internal static readonly string SettingsPath = Path.Combine(DataDir, "settings.json");
    internal static readonly string ModePath = Path.Combine(DataDir, "mode.txt");

    [STAThread]
    private static void Main(string[] args)
    {
        Directory.CreateDirectory(DataDir);
        if (args.Contains("--service")) { ServiceBase.Run(new IpProtectionService()); return; }
        if (args.Contains("--uninstall")) { ServiceManager.Uninstall(); return; }

        using var mutex = new Mutex(true, "BossIpSwitcher.Ui.SingleInstance", out var first);
        if (!first) return;
        ApplicationConfiguration.Initialize();
        if (!ServiceManager.IsInstalled() && MessageBox.Show(
            "需要安装明确命名的 Windows IP 保护服务。安装后，即使快捷键程序被结束，服务仍会保持网络配置。是否现在安装？",
            "IP 锁定器", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
            ServiceManager.Install();
        Application.Run(new SwitcherContext());
    }
}

internal sealed class AppSettings
{
    public string Adapter { get; set; } = "";
    public string AlternateIp { get; set; } = "192.168.1.100";
    public string AlternateMask { get; set; } = "255.255.255.0";
    public string AlternateGateway { get; set; } = "192.168.1.1";
    public bool StartWithWindows { get; set; }
}

internal sealed class IpProtectionService : ServiceBase
{
    private readonly System.Timers.Timer timer = new(5000) { AutoReset = true };
    private int busy;
    private int mismatchCount;
    private DateTime lastApplyUtc = DateTime.MinValue;
    public IpProtectionService() { ServiceName = Program.ServiceName; CanStop = true; AutoLog = true; }
    protected override void OnStart(string[] args) { timer.Elapsed += async (_, _) => await GuardAsync(); timer.Start(); }
    protected override void OnStop() => timer.Stop();

    private async Task GuardAsync()
    {
        if (Interlocked.Exchange(ref busy, 1) != 0) return;
        try
        {
            var s = SettingsStore.Load();
            if (string.IsNullOrWhiteSpace(s.Adapter)) return;
            var alternate = File.Exists(Program.ModePath) && File.ReadAllText(Program.ModePath).Trim() == "alternate";
            var ip = alternate ? s.AlternateIp : "20.65.32.199";
            var mask = alternate ? s.AlternateMask : "255.255.255.0";
            var gateway = alternate ? s.AlternateGateway : "20.65.32.254";
            if (NetworkTools.HasAddress(s.Adapter, ip, mask))
            {
                mismatchCount = 0;
                return;
            }

            mismatchCount++;
            if (mismatchCount < 3 || DateTime.UtcNow - lastApplyUtc < TimeSpan.FromMinutes(1)) return;
            lastApplyUtc = DateTime.UtcNow;
            mismatchCount = 0;
            await NetworkTools.ApplyAsync(s.Adapter, ip, mask, gateway);
        }
        catch { }
        finally { Interlocked.Exchange(ref busy, 0); }
    }
}

internal sealed class SwitcherContext : ApplicationContext
{
    private const string Password = "ccisme520";
    private readonly KeyboardHook keyboard = new();
    // 隐藏的 dispatcher form：用于把 hook 回调里的 UI 操作 marshal 回主消息循环。
    // 钩子线程里同步 ShowDialog 会让系统键盘钩子被卸载（5 秒超时），用户再次按
    // Ctrl+Alt+F10 时还会嵌套第二轮 OpenProtectedSettings，崩。BeginInvoke 解决。
    private readonly Form dispatcher = new()
    {
        ShowInTaskbar = false,
        FormBorderStyle = FormBorderStyle.None,
        Opacity = 0,
        WindowState = FormWindowState.Minimized,
        Text = "BossIpSwitcherDispatcher",
    };
    private AppSettings settings = SettingsStore.Load();
    private Process? marker;
    private bool alternateMode => File.Exists(Program.ModePath) && File.ReadAllText(Program.ModePath).Trim() == "alternate";

    public SwitcherContext()
    {
        // 先创建 handle（窗口不可见），之后 BeginInvoke 才会 marshal 到主线程
        // CreateControl() alone does not guarantee a native handle for an invisible
        // top-level Form. BeginInvoke requires that handle, so force it here while
        // we are still on the UI thread.
        _ = dispatcher.Handle;
        // 钩子回调里只投递任务，不直接做 UI 工作
        keyboard.TogglePressed += () => PostToUi(async () => await ToggleAsync());
        keyboard.MenuPressed += () => PostToUi(OpenProtectedSettings);
        keyboard.Start();
        if (string.IsNullOrWhiteSpace(settings.Adapter))
            MessageBox.Show("首次运行不会修改网络。请按 Ctrl+Alt+F10 选择网卡并设置备用网络。", "IP 锁定器");
    }

    private void PostToUi(Action action)
    {
        if (dispatcher.IsDisposed || !dispatcher.IsHandleCreated) return;
        try { dispatcher.BeginInvoke(action); }
        catch (InvalidOperationException) { }
    }

    private async Task ToggleAsync()
    {
        settings = SettingsStore.Load();
        if (string.IsNullOrWhiteSpace(settings.Adapter)) return;
        if (!alternateMode)
        {
            if (!Valid(settings.AlternateIp) || !Valid(settings.AlternateMask) || !Valid(settings.AlternateGateway)) return;
            SettingsStore.SetMode(true);
            await NetworkTools.ApplyAsync(settings.Adapter, settings.AlternateIp, settings.AlternateMask, settings.AlternateGateway);
            marker = Process.Start(new ProcessStartInfo("notepad.exe") { UseShellExecute = true });
        }
        else
        {
            CloseMarker(); SettingsStore.SetMode(false);
            await NetworkTools.ApplyAsync(settings.Adapter, "20.65.32.199", "255.255.255.0", "20.65.32.254");
            // 切回固定 IP（"20 段"）后自动清空访问痕迹
            TraceCleaner.CleanAll();
        }
    }

    private void OpenProtectedSettings()
    {
        using var password = new PasswordForm();
        if (password.ShowDialog() != DialogResult.OK) return;
        if (password.Value != Password) { MessageBox.Show("密码错误。", "IP 锁定器"); return; }
        settings = SettingsStore.Load();
        using var form = new SettingsForm(settings, NetworkTools.Adapters());
        var result = form.ShowDialog();
        if (result == DialogResult.Abort) { ExitUi(); return; }
        if (result != DialogResult.OK) return;
        settings = form.Result; SettingsStore.Save(settings); ConfigureUiStartup(settings.StartWithWindows);
    }

    private static void ConfigureUiStartup(bool enabled)
    {
        var exe = Environment.ProcessPath!;
        var args = enabled ? $"/Create /F /TN \"BossIpSwitcherUI\" /SC ONLOGON /RL HIGHEST /TR \"\\\"{exe}\\\"\"" : "/Delete /F /TN \"BossIpSwitcherUI\"";
        using var p = Process.Start(new ProcessStartInfo("schtasks.exe", args) { UseShellExecute = false, CreateNoWindow = true }); p?.WaitForExit(5000);
    }
    private static bool Valid(string value) => IPAddress.TryParse(value, out var a) && a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork;
    private void CloseMarker() { try { if (marker is { HasExited: false }) marker.Kill(); } catch { } marker?.Dispose(); marker = null; }
    private void ExitUi() { CloseMarker(); keyboard.Dispose(); ExitThread(); }
    protected override void Dispose(bool disposing) { if (disposing) { keyboard.Dispose(); marker?.Dispose(); dispatcher.Dispose(); } base.Dispose(disposing); }
}

internal static class SettingsStore
{
    public static AppSettings Load() { try { return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Program.SettingsPath)) ?? new(); } catch { return new(); } }
    public static void Save(AppSettings s) { Directory.CreateDirectory(Program.DataDir); File.WriteAllText(Program.SettingsPath, JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true })); }
    public static void SetMode(bool alternate) { Directory.CreateDirectory(Program.DataDir); File.WriteAllText(Program.ModePath, alternate ? "alternate" : "fixed"); }
}

internal static class NetworkTools
{
    public static string[] Adapters() => NetworkInterface.GetAllNetworkInterfaces().Where(n => n.NetworkInterfaceType != NetworkInterfaceType.Loopback).Select(n => n.Name).Order().ToArray();
    public static bool HasAddress(string adapter, string ip, string mask)
    {
        var p = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n => n.Name == adapter)?.GetIPProperties();
        return p is not null && p.UnicastAddresses.Any(a => a.Address.ToString() == ip && a.IPv4Mask?.ToString() == mask);
    }
    public static async Task<bool> ApplyAsync(string adapter, string ip, string mask, string gateway)
    {
        var name = adapter.Replace("\"", "");
        using var p = Process.Start(new ProcessStartInfo("netsh.exe", $"interface ipv4 set address name=\"{name}\" source=static address={ip} mask={mask} gateway={gateway} store=persistent") { UseShellExecute = false, CreateNoWindow = true });
        if (p is null) return false; await p.WaitForExitAsync(); return p.ExitCode == 0;
    }
}

internal static class ServiceManager
{
    public static bool IsInstalled()
    {
        try { using var s = new ServiceController(Program.ServiceName); _ = s.Status; return true; } catch { return false; }
    }
    public static void Install()
    {
        var exe = Environment.ProcessPath!;
        Run("sc.exe", $"create {Program.ServiceName} binPath= \"\\\"{exe}\\\" --service\" start= auto DisplayName= \"Boss IP Protection Service\"");
        Run("sc.exe", $"failure {Program.ServiceName} reset= 0 actions= restart/1000/restart/1000/restart/1000");
        Run("sc.exe", $"start {Program.ServiceName}");
    }
    public static void Uninstall() { Run("sc.exe", $"stop {Program.ServiceName}"); Run("sc.exe", $"delete {Program.ServiceName}"); }
    private static void Run(string file, string args) { using var p = Process.Start(new ProcessStartInfo(file, args) { UseShellExecute = false, CreateNoWindow = true }); p?.WaitForExit(10000); }
}
