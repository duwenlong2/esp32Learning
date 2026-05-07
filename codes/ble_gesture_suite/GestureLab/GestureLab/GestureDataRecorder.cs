using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace GestureLab;

public sealed class GestureDataRecorder : IDisposable
{
    private const int FlushEventBatchSize = 50;
    private static readonly TimeSpan FlushInterval = TimeSpan.FromMilliseconds(500);

    private readonly object _sync = new();
    private StreamWriter? _writer;
    private string? _currentFilePath;
    private int _sequence;
    private int _eventsSinceFlush;
    private DateTime _lastFlushUtc = DateTime.UtcNow;

    public bool IsRecording
    {
        get
        {
            lock (_sync)
            {
                return _writer is not null;
            }
        }
    }

    public string? CurrentFilePath
    {
        get
        {
            lock (_sync)
            {
                return _currentFilePath;
            }
        }
    }

    public string StartSession(string sessionLabel)
    {
        lock (_sync)
        {
            StopSessionInternal();

            string baseFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "GestureLab",
                "records");
            Directory.CreateDirectory(baseFolder);

            string safeLabel = MakeSafeFilePart(string.IsNullOrWhiteSpace(sessionLabel) ? "session" : sessionLabel.Trim());
            string fileName = $"{DateTime.Now:yyyyMMdd_HHmmss}_{safeLabel}.csv";
            string path = Path.Combine(baseFolder, fileName);

            _writer = new StreamWriter(path, append: false, new UTF8Encoding(false));
            _currentFilePath = path;
            _sequence = 0;
            _eventsSinceFlush = 0;
            _lastFlushUtc = DateTime.UtcNow;

            _writer.WriteLine("utc_time,seq,event,raw_payload,ax,ay,az,gx,gy,gz,temp,roll,pitch,yaw,note");
            _writer.Flush();

            WriteEventInternal("SESSION_START", string.Empty, null, null, null, null, null, null, null, null, null, null, sessionLabel);
            return path;
        }
    }

    public void StopSession()
    {
        lock (_sync)
        {
            WriteEventInternal("SESSION_STOP", string.Empty, null, null, null, null, null, null, null, null, null, null, string.Empty);
            StopSessionInternal();
        }
    }

    public void LogStatus(string payload)
    {
        lock (_sync)
        {
            WriteEventInternal("STATUS", payload, null, null, null, null, null, null, null, null, null, null, string.Empty);
        }
    }

    public void LogMarker(string note)
    {
        lock (_sync)
        {
            WriteEventInternal("MARK", string.Empty, null, null, null, null, null, null, null, null, null, null, note);
        }
    }

    public void LogImu(
        string payload,
        int? ax,
        int? ay,
        int? az,
        int? gx,
        int? gy,
        int? gz,
        int? temp,
        double? roll,
        double? pitch,
        double? yaw,
        string note)
    {
        lock (_sync)
        {
            WriteEventInternal("IMU", payload, ax, ay, az, gx, gy, gz, temp, roll, pitch, yaw, note);
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            StopSessionInternal();
        }
    }

    private void WriteEventInternal(
        string eventType,
        string rawPayload,
        int? ax,
        int? ay,
        int? az,
        int? gx,
        int? gy,
        int? gz,
        int? temp,
        double? roll,
        double? pitch,
        double? yaw,
        string note)
    {
        if (_writer is null)
        {
            return;
        }

        string timestamp = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        _sequence++;

        string line = string.Join(",",
            Csv(timestamp),
            Csv(_sequence.ToString(CultureInfo.InvariantCulture)),
            Csv(eventType),
            Csv(rawPayload),
            Csv(ToText(ax)),
            Csv(ToText(ay)),
            Csv(ToText(az)),
            Csv(ToText(gx)),
            Csv(ToText(gy)),
            Csv(ToText(gz)),
            Csv(ToText(temp)),
            Csv(ToText(roll)),
            Csv(ToText(pitch)),
            Csv(ToText(yaw)),
            Csv(note));

        _writer.WriteLine(line);
        _eventsSinceFlush++;

        // IMU events are high-frequency; batch flush to avoid UI hitching from frequent synchronous disk I/O.
        bool shouldFlush = !string.Equals(eventType, "IMU", StringComparison.OrdinalIgnoreCase)
            || _eventsSinceFlush >= FlushEventBatchSize
            || (DateTime.UtcNow - _lastFlushUtc) >= FlushInterval;

        if (shouldFlush)
        {
            _writer.Flush();
            _eventsSinceFlush = 0;
            _lastFlushUtc = DateTime.UtcNow;
        }
    }

    private static string ToText(int? value)
    {
        return value?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private static string ToText(double? value)
    {
        return value?.ToString("F6", CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private static string Csv(string value)
    {
        string escaped = (value ?? string.Empty).Replace("\"", "\"\"");
        return $"\"{escaped}\"";
    }

    private static string MakeSafeFilePart(string value)
    {
        foreach (char c in Path.GetInvalidFileNameChars())
        {
            value = value.Replace(c, '_');
        }

        return string.IsNullOrWhiteSpace(value) ? "session" : value;
    }

    private void StopSessionInternal()
    {
        _writer?.Flush();
        _writer?.Dispose();
        _writer = null;
        _currentFilePath = null;
        _eventsSinceFlush = 0;
    }
}
