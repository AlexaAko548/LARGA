using System;
using System.Diagnostics;
using System.Threading;

namespace LARGA.SharedCore;

/// <summary>
/// Load timings for the performance audit (PERFORMANCE_REPORT.md, P0). Off unless switched on:
/// ManagerWeb sets <see cref="Enabled"/> from the "Perf:Enabled" setting (on in Development), the mobile
/// app in Debug builds or a Release build made with -p:LargaPerf=true. Each line says what loaded, how long
/// it took and how many Firestore documents it read, so screens can be compared before and after a fix.
/// Nothing personal is logged - only names of screens and collections, times and counts.
/// </summary>
public static class PerfLog
{
    public static bool Enabled { get; set; }

    /// <summary>Where lines go (ILogger on the web, logcat tag LARGA_PERF on the phone).</summary>
    public static Action<string>? Sink { get; set; }

    /// <summary>"Debug" or "Release" - added to each line, since Debug builds are much slower.</summary>
    public static string Build { get; set; } = string.Empty;

    // Documents read on this async flow since the innermost Start() - summed by Reads().
    private static readonly AsyncLocal<Scope?> Current = new();

    /// <summary>Times a load: <c>using var _ = PerfLog.Start("Quick Ledger load");</c></summary>
    public static IDisposable Start(string what) => Enabled ? new Scope(what) : NoOp.Instance;

    /// <summary>Records <paramref name="documents"/> read from <paramref name="collection"/> (one line, and
    /// added to the enclosing Start()'s total).</summary>
    public static void Reads(string collection, int documents)
    {
        if (!Enabled) return;
        for (Scope? scope = Current.Value; scope is not null; scope = scope.Parent)
        {
            Interlocked.Add(ref scope.Documents, documents);
        }
        Write($"  read {collection}: {documents} docs");
    }

    private static void Write(string line)
    {
        try { Sink?.Invoke(string.IsNullOrEmpty(Build) ? line : $"[{Build}] {line}"); }
        catch { /* logging must never break a load */ }
    }

    private sealed class Scope : IDisposable
    {
        private readonly string _what;
        private readonly Stopwatch _watch = Stopwatch.StartNew();
        public readonly Scope? Parent;
        public int Documents;

        public Scope(string what)
        {
            _what = what;
            Parent = Current.Value;
            Current.Value = this;
        }

        public void Dispose()
        {
            _watch.Stop();
            Current.Value = Parent;
            Write($"{_what}: {_watch.ElapsedMilliseconds} ms, {Documents} docs read");
        }
    }

    private sealed class NoOp : IDisposable
    {
        public static readonly NoOp Instance = new();
        public void Dispose() { }
    }
}
