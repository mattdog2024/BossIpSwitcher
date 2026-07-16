using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Microsoft.Win32;

namespace BossIpSwitcher;

/// <summary>
/// 切回固定 IP 时清空本机访问痕迹。所有操作静默失败，避免弹窗引起注意。
/// <para>
/// 浏览器（Chrome / Edge）历史 <b>不在自动清理范围内</b>——它需要强制关闭浏览器进程，
/// 会丢掉所有标签页、下载、未保存内容。由用户在设置界面手动点「立即清浏览器」按钮触发。
/// </para>
/// </summary>
internal static class TraceCleaner
{
    /// <summary>
    /// 切回固定 IP 时自动清：不杀进程就能清的部分。
    /// </summary>
    public static void CleanAll()
    {
        try { CleanRunDialogHistory(); } catch (Exception ex) { Debug.WriteLine($"[TraceCleaner] RunMRU 清理失败：{ex.Message}"); }
        try { CleanMstscHistory(); } catch (Exception ex) { Debug.WriteLine($"[TraceCleaner] mstsc 清理失败：{ex.Message}"); }
        try { CleanExplorerHistory(); } catch (Exception ex) { Debug.WriteLine($"[TraceCleaner] Explorer 清理失败：{ex.Message}"); }
    }

    /// <summary>
    /// 手动清 Chrome / Edge 历史。会强制关闭浏览器，调用前必须经用户确认。
    /// </summary>
    public static void CleanBrowserHistoryManual()
    {
        try { KillBrowserProcesses(); } catch (Exception ex) { Debug.WriteLine($"[TraceCleaner] 杀浏览器进程失败：{ex.Message}"); }
        try { CleanBrowserHistory(isChrome: true); } catch (Exception ex) { Debug.WriteLine($"[TraceCleaner] Chrome 清理失败：{ex.Message}"); }
        try { CleanBrowserHistory(isChrome: false); } catch (Exception ex) { Debug.WriteLine($"[TraceCleaner] Edge 清理失败：{ex.Message}"); }
    }

    private static void KillBrowserProcesses()
    {
        string[] names = ["chrome", "msedge", "MicrosoftEdge", "MicrosoftEdgeCP", "MicrosoftEdgeSHARP", "MSEdge"];
        foreach (var name in names)
        {
            try
            {
                foreach (var p in Process.GetProcessesByName(name))
                {
                    try { p.Kill(); p.WaitForExit(2000); } catch { /* 忽略 */ }
                }
            }
            catch { /* 忽略 */ }
        }
    }

    /// <summary>
    /// 清空 Win+R「运行」对话框历史。
    /// </summary>
    private static void CleanRunDialogHistory()
    {
        using var key = Registry.CurrentUser.OpenSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Explorer\RunMRU", writable: true);
        if (key is null) return;
        foreach (var name in key.GetValueNames())
        {
            if (!string.IsNullOrEmpty(name)) key.DeleteValue(name, throwOnMissingValue: false);
        }
        // MRUList 留空字符串占位，避免某些程序崩溃
        key.SetValue("MRUList", "", RegistryValueKind.String);
    }

    /// <summary>
    /// 精细清空 Chrome/Edge 历史。保留 cookie、密码、扩展等其它数据。
    /// </summary>
    private static void CleanBrowserHistory(bool isChrome)
    {
        var browser = isChrome ? "Google\\Chrome" : "Microsoft\\Edge";
        var userData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            browser, "User Data");
        if (!Directory.Exists(userData)) return;

