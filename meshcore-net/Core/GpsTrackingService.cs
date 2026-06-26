using System.Globalization;
using System.IO.Ports;
using System.Text.Json;

namespace MeshCoreNet;

public enum GpsTrackingMode
{
    Average,
    Roaming
}

public sealed record GpsTrackingOptions(
    string SerialDevice,
    int BaudRate,
    int SampleIntervalSeconds,
    int RetentionDays,
    string HistoryFilePath,
    string StateFilePath,
    GpsTrackingMode Mode);

public sealed class GpsTrackingService
{
    private const int CompactEverySamples = 256;

    private readonly GpsTrackingOptions _options;
    private readonly IReadOnlyList<SelfIdentity> _identities;
    private readonly List<GpsFix> _history = [];
    private readonly object _sync = new();
    private readonly int _maxSamples;
    private DateTimeOffset _lastRecorded = DateTimeOffset.MinValue;
    private int _samplesSinceCompact;

    public GpsTrackingService(GpsTrackingOptions options, IReadOnlyList<SelfIdentity> identities)
    {
        _options = options;
        _identities = identities;
        var sampleInterval = Math.Max(1, _options.SampleIntervalSeconds);
        var retentionDays = Math.Max(1, _options.RetentionDays);
        _maxSamples = Math.Max(1, (int)Math.Ceiling(retentionDays * 24d * 60d * 60d / sampleInterval));
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        EnsureDirectories();
        LoadHistory();
        await WriteStateAsync(BuildSnapshot(), cancellationToken).ConfigureAwait(false);

        Console.WriteLine($"GPS tracking enabled on {_options.SerialDevice} ({_options.Mode}, {_options.SampleIntervalSeconds}s interval, {_options.RetentionDays}d retention).");

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using var serial = CreatePort();
                serial.Open();
                Console.WriteLine($"GPS serial connected: {_options.SerialDevice} @ {_options.BaudRate}");
                await ReadLoopAsync(serial, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"GPS tracking warning: {ex.Message}");
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }
    }

    private SerialPort CreatePort()
    {
        return new SerialPort(_options.SerialDevice, _options.BaudRate)
        {
            NewLine = "\n",
            ReadTimeout = 2000,
            DtrEnable = false,
            RtsEnable = false
        };
    }

    private async Task ReadLoopAsync(SerialPort serial, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            string? sentence;
            try
            {
                sentence = serial.ReadLine();
            }
            catch (TimeoutException)
            {
                continue;
            }

            if (!GpsNmeaParser.TryParse(sentence, out var fix) || !fix.IsValid)
            {
                continue;
            }

            TrackingUpdate update;
            lock (_sync)
            {
                if ((_lastRecorded + TimeSpan.FromSeconds(Math.Max(1, _options.SampleIntervalSeconds))) > fix.Timestamp)
                {
                    continue;
                }

                _history.Add(fix);
                _lastRecorded = fix.Timestamp;
                var compact = PruneHistoryLocked(fix.Timestamp);
                _samplesSinceCompact++;
                if (_samplesSinceCompact >= CompactEverySamples)
                {
                    compact = true;
                }

                IReadOnlyList<GpsFix>? compactData = null;
                if (compact)
                {
                    _samplesSinceCompact = 0;
                    compactData = _history.ToArray();
                }

                var snapshot = BuildSnapshotLocked();
                update = new TrackingUpdate(fix, snapshot, compactData);
            }

            ApplyPosition(update.Snapshot);
            await AppendHistoryAsync(update.Fix, cancellationToken).ConfigureAwait(false);
            if (update.CompactData is not null)
            {
                await RewriteHistoryAsync(update.CompactData, cancellationToken).ConfigureAwait(false);
            }

            await WriteStateAsync(update.Snapshot, cancellationToken).ConfigureAwait(false);
        }
    }

    private void EnsureDirectories()
    {
        var historyDir = Path.GetDirectoryName(_options.HistoryFilePath);
        if (!string.IsNullOrWhiteSpace(historyDir))
        {
            Directory.CreateDirectory(historyDir);
        }

        var stateDir = Path.GetDirectoryName(_options.StateFilePath);
        if (!string.IsNullOrWhiteSpace(stateDir))
        {
            Directory.CreateDirectory(stateDir);
        }
    }

    private void LoadHistory()
    {
        if (!File.Exists(_options.HistoryFilePath))
        {
            return;
        }

        foreach (var line in File.ReadLines(_options.HistoryFilePath))
        {
            if (TryParseHistoryLine(line, out var fix))
            {
                _history.Add(fix);
            }
        }

        if (_history.Count == 0)
        {
            return;
        }

        _lastRecorded = _history[^1].Timestamp;
        var compact = PruneHistoryLocked(DateTimeOffset.UtcNow);
        var snapshot = BuildSnapshotLocked();
        ApplyPosition(snapshot);
        if (compact)
        {
            File.WriteAllLines(_options.HistoryFilePath, _history.Select(FormatHistoryLine));
        }
    }

    private bool PruneHistoryLocked(DateTimeOffset now)
    {
        var compact = false;
        var cutoff = now - TimeSpan.FromDays(Math.Max(1, _options.RetentionDays));
        while (_history.Count > 0 && _history[0].Timestamp < cutoff)
        {
            _history.RemoveAt(0);
            compact = true;
        }

        while (_history.Count > _maxSamples)
        {
            _history.RemoveAt(0);
            compact = true;
        }

        return compact;
    }

    private TrackingSnapshot BuildSnapshot()
    {
        lock (_sync)
        {
            return BuildSnapshotLocked();
        }
    }

    private TrackingSnapshot BuildSnapshotLocked()
    {
        var latest = _history.Count == 0 ? null : _history[^1];
        (double Latitude, double Longitude)? average = null;
        if (_history.Count > 0)
        {
            average =
            (
                _history.Average(fix => fix.Latitude),
                _history.Average(fix => fix.Longitude)
            );
        }

        (double Latitude, double Longitude)? active = _options.Mode switch
        {
            GpsTrackingMode.Average => average,
            GpsTrackingMode.Roaming => latest is null ? null : (latest.Latitude, latest.Longitude),
            _ => average
        };

        return new TrackingSnapshot(
            _options.Mode,
            _history.Count,
            latest,
            average,
            active);
    }

    private void ApplyPosition(TrackingSnapshot snapshot)
    {
        if (snapshot.ActivePosition is null)
        {
            return;
        }

        var valid = MeshUtilities.ValidateLatLon(snapshot.ActivePosition.Value.Latitude, snapshot.ActivePosition.Value.Longitude);
        foreach (var identity in _identities)
        {
            identity.LatLon = valid;
        }
    }

    private async Task AppendHistoryAsync(GpsFix fix, CancellationToken cancellationToken)
    {
        await File.AppendAllTextAsync(_options.HistoryFilePath, FormatHistoryLine(fix) + Environment.NewLine, cancellationToken).ConfigureAwait(false);
    }

    private async Task RewriteHistoryAsync(IReadOnlyList<GpsFix> history, CancellationToken cancellationToken)
    {
        var text = string.Join(Environment.NewLine, history.Select(FormatHistoryLine));
        await File.WriteAllTextAsync(_options.HistoryFilePath, text + Environment.NewLine, cancellationToken).ConfigureAwait(false);
    }

    private async Task WriteStateAsync(TrackingSnapshot snapshot, CancellationToken cancellationToken)
    {
        var payload = new
        {
            mode = snapshot.Mode.ToString().ToLowerInvariant(),
            samples = snapshot.SampleCount,
            latest = snapshot.Latest is null
                ? null
                : new
                {
                    latitude = snapshot.Latest.Latitude,
                    longitude = snapshot.Latest.Longitude,
                    timestamp = snapshot.Latest.Timestamp
                },
            average = snapshot.AveragePosition is null
                ? null
                : new
                {
                    latitude = snapshot.AveragePosition.Value.Latitude,
                    longitude = snapshot.AveragePosition.Value.Longitude
                },
            active = snapshot.ActivePosition is null
                ? null
                : new
                {
                    latitude = snapshot.ActivePosition.Value.Latitude,
                    longitude = snapshot.ActivePosition.Value.Longitude
                }
        };

        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(_options.StateFilePath, json, cancellationToken).ConfigureAwait(false);
    }

    private static string FormatHistoryLine(GpsFix fix)
    {
        return string.Create(CultureInfo.InvariantCulture, $"{fix.Timestamp.ToUnixTimeSeconds()},{fix.Latitude:F8},{fix.Longitude:F8}");
    }

    private static bool TryParseHistoryLine(string? line, out GpsFix fix)
    {
        fix = default!;
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        var parts = line.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length != 3)
        {
            return false;
        }

        if (!long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var unixSeconds) ||
            !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var latitude) ||
            !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var longitude))
        {
            return false;
        }

        var validated = MeshUtilities.ValidateLatLon(latitude, longitude);
        fix = new GpsFix(validated.Latitude, validated.Longitude, true, DateTimeOffset.FromUnixTimeSeconds(unixSeconds));
        return true;
    }

    private sealed record TrackingSnapshot(
        GpsTrackingMode Mode,
        int SampleCount,
        GpsFix? Latest,
        (double Latitude, double Longitude)? AveragePosition,
        (double Latitude, double Longitude)? ActivePosition);

    private sealed record TrackingUpdate(GpsFix Fix, TrackingSnapshot Snapshot, IReadOnlyList<GpsFix>? CompactData);
}