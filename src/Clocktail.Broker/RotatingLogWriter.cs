using System.Text;

namespace Clocktail.Broker;

/// <summary>
/// Thread-safe, append-only log writer for <c>broker.log</c> that rotates by size without deleting existing logs on
/// startup and without crashing if a viewer temporarily holds a file open.
/// </summary>
public sealed class RotatingLogWriter : TextWriter
{
    public const long DefaultMaxBytes = 10 * 1024 * 1024;
    public const int DefaultMaxBackups = 3;

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly string _path;
    private readonly long _maxBytes;
    private readonly int _maxBackups;
    private readonly long _retryStepBytes;
    private readonly object _gate = new();

    private FileStream? _stream;
    private StreamWriter? _writer;
    private long _nextRotateAt;
    private bool _atLineStart = true;
    private bool _disposed;

    public RotatingLogWriter(string path, long maxBytes = DefaultMaxBytes, int maxBackups = DefaultMaxBackups)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("log path must not be empty", nameof(path));
        if (maxBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxBytes), "maxBytes must be positive");
        if (maxBackups < 1) throw new ArgumentOutOfRangeException(nameof(maxBackups), "maxBackups must be at least 1");

        _path = Path.GetFullPath(path);
        _maxBytes = maxBytes;
        _maxBackups = maxBackups;
        _retryStepBytes = Math.Max(64 * 1024, maxBytes / 10);

        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        OpenOrRotateIfOversizedLocked();
    }

    public override Encoding Encoding => Utf8NoBom;

    public string BackupPath(int index) => $"{_path}.{index}";

    public override void Write(char value)
    {
        lock (_gate)
        {
            if (_disposed) return;
            PrepareForWriteLocked();
            _writer!.Write(value);
            _atLineStart = value == '\n';
        }
    }

    public override void Write(string? value)
    {
        if (value == null) return;
        lock (_gate)
        {
            if (_disposed) return;
            PrepareForWriteLocked();
            _writer!.Write(value);
            if (value.Length > 0) _atLineStart = value[^1] == '\n';
        }
    }

    public override void Write(char[] buffer, int index, int count)
    {
        lock (_gate)
        {
            if (_disposed) return;
            PrepareForWriteLocked();
            _writer!.Write(buffer, index, count);
            if (count > 0) _atLineStart = buffer[index + count - 1] == '\n';
        }
    }

    public override void WriteLine(string? value)
    {
        lock (_gate)
        {
            if (_disposed) return;
            PrepareForWriteLocked();
            _writer!.WriteLine(value);
            _atLineStart = true;
        }
    }

    public override void Flush()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _writer?.Flush();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                CloseStreamLocked();
            }
        }
        base.Dispose(disposing);
    }

    private void OpenOrRotateIfOversizedLocked()
    {
        OpenAppendLocked();
        if (_stream!.Position >= _maxBytes)
            RotateLocked();
    }

    private void PrepareForWriteLocked()
    {
        if (_writer == null)
            OpenAppendLocked();
        long pos = _stream!.Position;
        if (pos >= _maxBytes && pos >= _nextRotateAt && (_atLineStart || pos >= _maxBytes * 2))
            RotateLocked();
    }

    private void OpenAppendLocked()
    {
        _stream = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        _writer = new StreamWriter(_stream, Utf8NoBom) { AutoFlush = true };
        _atLineStart = true;
    }

    private void CloseStreamLocked()
    {
        _writer?.Dispose();
        _writer = null;
        _stream = null;
    }

    private void RotateLocked()
    {
        long currentSize = _stream?.Position ?? 0;
        CloseStreamLocked();
        try
        {
            for (int i = _maxBackups - 1; i >= 1; i--)
            {
                string src = BackupPath(i);
                if (File.Exists(src))
                    File.Move(src, BackupPath(i + 1), overwrite: true);
            }
            if (File.Exists(_path))
                File.Move(_path, BackupPath(1), overwrite: true);
            _nextRotateAt = 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Keep logging rather than dropping output or crashing the supervisor if a reader holds a lock.
            _nextRotateAt = currentSize + _retryStepBytes;
        }
        OpenAppendLocked();
    }
}
