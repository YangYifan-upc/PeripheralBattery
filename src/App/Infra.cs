using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace PeripheralBattery;

internal static class Paths
{
    public static string Root => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PeripheralBattery");

    public static string SettingsFile => System.IO.Path.Combine(Root, "settings.json");
    public static string LogDir => System.IO.Path.Combine(Root, "logs");

    public static void Ensure()
    {
        try { Directory.CreateDirectory(Root); } catch { }
        try { Directory.CreateDirectory(LogDir); } catch { }
    }
}

internal static class Log
{
    private static readonly object Gate = new();
    private static string CurrentFile => System.IO.Path.Combine(Paths.LogDir, $"app-{DateTime.Now:yyyyMMdd}.log");

    public static void Info(string message) => Write("INFO ", message);
    public static void Warn(string message) => Write("WARN ", message);
    public static void Error(string message, Exception? ex = null)
        => Write("ERROR", ex == null ? message : $"{message} | {ex.GetType().Name}: {ex.Message}");

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                Paths.Ensure();
                File.AppendAllText(CurrentFile, $"{DateTime.Now:HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}",
                    new UTF8Encoding(false));
            }
        }
        catch { /* 日志失败不能影响主程序 */ }
    }

    public static void Cleanup(int keepDays = 7)
    {
        try
        {
            if (!Directory.Exists(Paths.LogDir)) return;
            foreach (var file in Directory.GetFiles(Paths.LogDir, "app-*.log"))
            {
                if (File.GetLastWriteTime(file) < DateTime.Now.AddDays(-keepDays))
                    File.Delete(file);
            }
        }
        catch { }
    }
}

internal sealed class AppSettings
{
    public int LowThreshold { get; set; } = 20;
    public bool ShowPercentOnIcon { get; set; } = true;
    public bool NotifyOnLowBattery { get; set; } = true;
    public int StaleSeconds { get; set; } = 90;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(Paths.SettingsFile)) return new AppSettings();
            string json = File.ReadAllText(Paths.SettingsFile);
            var s = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
            s.Normalize();
            return s;
        }
        catch (Exception ex)
        {
            TryBackupBroken();
            Log.Warn("配置损坏，已回落默认值: " + ex.Message);
            return new AppSettings();
        }
    }

    private static void TryBackupBroken()
    {
        try
        {
            if (!File.Exists(Paths.SettingsFile)) return;
            string bad = Paths.SettingsFile + ".bad-" + DateTime.Now.ToString("yyyyMMddHHmmss");
            File.Move(Paths.SettingsFile, bad, true);
        }
        catch { }
    }

    public void Normalize()
    {
        LowThreshold = Math.Clamp(LowThreshold, 5, 50);
        StaleSeconds = Math.Clamp(StaleSeconds, 10, 600);
    }

    public void Save()
    {
        try
        {
            Normalize();
            Paths.Ensure();
            string tmp = Paths.SettingsFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, JsonOptions), new UTF8Encoding(false));
            File.Move(tmp, Paths.SettingsFile, true);
        }
        catch (Exception ex)
        {
            Log.Warn("保存配置失败: " + ex.Message);
        }
    }
}

/// <summary>单实例：Mutex 抢占 + 命名管道唤起已有窗口。</summary>
internal sealed class SingleInstance : IDisposable
{
    private const string PipeName = "PeripheralBattery.IPC";
    private Mutex? _mutex;
    private CancellationTokenSource? _cts;
    private bool _owned;

    public event Action? ShowRequested;

    public bool TryAcquire()
    {
        string sid = "";
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            sid = identity.User?.Value ?? "";
        }
        catch { }

        _mutex = new Mutex(true, $"Local\\PeripheralBattery.{sid}", out bool created);
        _owned = created;

        if (created)
        {
            _cts = new CancellationTokenSource();
            _ = Task.Run(() => ListenLoop(_cts.Token));
            return true;
        }

        // 已有实例：让它在 300 毫秒内把面板弹出来
        for (int i = 0; i < 3; i++)
        {
            try
            {
                using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
                client.Connect(300);
                var payload = Encoding.UTF8.GetBytes("SHOW\n");
                client.Write(payload, 0, payload.Length);
                client.Flush();
                return false;
            }
            catch
            {
                Thread.Sleep(200);
            }
        }

        return false;
    }

    private async Task ListenLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1);
                await server.WaitForConnectionAsync(token).ConfigureAwait(false);
                using var reader = new StreamReader(server, Encoding.UTF8);
                string? line = await reader.ReadLineAsync(token).ConfigureAwait(false);
                if (line == "SHOW") ShowRequested?.Invoke();
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Log.Warn("IPC 异常: " + ex.Message);
                await Task.Delay(500, token).ConfigureAwait(false);
            }
        }
    }

    public void Dispose()
    {
        try { _cts?.Cancel(); } catch { }
        try { if (_owned) _mutex?.ReleaseMutex(); } catch { }
        try { _mutex?.Dispose(); } catch { }
    }
}

internal static class AutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "PeripheralBattery";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, false);
            return key?.GetValue(ValueName) is string s && s.Length > 0;
        }
        catch { return false; }
    }

    public static void Set(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, true);
            if (key == null) return;
            if (enabled)
            {
                string exe = Environment.ProcessPath ?? "";
                key.SetValue(ValueName, $"\"{exe}\" --tray");
            }
            else
            {
                key.DeleteValue(ValueName, false);
            }
        }
        catch (Exception ex)
        {
            Log.Warn("设置开机自启失败: " + ex.Message);
        }
    }
}