        foreach (var historyPath in Directory.EnumerateFiles(userData, "History", SearchOption.AllDirectories))
        {
            try
            {
                var csb = new SqliteConnectionStringBuilder
                {
                    DataSource = historyPath,
                    Mode = SqliteOpenMode.ReadWrite,
                };
                using var conn = new SqliteConnection(csb.ConnectionString);
                conn.Open();

                // 这些表在某些 Chrome/Edge 版本里可能不存在，逐条执行
                string[] tables =
                [
                    "urls", "visits", "visit_source", "typed_count",
                    "keyword_search_terms", "segments", "search_engines",
                    "search_terms", "clusters", "cluster_keywords",
                ];
                foreach (var t in tables)
                {
                    try
                    {
                        using var cmd = conn.CreateCommand();
                        cmd.CommandText = $"DELETE FROM {t};";
                        cmd.ExecuteNonQuery();
                    }
                    catch { /* 表可能不存在 */ }
                }

                try
                {
                    using var vacuum = conn.CreateCommand();
                    vacuum.CommandText = "VACUUM;";
                    vacuum.ExecuteNonQuery();
                }
                catch { /* VACUUM 在某些锁状态下可能失败，忽略 */ }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[TraceCleaner] {Path.GetFileName(historyPath)} 清理失败：{ex.Message}");
            }
        }
    }

    /// <summary>
    /// 清空远程桌面 (mstsc) 历史连接。
    /// </summary>
    private static void CleanMstscHistory()
    {
        // HKCU\Software\Microsoft\Terminal Server Client\Default 下的 MRU0~MRU9
        using (var key = Registry.CurrentUser.OpenSubKey(
            @"Software\Microsoft\Terminal Server Client\Default", writable: true))
        {
            if (key is not null)
            {
                foreach (var name in key.GetValueNames())
                {
                    if (name.StartsWith("MRU", StringComparison.OrdinalIgnoreCase))
                        key.DeleteValue(name, throwOnMissingValue: false);
                }
                key.SetValue("MRU0", "", RegistryValueKind.String);
            }
        }

        // HKCU\Software\Microsoft\Terminal Server Client\Servers 下的每个连接子键
        using (var key = Registry.CurrentUser.OpenSubKey(
            @"Software\Microsoft\Terminal Server Client\Servers", writable: true))
        {
            if (key is not null)
            {
                foreach (var sub in key.GetSubKeyNames())
                {
                    try { key.DeleteSubKeyTree(sub, throwOnMissingSubKey: false); } catch { }
                }
            }
        }

        // 用户文档下的 Default.rdp
        try
        {
            var rdp = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Documents", "Default.rdp");
            if (File.Exists(rdp)) File.Delete(rdp);
        }
        catch { }
    }

    /// <summary>
    /// 清空资源管理器 / 快速访问 / 跳转列表里的访问痕迹。
    /// </summary>
    private static void CleanExplorerHistory()
    {
        // RecentDocs（含所有扩展名子键 + .lnk 历史）
        ClearSubKeysAndValues(@"Software\Microsoft\Windows\CurrentVersion\Explorer\RecentDocs");
        // 地址栏输入历史
        ClearValues(@"Software\Microsoft\Windows\CurrentVersion\Explorer\TypedPaths");
        // 文件打开/保存对话框历史
        ClearSubKeysAndValues(@"Software\Microsoft\Windows\CurrentVersion\Explorer\ComDlg32\OpenSavePidlMRU");
        ClearSubKeysAndValues(@"Software\Microsoft\Windows\CurrentVersion\Explorer\ComDlg32\LastVisitedPidlMRU");
        // 最近访问的 Office 文档（Word/Excel/PowerPoint 通用）
        ClearSubKeysAndValues(@"Software\Microsoft\Office\16.0\Word\User MRU");
        ClearSubKeysAndValues(@"Software\Microsoft\Office\16.0\Word\Place MRU");
        ClearSubKeysAndValues(@"Software\Microsoft\Office\16.0\Excel\User MRU");
        ClearSubKeysAndValues(@"Software\Microsoft\Office\16.0\Excel\Place MRU");

        // 跳转列表（任务栏右键"最近"、开始菜单"最近添加"）
        var recentDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Microsoft", "Windows", "Recent");
        if (Directory.Exists(recentDir))
        {
            foreach (var pattern in new[] { "*.automaticDestinations-ms", "*.customDestinations-ms" })
            {
                foreach (var file in Directory.EnumerateFiles(recentDir, pattern))
                {
                    try { File.Delete(file); } catch { }
                }
            }
        }
    }

    private static void ClearValues(string subKey)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(subKey, writable: true);
            if (key is null) return;
            foreach (var name in key.GetValueNames())
            {
                if (!string.IsNullOrEmpty(name)) key.DeleteValue(name, throwOnMissingValue: false);
            }
        }
        catch { }
    }

    private static void ClearSubKeysAndValues(string subKey)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(subKey, writable: true);
            if (key is null) return;
            foreach (var sub in key.GetSubKeyNames())
            {
                try { key.DeleteSubKeyTree(sub, throwOnMissingSubKey: false); } catch { }
            }
            foreach (var name in key.GetValueNames())
            {
                if (!string.IsNullOrEmpty(name)) key.DeleteValue(name, throwOnMissingValue: false);
            }
        }
        catch { }
    }
}
