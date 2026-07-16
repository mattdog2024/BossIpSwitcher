namespace BossIpSwitcher;

internal sealed class PasswordForm : Form
{
    private readonly TextBox input = new() { PasswordChar = '●', Width = 220 };
    public string Value => input.Text;
    public PasswordForm()
    {
        Text = "请输入管理密码"; FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen; ClientSize = new Size(300, 115); TopMost = true;
        Controls.Add(new Label { Text = "密码：", Left = 18, Top = 23, AutoSize = true });
        input.SetBounds(65, 18, 215, 28); Controls.Add(input);
        var ok = new Button { Text = "确定", DialogResult = DialogResult.OK, Left = 113, Top = 65, Width = 80 };
        var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, Left = 200, Top = 65, Width = 80 };
        Controls.AddRange([ok, cancel]); AcceptButton = ok; CancelButton = cancel;
    }
}

internal sealed class SettingsForm : Form
{
    private readonly ComboBox adapter = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly TextBox ip = new(), mask = new(), gateway = new();
    private readonly CheckBox startup = new() { Text = "随 Windows 启动" };
    public AppSettings Result { get; private set; }

    public SettingsForm(AppSettings current, string[] adapters)
    {
        Result = current; Text = "IP 锁定器设置"; StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = MinimizeBox = false; ClientSize = new Size(430, 335); TopMost = true;
        adapter.Items.AddRange(adapters); adapter.Text = current.Adapter;
        ip.Text = current.AlternateIp; mask.Text = current.AlternateMask; gateway.Text = current.AlternateGateway; startup.Checked = current.StartWithWindows;
        AddRow("控制网卡：", adapter, 25); AddRow("备用 IP：", ip, 70); AddRow("子网掩码：", mask, 115); AddRow("默认网关：", gateway, 160);
        startup.SetBounds(112, 201, 180, 25); Controls.Add(startup);
        var cleanBrowser = new Button { Text = "立即清浏览器历史", Left = 112, Top = 232, Width = 200, Height = 30 };
        cleanBrowser.Click += (_, _) => CleanBrowserClicked();
        Controls.Add(cleanBrowser);
        var save = new Button { Text = "保存", Left = 168, Top = 290, Width = 75 };
        var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, Left = 251, Top = 290, Width = 75 };
        var exit = new Button { Text = "强制退出", Left = 334, Top = 290, Width = 80 };
        save.Click += (_, _) => SaveAndClose(); exit.Click += (_, _) => { DialogResult = DialogResult.Abort; Close(); };
        Controls.AddRange([save, cancel, exit]); CancelButton = cancel;
    }

    private void CleanBrowserClicked()
    {
        var confirm = MessageBox.Show(
            "会强制关闭 Chrome / Edge，所有标签页、未保存内容和下载任务都会丢失。\n\n确定继续吗？",
            "立即清浏览器", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
        if (confirm != DialogResult.Yes) return;
        TraceCleaner.CleanBrowserHistoryManual();
        MessageBox.Show("已清理 Chrome / Edge 历史。\n\nCookie / 密码 / 扩展已保留。",
            "完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void AddRow(string label, Control control, int top)
    {
        Controls.Add(new Label { Text = label, Left = 25, Top = top + 5, Width = 85 });
        control.SetBounds(112, top, 285, 28); Controls.Add(control);
    }

    private void SaveAndClose()
    {
        if (adapter.SelectedItem is null || !Valid(ip.Text) || !Valid(mask.Text) || !Valid(gateway.Text))
        { MessageBox.Show("请选择网卡并填写有效的 IPv4 地址。", "设置", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
        Result = new AppSettings { Adapter = adapter.Text, AlternateIp = ip.Text.Trim(), AlternateMask = mask.Text.Trim(), AlternateGateway = gateway.Text.Trim(), StartWithWindows = startup.Checked };
        DialogResult = DialogResult.OK; Close();
    }
    private static bool Valid(string value) => System.Net.IPAddress.TryParse(value, out var a) && a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork;
}
