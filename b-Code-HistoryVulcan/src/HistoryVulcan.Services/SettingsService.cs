using System.Text.Json;
using HistoryVulcan.Core.Storage;

namespace HistoryVulcan.Services;

/// <summary>
/// 应用设置(F-01/F-03):%AppData%/&lt;应用名&gt;/settings.json,扁平键值对。
/// vulcan.app.set / vulcan.app.get 指令与派生应用共用;写入即落盘。
/// </summary>
public sealed class SettingsService : ISettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly object _gate = new();
    private readonly string _filePath;
    private Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    public SettingsService(AppPaths paths)
    {
        _filePath = Path.Combine(paths.Root, "settings.json");
        try
        {
            if (File.Exists(_filePath))
            {
                _values = JsonSerializer.Deserialize<Dictionary<string, string>>(
                              File.ReadAllText(_filePath), JsonOptions)
                          ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                _values = new Dictionary<string, string>(_values, StringComparer.OrdinalIgnoreCase);
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            PreserveUnreadableSettings(ex);
        }
    }

    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    public string? Get(string key)
    {
        lock (_gate)
        {
            return _values.TryGetValue(key, out var v) ? v : null;
        }
    }

    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    public int GetInt(string key, int fallback)
        => int.TryParse(
            Get(key), System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var v)
            ? v
            : fallback;

    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    public void Set(string key, string value)
    {
        lock (_gate)
        {
            var existed = _values.TryGetValue(key, out var previous);
            _values[key] = value;
            try
            {
                Persist();
            }
            catch
            {
                if (existed)
                    _values[key] = previous!;
                else
                    _values.Remove(key);
                throw;
            }
        }
    }

    /// <summary>全部键(vulcan.app.get 不带参数时列出)。</summary>
    public IReadOnlyList<KeyValuePair<string, string>> All()
    {
        lock (_gate)
        {
            return _values.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase).ToList();
        }
    }

    private void Persist()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
        var temporary = _filePath + ".tmp." + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(_values, JsonOptions));
            File.Move(temporary, _filePath, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private void PreserveUnreadableSettings(Exception error)
    {
        var preserved = "";
        if (error is JsonException && File.Exists(_filePath))
        {
            preserved = _filePath + ".corrupt-" + DateTime.Now.ToString(
                "yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
            try
            {
                File.Move(_filePath, preserved, overwrite: false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                preserved = $"(备份失败: {ex.Message})";
            }
        }

        System.Diagnostics.Trace.TraceWarning(
            $"HistoryVulcan 设置文件无法读取，已使用空配置: {_filePath}; {error.Message}" +
            (preserved.Length == 0 ? "" : $"; 原文件: {preserved}"));
    }
}
