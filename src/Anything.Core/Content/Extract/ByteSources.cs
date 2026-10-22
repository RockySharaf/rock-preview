namespace Anything.Core.Content.Extract;

/// <summary>Read-only random access to one file that counts the bytes actually read (the network cost).</summary>
internal interface IByteSource : IDisposable
{
    /// <summary>File length in bytes.</summary>
    long Length { get; }
    /// <summary>Bytes read so far.</summary>
    long BytesRead { get; }
    /// <summary>Reads at offset; fewer bytes only at the end; charges the budget (ContentLimitException past it, ContentIoException on I/O errors).</summary>
    int ReadAt(long offset, Span<byte> buffer);
}

/// <summary>Bytes in memory (tests, demo, decoded embedded streams).</summary>
internal sealed class MemoryByteSource(ReadOnlyMemory<byte> bytes, ExtractBudget budget) : IByteSource
{
    long _read;

    /// <inheritdoc/>
    public long Length => bytes.Length;
    /// <inheritdoc/>
    public long BytesRead => _read;

    /// <inheritdoc/>
    public int ReadAt(long offset, Span<byte> buffer)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        budget.Check();
        if (offset >= bytes.Length || buffer.IsEmpty) return 0;
        int n = (int)Math.Min(buffer.Length, bytes.Length - offset);
        budget.Read(n);
        bytes.Span.Slice((int)offset, n).CopyTo(buffer);
        _read += n;
        return n;
    }

    public void Dispose() { }
}

/// <summary>A seekable read-only Stream over a byte source (ZipArchive, XmlReader); it doesn't own the source.</summary>
internal sealed class ByteSourceStream(IByteSource source) : Stream
{
    long _position;

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => source.Length;

    public override long Position
    {
        get => _position;
        set { ArgumentOutOfRangeException.ThrowIfNegative(value); _position = value; }
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        int n = source.ReadAt(_position, buffer);
        _position += n;
        return n;
    }

    public override int ReadByte()
    {
        Span<byte> one = stackalloc byte[1];
        return Read(one) == 1 ? one[0] : -1;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        long to = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => source.Length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
        };
        if (to < 0) throw new IOException("Seek before the start of the file.");
        return _position = to;
    }

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>
/// Waits before a network read so a share is read at its speed limit; gets the share ("\\SERVER\SHARE" or "Z:") and the
/// bytes about to be read, returns the milliseconds it waited (left out of the reader's time limit). The indexer's reads
/// bring one per request (<see cref="ExtractRequest.Throttle"/>, the scheduler's per-share bucket); a FileContentSource's
/// own one serves the other reads (calibration).
/// </summary>
internal delegate long NetworkThrottle(string share, long bytes, CancellationToken ct);

/// <summary>An I/O error from the file (not the format): the source classifies it as Retry, Offline, AccessDenied or temporary trouble, never Damaged.</summary>
internal sealed class ContentIoException(int win32Error, string message) : IOException(message)
{
    /// <summary>The Win32 error code (995 = cancelled, for example by the watchdog).</summary>
    public int Win32Error { get; } = win32Error;
}

/// <summary>A limit was reached; the text read so far is kept.</summary>
internal sealed class ContentLimitException(string limit) : Exception(limit)
{
    /// <summary>Time limit (TimedOut when nothing was read).</summary>
    public const string Time = "time";
    /// <summary>Bytes read from the file: what the reader asked for (on a share, what was fetched when that is more).</summary>
    public const string Bytes = "bytes";
    /// <summary>Decoded bytes in total.</summary>
    public const string Decoded = "decoded";
    /// <summary>One stream or entry decoded past MaxStreamBytes: readers skip that unit and go on.</summary>
    public const string Stream = "stream";
    /// <summary>Text or pages: the sink is full.</summary>
    public const string Text = "text";

    /// <summary>Which limit (one of the constants).</summary>
    public string Limit { get; } = limit;
}

/// <summary>Time, byte and decode budgets for one file; Check throws ContentLimitException or OperationCanceledException.</summary>
internal sealed class ExtractBudget
{
    readonly Func<long> _clock;
    readonly long _start;
    long _waited, _read, _decoded;

    public ExtractBudget(ExtractLimits limits, CancellationToken ct, Func<long>? clockMs = null)
    {
        Limits = limits;
        Token = ct;
        _clock = clockMs ?? (static () => Environment.TickCount64);
        _start = _clock();
    }

    public ExtractLimits Limits { get; }
    public CancellationToken Token { get; }

    /// <summary>Bytes charged by <see cref="Read"/> so far.</summary>
    public long BytesRead => _read;
    /// <summary>Bytes charged by <see cref="Decoded"/> so far.</summary>
    public long DecodedBytes => _decoded;

    /// <summary>Charges bytes read from the file (FileByteSource: the bytes asked for, not its blocks; see its Charge).</summary>
    public void Read(long bytes)
    {
        _read += bytes;
        if (_read > Limits.MaxBytesRead) throw new ContentLimitException(ContentLimitException.Bytes);
    }

    /// <summary>Charges decoded (inflated, decrypted) bytes; a stream past MaxStreamBytes must stop (ContentLimitException.Stream, raised by the reader).</summary>
    public void Decoded(long bytes)
    {
        _decoded += bytes;
        if (_decoded > Limits.MaxDecodedBytes) throw new ContentLimitException(ContentLimitException.Decoded);
    }

    /// <summary>Leaves time spent waiting (the network throttle) out of the time limit.</summary>
    public void Waited(long milliseconds)
    {
        if (milliseconds > 0) _waited += milliseconds;
    }

    /// <summary>Checks time and cancellation; readers call it in every loop over file data.</summary>
    public void Check()
    {
        Token.ThrowIfCancellationRequested();
        if (ElapsedMs > Limits.MaxMilliseconds) throw new ContentLimitException(ContentLimitException.Time);
    }

    /// <summary>Time spent so far, throttle waits excluded.</summary>
    public long ElapsedMs => Math.Max(0, _clock() - _start - _waited);
}
