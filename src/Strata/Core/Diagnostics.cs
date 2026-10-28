using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace Strata.Core;

public sealed record DiagnosticError(DateTime At, string Component, string ErrorType);

/// <summary>A bounded, deliberately content-free journal for diagnostics. Exception messages and stacks never enter it.</summary>
public sealed class DiagnosticErrorJournal(int capacity = 50)
{
    private readonly List<DiagnosticError> _items = [];
    public int Capacity { get; } = Math.Max(1, capacity);
    public IReadOnlyList<DiagnosticError> Items => _items;

    public void Record(string component, Exception error, DateTime at)
    {
        _items.Add(new DiagnosticError(at, SafeComponent(component), SafeErrorType(error)));
        if (_items.Count > Capacity) _items.RemoveRange(0, _items.Count - Capacity);
    }

    private static string SafeComponent(string value)
    {
        if (value.StartsWith("widget:", StringComparison.Ordinal)) return "widget";
        if (value.StartsWith("automation:", StringComparison.Ordinal)) return "automation";
        return value switch
        {
            "ui-thread" or "fatal" or "performance" or "screen-time" or "media" or "taskbar" or "updates" => value,
            _ => "component",
        };
    }

    private static string SafeErrorType(Exception error) => error.GetType().Namespace is { } ns &&
        (ns.StartsWith("System", StringComparison.Ordinal) || ns.StartsWith("Strata", StringComparison.Ordinal))
        ? new string(error.GetType().Name.Where(char.IsAsciiLetterOrDigit).Take(60).ToArray())
        : "Exception";
}

public sealed record PerformanceSnapshot(
    long Samples,
    double CpuMeanPercent,
    double CpuMaxPercent,
    long PrivateMemoryStartBytes,
    long PrivateMemoryCurrentBytes,
    long PrivateMemoryMaxBytes,
    int HandlesCurrent,
    int HandlesMax,
    int ThreadsCurrent,
    int ThreadsMax);

/// <summary>Low-cost process counters sampled by the existing single app heartbeat; it creates no timer.</summary>
public sealed class PerformanceSampler
{
    private DateTime _lastAt;
    private TimeSpan _lastCpu;
    private double _cpuTotal;
    private long _samples;
    private double _cpuMax;
    private readonly long _memoryStart;
    private long _memoryCurrent;
    private long _memoryMax;
    private int _handlesCurrent, _handlesMax, _threadsCurrent, _threadsMax;

    public PerformanceSampler()
    {
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        _memoryStart = _memoryCurrent = _memoryMax = process.PrivateMemorySize64;
        _handlesCurrent = _handlesMax = process.HandleCount;
        _threadsCurrent = _threadsMax = process.Threads.Count;
        _lastAt = DateTime.UtcNow;
        _lastCpu = process.TotalProcessorTime;
    }

    public void Sample()
    {
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        DateTime now = DateTime.UtcNow;
        TimeSpan cpu = process.TotalProcessorTime;
        double elapsed = (now - _lastAt).TotalSeconds;
        double percent = elapsed <= 0 ? 0 : Math.Max(0, (cpu - _lastCpu).TotalSeconds / elapsed / Environment.ProcessorCount * 100);
        _lastAt = now;
        _lastCpu = cpu;
        Record(percent, process.PrivateMemorySize64, process.HandleCount, process.Threads.Count);
    }

    public void Record(double cpuPercent, long privateMemoryBytes, int handles, int threads)
    {
        _samples++;
        _cpuTotal += Math.Max(0, cpuPercent);
        _cpuMax = Math.Max(_cpuMax, cpuPercent);
        _memoryCurrent = privateMemoryBytes;
        _memoryMax = Math.Max(_memoryMax, privateMemoryBytes);
        _handlesCurrent = handles;
        _handlesMax = Math.Max(_handlesMax, handles);
        _threadsCurrent = threads;
        _threadsMax = Math.Max(_threadsMax, threads);
    }

    public PerformanceSnapshot Snapshot() => new(
        _samples,
        _samples == 0 ? 0 : _cpuTotal / _samples,
        _cpuMax,
        _memoryStart,
        _memoryCurrent,
        _memoryMax,
        _handlesCurrent,
        _handlesMax,
        _threadsCurrent,
        _threadsMax);
}

public sealed record MonitorGeometry(int X, int Y, int Width, int Height, int WorkX, int WorkY, int WorkWidth, int WorkHeight, bool Primary);

public sealed record DiagnosticBundle(
    string Format,
    int FormatVersion,
    DateTime GeneratedAt,
    string Version,
    string ExecutableSha256,
    long ExecutableSizeBytes,
    IReadOnlyDictionary<string, string> ConfigurationSchema,
    string ConfigurationSchemaSha256,
    IReadOnlyList<DiagnosticError> RecentErrors,
    PerformanceSnapshot Performance,
    IReadOnlyList<MonitorGeometry> Monitors);

public static class DiagnosticBundleBuilder
{
    public const string Format = "strata-diagnostics";

    public static string Create(string version, string executablePath, DiagnosticErrorJournal errors,
        PerformanceSnapshot performance, IReadOnlyList<MonitorGeometry> monitors, DateTime generatedAt)
    {
        using var stream = new FileStream(executablePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 1024 * 1024, FileOptions.SequentialScan);
        using var sha = SHA256.Create();
        string executableHash = Convert.ToHexString(sha.ComputeHash(stream));
        var schema = ConfigurationSchema(typeof(AppSettings));
        string schemaHash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(schema)));
        var bundle = new DiagnosticBundle(
            Format,
            1,
            generatedAt,
            version,
            executableHash,
            stream.Length,
            schema,
            schemaHash,
            errors.Items.ToList(),
            performance,
            monitors);
        return JsonSerializer.Serialize(bundle, Storage.Json);
    }

    public static IReadOnlyDictionary<string, string> ConfigurationSchema(Type settingsType) =>
        settingsType.GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(p => p.SetMethod is not null)
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .ToDictionary(p => JsonNamingPolicy.CamelCase.ConvertName(p.Name), p => TypeName(p.PropertyType), StringComparer.Ordinal);

    private static string TypeName(Type type)
    {
        Type value = Nullable.GetUnderlyingType(type) ?? type;
        if (value.IsEnum) return "enum:" + string.Join('|', Enum.GetNames(value));
        if (value == typeof(string)) return "string";
        if (value == typeof(bool)) return "boolean";
        if (value == typeof(int) || value == typeof(long) || value == typeof(double)) return "number";
        if (value == typeof(TimeSpan)) return "duration";
        if (value.IsGenericType && value.GetGenericTypeDefinition() == typeof(List<>)) return "array:" + TypeName(value.GetGenericArguments()[0]);
        return "object:" + value.Name;
    }

    public static IReadOnlyList<MonitorGeometry> CurrentMonitors() =>
        System.Windows.Forms.Screen.AllScreens.Select(screen => new MonitorGeometry(
            screen.Bounds.X, screen.Bounds.Y, screen.Bounds.Width, screen.Bounds.Height,
            screen.WorkingArea.X, screen.WorkingArea.Y, screen.WorkingArea.Width, screen.WorkingArea.Height,
            screen.Primary)).OrderBy(m => m.X).ThenBy(m => m.Y).ToList();
}
