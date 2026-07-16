using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Text.Json;

namespace EmergencyRestore;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        var adapters = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .Select(n => n.Name).Order().ToArray();
        var saved = ReadSavedAdapter();
        using var form = new RestoreForm(adapters, saved);
        Application.Run(form);
    }

    private static string ReadSavedAdapter()
    {
        try
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "BossIpSwitcher", "settings.json");
            using var json = JsonDocument.Parse(File.ReadAllText(path));
            return json.RootElement.GetProperty("Adapter").GetString() ?? "";
        }
        catch { return ""; }
    }
}

internal sealed class RestoreForm : Form
{
    private readonly ComboBox adapter = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    public RestoreForm(string[] adapters, string saved)
    {
        Text = "IP 应急恢复"; StartPosition = FormStartPosition.CenterScreen; TopMost = true;
        FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = MinimizeBox = false; ClientSize = new Size(470, 210);
        Controls.Add(new Label { Text = "选择需要恢复的网卡：", Left = 20, Top = 25, AutoSize = true });
        adapter.SetBounds(20, 52, 425, 28); adapter.Items.AddRange(adapters); adapter.Text = saved; Controls.Add(adapter);
        Controls.Add(new Label { Text = "恢复后：20.65.32.199 / 255.255.255.0 / 20.65.32.254", Left = 20, Top = 95, AutoSize = true });
        var restore = new Button { Text = "立即恢复固定 IP", Left = 80, Top = 140, Width = 145, Height = 38 };
        var dhcp = new Button { Text = "紧急改为 DHCP", Left = 245, Top = 140, Width = 145, Height = 38 };
        restore.Click += async (_, _) => await RunRestore(false);
        dhcp.Click += async (_, _) => await RunRestore(true);
        Controls.AddRange([restore, dhcp]);
    }

    private async Task RunRestore(bool dhcp)
    {
        if (adapter.SelectedItem is null) { MessageBox.Show("请先选择网卡。"); return; }
        RunAndWait("sc.exe", "stop BossIpProtectionService");
        RunAndWait("sc.exe", "delete BossIpProtectionService");
        foreach (var p in Process.GetProcessesByName("BossIpSwitcher")) { try { p.Kill(true); } catch { } }
        var name = adapter.Text.Replace("\"", "");
        var args = dhcp
            ? $"interface ipv4 set address name=\"{name}\" source=dhcp"
            : $"interface ipv4 set address name=\"{name}\" source=static address=20.65.32.199 mask=255.255.255.0 gateway=20.65.32.254 store=persistent";
        using var process = Process.Start(new ProcessStartInfo("netsh.exe", args) { UseShellExecute = false, CreateNoWindow = true });
        if (process is null) { MessageBox.Show("无法启动网络恢复命令。", "失败"); return; }
        await process.WaitForExitAsync();
        MessageBox.Show(process.ExitCode == 0 ? "恢复命令已成功执行。" : "恢复失败，请检查网卡和管理员权限。",
            "IP 应急恢复", MessageBoxButtons.OK, process.ExitCode == 0 ? MessageBoxIcon.Information : MessageBoxIcon.Error);
    }

    private static void RunAndWait(string file, string args)
    {
        using var process = Process.Start(new ProcessStartInfo(file, args) { UseShellExecute = false, CreateNoWindow = true });
        process?.WaitForExit(10000);
    }
}
