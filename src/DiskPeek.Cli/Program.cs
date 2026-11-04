using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using DiskPeek.Core;

return await Cli.RunAsync(args);

internal static class Cli
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    public static async Task<int> RunAsync(string[] args)
    {
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        try
        {
            if (args.Length == 0 || args[0] is "help" or "--help" or "-h") { Help(); return 0; }
            var options = ParseOptions(args.Skip(args[0] == "synthetic" ? 1 : 2).ToArray());
            return args[0] switch
            {
                "scan" when args.Length >= 2 => await ScanAsync(args[1], options, cts.Token),
                "benchmark" when args.Length >= 2 => await BenchmarkAsync(args[1], options, cts.Token),
                "sample" when args.Length >= 2 => await SampleAsync(args[1], options, cts.Token),
                "synthetic" => await SyntheticAsync(options, cts.Token),
                _ => throw new ArgumentException("Unknown command or missing target. Run --help for usage.")
            };
        }
        catch (OperationCanceledException) { Console.Error.WriteLine("Cancelled."); return 2; }
        catch (Exception ex) { Console.Error.WriteLine($"DiskPeek: {ex.Message}"); return 1; }
    }

    private static async Task<int> ScanAsync(string target, Dictionary<string, string> options, CancellationToken ct)
    {
        ValidateOptions(options, "engine", "metric", "csv", "workers");
        var metric = ParseMetric(Value(options, "metric", "allocated"));
        var engine = ParseEngine(Value(options, "engine", "auto"));
        var report = new InlineProgress<ScanProgress>(p => Console.Error.Write($"\r{p.Engine}: {p.Entries:N0} entries, {p.Elapsed.TotalSeconds:N1}s — {p.Phase}          "));
        var result = await ScanService.ScanAsync(new(target, engine, IntValue(options, "workers", 0, 0, 16)), report, ct);
        Console.Error.WriteLine();
        Console.WriteLine($"{result.Target}\nEngine: {result.Engine}\nEntries: {result.Count:N0}\nLogical: {FormatUtil.Bytes(result.LogicalTotals[0])}\nAllocated file data: {FormatUtil.Bytes(result.AllocatedTotals[0])}\nUnmeasured files: {result.UnknownCounts[0]:N0}\nElapsed: {result.Elapsed.TotalSeconds:N3}s\nStatus: {(result.Cancelled ? "cancelled / partial" : result.Partial ? "partial" : "complete")}");
        Console.WriteLine("Largest files:");
        foreach (var index in Enumerable.Range(1, result.Count - 1).Where(i => !result.Nodes[i].IsDirectory).OrderByDescending(i => result.Value(i, metric)).Take(20))
            Console.WriteLine($"{FormatUtil.Bytes(result.Value(index, metric)),14}  {result.GetPath(index)}");
        foreach (var issue in result.Issues.Take(10)) Console.Error.WriteLine($"Skipped: {issue.Path}: {issue.Message}");
        if (options.TryGetValue("csv", out var csv)) { await CsvExporter.ExportAsync(result, csv, ct); Console.WriteLine($"CSV: {Path.GetFullPath(csv)}"); }
        Console.WriteLine("File metrics cover the unnamed data stream only. Alternate streams and filesystem overhead are excluded; volume use is separate.");
        return result.Cancelled ? 2 : 0;
    }

    private static async Task<int> SampleAsync(string target, Dictionary<string, string> options, CancellationToken ct)
    {
        ValidateOptions(options, "engine", "output");
        var engine = ParseEngine(Value(options, "engine", "directory"));
        var sample = await MeasureAsync(target, engine, ct);
        await WriteJsonAsync(Require(options, "output"), sample, ct);
        return 0;
    }

    private static async Task<ScanSample> MeasureAsync(string target, ScanEngineKind engine, CancellationToken ct)
    {
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        var initialMemory = process.WorkingSet64;
        var initialCpu = process.TotalProcessorTime;
        var initialAllocation = GC.GetTotalAllocatedBytes();
        var clock = Stopwatch.StartNew();
        double? firstResult = null;
        var result = await ScanService.ScanAsync(new(target, engine), new InlineProgress<ScanProgress>(p => { if (p.Entries > 1) firstResult ??= clock.Elapsed.TotalMilliseconds; }), ct);
        var elapsed = clock.Elapsed.TotalMilliseconds;
        process.Refresh();
        return new(result.Engine, result.Count, result.LogicalTotals[0], result.AllocatedTotals[0], result.UnknownCounts[0], result.Partial, result.Cancelled,
            elapsed, firstResult, (process.TotalProcessorTime - initialCpu).TotalMilliseconds,
            initialMemory, process.WorkingSet64, process.PeakWorkingSet64, GC.GetTotalAllocatedBytes() - initialAllocation);
    }

    private static async Task<int> BenchmarkAsync(string target, Dictionary<string, string> options, CancellationToken ct)
    {
        ValidateOptions(options, "runs", "output", "cache-state", "engine");
        target = Path.GetFullPath(target);
        var runs = IntValue(options, "runs", 3, 1, 20);
        var output = Path.GetFullPath(Value(options, "output", "diskpeek-benchmark.json"));
        var optimized = ParseEngine(Value(options, "engine", "directory"));
        if (optimized == ScanEngineKind.Baseline) throw new ArgumentException("Benchmark --engine must be directory, mft, or auto.");
        var cache = Value(options, "cache-state", "warm");
        if (cache is not "warm" and not "external-cold") throw new ArgumentException("--cache-state must be warm or external-cold.");
        if (cache == "external-cold" && runs != 1) throw new ArgumentException("External cold-cache samples require --runs 1 and an externally reset cache before each engine; use separate invocations for rigorous cold runs.");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        if (cache == "warm")
        {
            Console.WriteLine("Warming both engines (not measured)…");
            await IsolatedSampleAsync(target, ScanEngineKind.Baseline, output, ct);
            await IsolatedSampleAsync(target, optimized, output, ct);
        }
        var baseline = new List<ScanSample>();
        var candidate = new List<ScanSample>();
        for (int run = 0; run < runs; run++)
        {
            ct.ThrowIfCancellationRequested();
            // Alternate order to reduce systematic cache/order bias.
            foreach (var engine in run % 2 == 0 ? new[] { ScanEngineKind.Baseline, optimized } : new[] { optimized, ScanEngineKind.Baseline })
            {
                var sample = await IsolatedSampleAsync(target, engine, output, ct);
                (engine == ScanEngineKind.Baseline ? baseline : candidate).Add(sample);
                Console.WriteLine($"Run {run + 1}: {sample.Engine}, {sample.Entries:N0} entries, {sample.TotalMilliseconds:N1} ms, peak {FormatUtil.Bytes(sample.PeakWorkingSetBytes)}");
            }
        }
        var matched = baseline.Concat(candidate).Select(s => (s.Entries, s.LogicalBytes, s.AllocatedBytes, s.UnknownFiles)).Distinct().Count() == 1;
        var baselineMedian = Median(baseline.Select(s => s.TotalMilliseconds));
        var candidateMedian = Median(candidate.Select(s => s.TotalMilliseconds));
        var cancellation = await MeasureCancellationAsync(target, optimized);
        var result = new
        {
            GeneratedUtc = DateTime.UtcNow, Target = target, CacheState = cache,
            CacheNote = cache == "warm" ? "Both engines warmed before measured runs. Every sample uses a fresh process. OS cache was not flushed." : "Cache reset is operator-managed. This tool does not reset Windows caches; back-to-back engines cannot both be assumed cold.",
            Accounting = "Unnamed file data; hard-link allocation counted once per scope; unmeasured allocations excluded. Same scopes and current process privileges.",
            Machine = Environment.MachineName, OS = Environment.OSVersion.ToString(), Runtime = Environment.Version.ToString(), ProcessorCount = Environment.ProcessorCount,
            MatchedResults = matched, BaselineMedianMilliseconds = baselineMedian, CandidateMedianMilliseconds = candidateMedian,
            ObservedSpeedRatio = matched && candidateMedian > 0 ? baselineMedian / candidateMedian : (double?)null,
            Cancellation = cancellation, Baseline = baseline, Candidate = candidate
        };
        await WriteJsonAsync(output, result, ct);
        Console.WriteLine($"Matched totals: {matched}; baseline median {baselineMedian:N1} ms; candidate median {candidateMedian:N1} ms.");
        if (matched) Console.WriteLine($"Observed ratio: {baselineMedian / candidateMedian:N2}× (specific to this fixture and cache state).");
        Console.WriteLine($"Report: {output}");
        return matched ? 0 : 3;
    }

    private static async Task<ScanSample> IsolatedSampleAsync(string target, ScanEngineKind engine, string reportPath, CancellationToken ct)
    {
        var temporary = Path.Combine(Path.GetDirectoryName(reportPath)!, ".diskpeek-sample-" + Guid.NewGuid().ToString("N") + ".json");
        var info = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        if (Path.GetFileNameWithoutExtension(info.FileName).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) info.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        foreach (var argument in new[] { "sample", target, "--engine", engine.ToString().ToLowerInvariant(), "--output", temporary }) info.ArgumentList.Add(argument);
        try
        {
            using var child = Process.Start(info) ?? throw new IOException("Unable to start isolated benchmark process.");
            var stdout = child.StandardOutput.ReadToEndAsync(ct);
            var stderr = child.StandardError.ReadToEndAsync(ct);
            try { await child.WaitForExitAsync(ct); }
            catch { if (!child.HasExited) child.Kill(entireProcessTree: true); throw; }
            if (child.ExitCode != 0) throw new IOException($"Benchmark sample failed: {await stderr} {await stdout}");
            return JsonSerializer.Deserialize<ScanSample>(await File.ReadAllTextAsync(temporary, ct), JsonOptions) ?? throw new InvalidDataException("Empty benchmark sample.");
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static async Task<object> MeasureCancellationAsync(string target, ScanEngineKind engine)
    {
        using var cancellation = new CancellationTokenSource();
        using var started = new ManualResetEventSlim();
        long requestedAt = 0;
        var stopper = new Thread(() =>
        {
            started.Wait();
            // Sleep's Windows timer quantum can exceed the entire warmed scan.
            // One millisecond on a dedicated thread keeps the request independent
            // of both timer granularity and the scanner's ThreadPool workers.
            long start = Stopwatch.GetTimestamp();
            while (Stopwatch.GetElapsedTime(start).TotalMilliseconds < 1) Thread.SpinWait(32);
            Interlocked.Exchange(ref requestedAt, Stopwatch.GetTimestamp());
            cancellation.Cancel();
        }) { IsBackground = true };
        stopper.Start();
        var task = ScanService.ScanAsync(new(target, engine), null, cancellation.Token);
        started.Set();
        try
        {
            var result = await task;
            long completedAt = Stopwatch.GetTimestamp();
            stopper.Join();
            return new { RequestedAfterMilliseconds = 1, ObservedLatencyMilliseconds = result.Cancelled ? (double?)Stopwatch.GetElapsedTime(Interlocked.Read(ref requestedAt), completedAt).TotalMilliseconds : null, ScanAlreadyCompleted = !result.Cancelled, PartialRetained = result.Cancelled && result.Count > 0 };
        }
        catch (OperationCanceledException)
        {
            long completedAt = Stopwatch.GetTimestamp(); stopper.Join();
            return new { RequestedAfterMilliseconds = 1, ObservedLatencyMilliseconds = (double?)Stopwatch.GetElapsedTime(Interlocked.Read(ref requestedAt), completedAt).TotalMilliseconds, ScanAlreadyCompleted = false, PartialRetained = false };
        }
        finally { stopper.Join(); }
    }

    private static async Task<int> SyntheticAsync(Dictionary<string, string> options, CancellationToken ct)
    {
        ValidateOptions(options, "count", "output");
        var count = IntValue(options, "count", 1_000_000, 1, 10_000_000);
        var clock = Stopwatch.StartNew();
        var allocationStart = GC.GetTotalAllocatedBytes();
        var nodes = new ScanNode[count + 1];
        nodes[0] = new() { Name = "Synthetic", ParentIndex = -1, Attributes = FileAttributes.Directory, Identity = new(1), AllocatedBytes = 0 };
        for (int i = 1; i <= count; i++)
        {
            if ((i & 4095) == 0) ct.ThrowIfCancellationRequested();
            nodes[i] = new() { Name = $"file-{i:D7}.{(i % 3 == 0 ? "log" : "bin")}", ParentIndex = 0, Identity = new((ulong)i + 1), LogicalBytes = 100 + i % 65536, AllocatedBytes = ((100 + i % 65536 + 4095) / 4096) * 4096, ModifiedUtcTicks = DateTime.UnixEpoch.Ticks };
        }
        var build = clock.Elapsed.TotalMilliseconds;
        var result = new ScanResult { Target = @"C:\Synthetic", Nodes = nodes, Engine = "Synthetic model" };
        clock.Restart();
        ScanAggregation.Complete(result);
        var aggregate = clock.Elapsed.TotalMilliseconds;
        clock.Restart();
        var matches = 0;
        for (int i = 1; i <= count; i++) if (nodes[i].Name.Contains("999", StringComparison.OrdinalIgnoreCase)) matches++;
        var search = clock.Elapsed.TotalMilliseconds;
        clock.Restart();
        var largest = new PriorityQueue<int, long>();
        for (int i = 1; i <= count; i++)
        {
            if (largest.Count < 1000) largest.Enqueue(i, result.AllocatedTotals[i]);
            else if (largest.TryPeek(out _, out var minimum) && result.AllocatedTotals[i] > minimum) { largest.Dequeue(); largest.Enqueue(i, result.AllocatedTotals[i]); }
        }
        var top = clock.Elapsed.TotalMilliseconds;
        using var process = Process.GetCurrentProcess();
        var report = new
        {
            GeneratedUtc = DateTime.UtcNow, Entries = result.Count, Files = result.FileCounts[0],
            BuildMilliseconds = build, AggregateMilliseconds = aggregate, SearchMilliseconds = search, SearchMatches = matches,
            TopThousandMilliseconds = top, AllocatedManagedBytes = GC.GetTotalAllocatedBytes() - allocationStart,
            RetainedManagedBytes = GC.GetTotalMemory(true), WorkingSetBytes = process.WorkingSet64, PeakWorkingSetBytes = process.PeakWorkingSet64,
            Note = "Synthetic compact-model benchmark, not filesystem I/O or UI frame timing. Flat one-million-entry directory stresses aggregation and result queries."
        };
        var json = JsonSerializer.Serialize(report, JsonOptions);
        Console.WriteLine(json);
        if (options.TryGetValue("output", out var output)) await WriteJsonAsync(output, report, ct);
        GC.KeepAlive(result);
        return 0;
    }

    private static async Task WriteJsonAsync<T>(string path, T value, CancellationToken ct)
    {
        var absolute = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
        await File.WriteAllTextAsync(absolute, JsonSerializer.Serialize(value, JsonOptions), ct);
    }
    private static Dictionary<string, string> ParseOptions(string[] args)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Length; i += 2)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal) || i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException($"Expected --option value, got '{args[i]}'.");
            if (!result.TryAdd(args[i][2..], args[i + 1])) throw new ArgumentException($"Duplicate option '{args[i]}'.");
        }
        return result;
    }
    private static void ValidateOptions(Dictionary<string, string> options, params string[] allowed)
    { foreach (var name in options.Keys) if (!allowed.Contains(name, StringComparer.OrdinalIgnoreCase)) throw new ArgumentException($"Unknown option --{name}."); }
    private static string Value(Dictionary<string, string> options, string key, string fallback) => options.GetValueOrDefault(key, fallback);
    private static string Require(Dictionary<string, string> options, string key) => options.TryGetValue(key, out var value) ? value : throw new ArgumentException($"Missing --{key}.");
    private static int IntValue(Dictionary<string, string> options, string key, int fallback, int minimum, int maximum)
    {
        if (!options.TryGetValue(key, out var text)) return fallback;
        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value >= minimum && value <= maximum ? value : throw new ArgumentException($"--{key} must be {minimum}…{maximum}.");
    }
    private static ScanEngineKind ParseEngine(string text) => Enum.TryParse<ScanEngineKind>(text, true, out var engine) && Enum.IsDefined(engine) ? engine : throw new ArgumentException("Engine must be auto, directory, mft, or baseline.");
    private static SizeMetric ParseMetric(string text) => Enum.TryParse<SizeMetric>(text, true, out var metric) && Enum.IsDefined(metric) ? metric : throw new ArgumentException("Metric must be allocated or logical.");
    private static double Median(IEnumerable<double> source) { var values = source.Order().ToArray(); return values.Length % 2 == 0 ? (values[values.Length / 2 - 1] + values[values.Length / 2]) / 2 : values[values.Length / 2]; }
    private static void Help() => Console.WriteLine($"""
DiskPeek CLI {typeof(Cli).Assembly.GetName().Version!.ToString(3)} — AGPL-3.0-or-later
  scan <path> [--engine auto|directory|mft|baseline] [--metric allocated|logical]
              [--csv file.csv] [--workers 0..16]
  benchmark <path> [--runs 3] [--engine directory|mft|auto]
                   [--cache-state warm|external-cold] [--output results.json]
  synthetic [--count 1000000] [--output results.json]

MFT scans require an elevated console and an NTFS volume root. The GUI offers optional UAC.
Warm benchmarks launch each measured sample in a fresh process and alternate engine order.
Cold-cache reset is external; this tool never flushes system caches.
Metrics cover unnamed file data, not alternate streams or filesystem overhead.
Ctrl+C cancels and retains available partial scan results. Exit codes: 0 success, 1 error,
2 cancelled, 3 benchmark results differ (changing filesystem or accounting mismatch).
""");

    private sealed class InlineProgress<T>(Action<T> action) : IProgress<T> { public void Report(T value) => action(value); }
    private sealed record ScanSample(string Engine, int Entries, long LogicalBytes, long AllocatedBytes, int UnknownFiles, bool Partial, bool Cancelled,
        double TotalMilliseconds, double? FirstMetadataMilliseconds, double CpuMilliseconds,
        long InitialWorkingSetBytes, long FinalWorkingSetBytes, long PeakWorkingSetBytes, long ManagedAllocatedBytes);
}
