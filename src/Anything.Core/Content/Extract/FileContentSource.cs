using Anything.Core.Indexing;

namespace Anything.Core.Content.Extract;

/// <summary>
/// Real files: fresh attributes, share-all handle, Date Accessed kept, stat-read-stat, I/O errors classified (never
/// Damaged). The protocol per file:
/// <list type="number">
/// <item>GetFileAttributesEx on the \\?\ path (fresh: NTFS roots of the name index mask the cloud bits): cloud
/// placeholders are CloudOnly and never opened, "don't index contents" is OptedOut, EFS is EfsEncrypted unless allowed,
/// a document over the size limit is TooBig (pictures never are: only their headers are read), and an empty local file
/// is NoText without being opened (on a share, once its handle says it is empty).</item>
/// <item>Open with GENERIC_READ | FILE_WRITE_ATTRIBUTES and Read|Write|Delete sharing, SetFileTime(-1) so Date Accessed
/// stays; read-only when that is refused (remembered per share for a day).</item>
/// <item>Stat the handle, read through the readers (random access only), then stat the handle again: a change of size
/// or date during the read gives Retry and nothing is recorded. The handle's stamp is the one reported.</item>
/// </list>
/// I/O errors become Retry (in use, deleted, cancelled, other trouble: record nothing, try later), Offline (the drive or
/// share went away: back its root off) or AccessDenied (recorded). A read the watchdog cancelled (995) is Retry with
/// <see cref="LastIoError"/> = 995, so the follower, which knows it cancelled, probes the root and decides between Offline
/// and TimedOut.
/// </summary>
internal sealed class FileContentSource : IContentSource
{
    [ThreadStatic] static int t_lastIoError;

    readonly NetworkThrottle? _throttle;
    readonly Func<string, FileAttributes, FileAttributes>? _attributes;
    readonly Action<string>? _afterRead;

    /// <summary>The real readers on disk.</summary>
    /// <param name="networkThrottle">Called before each network read (the per-share speed limit) unless the request brings its own; null = no limit.</param>
    public FileContentSource(NetworkThrottle? networkThrottle = null) : this(networkThrottle, null, null) { }

    /// <summary>Tests: an attribute hook (simulates cloud and EFS files) and a hook run between the read and the second stat.</summary>
    internal FileContentSource(NetworkThrottle? networkThrottle, Func<string, FileAttributes, FileAttributes>? attributes, Action<string>? afterRead)
    {
        _throttle = networkThrottle;
        _attributes = attributes;
        _afterRead = afterRead;
    }

    /// <summary>
    /// The Win32 error behind the calling thread's last <see cref="Read(ExtractRequest, CancellationToken)"/> that gave
    /// Retry, Offline or AccessDenied because of an I/O error (995: cancelled, for example by the watchdog); 0 otherwise.
    /// </summary>
    public static int LastIoError => t_lastIoError;

    /// <inheritdoc/>
    public ContentRead Read(ExtractRequest request, CancellationToken ct) => Read(request, ct, out _);

    /// <summary><see cref="Read(ExtractRequest, CancellationToken)"/> that also gives the I/O error behind its outcome (0 = none).</summary>
    public ContentRead Read(ExtractRequest request, CancellationToken ct, out int ioError)
    {
        ct.ThrowIfCancellationRequested();
        t_lastIoError = ioError = 0;
        string path = FileByteSource.Backslashes(request.FullPath);
        var extension = ContentTypes.ExtensionOf(Path.GetFileName(path.AsSpan()));
        byte version = ContentReaders.CurrentVersion(extension, request.Kind);
        ContentRead Status(DocStatus status, long size, long modified) => new(ExtractedDoc.Failed(request.Kind, status, version), size, modified, false, false);
        ContentRead Retry(long size, long modified) => new(ExtractedDoc.Failed(request.Kind, DocStatus.Unsupported, version), size, modified, true, false);

        using var quiet = Drives.QuietErrors();
        try
        {
            var budget = new ExtractBudget(request.Limits, ct);
            if (!Stat(path, out var attributes, out long size, out long modified, out int statError))
                return Classify(statError, path, -1, 0, ct, out ioError);
            if (_attributes != null) attributes = _attributes(path, attributes);
            uint bits = (uint)attributes;
            if ((attributes & FileAttributes.Directory) != 0) return Retry(size, modified);
            if ((bits & ReaderNative.CloudBits) != 0) return Status(DocStatus.CloudOnly, size, modified);
            if ((bits & ReaderNative.FILE_ATTRIBUTE_NOT_CONTENT_INDEXED) != 0) return Status(DocStatus.OptedOut, size, modified);
            if ((bits & ReaderNative.FILE_ATTRIBUTE_ENCRYPTED) != 0 && !request.ReadEncrypted) return Status(DocStatus.EfsEncrypted, size, modified);
            if (request.Kind != ContentKind.Picture && request.MaxFileBytes > 0 && size > request.MaxFileBytes) return Status(DocStatus.TooBig, size, modified);
            if (ContentReaders.ReaderFor(extension, request.Kind) == null) return Status(DocStatus.Unsupported, size, modified);
            // An empty file (a failed download, a file made before it was written, a ShellNew template) has no words, and
            // is no problem: never opened on a local disk; a share's answer may lag, so there the open handle decides.
            if (size == 0 && !request.Network) return Status(DocStatus.NoText, size, modified);

            var failed = FileByteSource.TryOpen(path, budget, request.Network, request.Throttle ?? _throttle, out var opened, out int openError);
            if (failed != null || opened == null)
                return failed == DocStatus.AccessDenied ? Denied(openError, size, modified, out ioError) : Classify(openError, path, size, modified, ct, out ioError);
            using (var source = opened)
            {
                // Both stamps of stat-read-stat come from the handle: a share may answer the path query from its
                // directory cache, which can lag behind the file itself.
                if (source.TryStat(out long sizeBefore, out long modifiedBefore, out _)) (size, modified) = (sizeBefore, modifiedBefore);
                if (size == 0 && source.Length == 0) return Status(DocStatus.NoText, size, modified);
                ExtractedDoc doc;
                try { doc = ContentReaders.Extract(source, extension, request.Kind, budget); }
                catch (ContentIoException e)
                {
                    ct.ThrowIfCancellationRequested();
                    return Classify(e.Win32Error, path, size, modified, ct, out ioError);
                }
                _afterRead?.Invoke(path);
                // Stat-read-stat: a file that changed while it was read is read again later, not recorded.
                if (!source.TryStat(out long sizeAfter, out long modifiedAfter, out _) || sizeAfter != size || modifiedAfter != modified)
                    return Retry(size, modified);
                return new ContentRead(doc, size, modified, false, false);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // Never a status from an unexpected fault outside the readers: try again later.
            ct.ThrowIfCancellationRequested();
            t_lastIoError = ioError = ReaderNative.ErrorOf(e);
            return Retry(request.ExpectedSize, request.ExpectedModifiedUtc);
        }

        ContentRead Denied(int error, long size, long modified, out int io)
        {
            t_lastIoError = io = error;
            return Status(DocStatus.AccessDenied, size, modified);
        }

        ContentRead Classify(int error, string file, long size, long modified, CancellationToken token, out int io)
        {
            t_lastIoError = io = error;
            if (error == ReaderNative.ERROR_ACCESS_DENIED) return Status(DocStatus.AccessDenied, size, modified);
            if (error is >= ReaderNative.ERROR_CLOUD_FILE_FIRST and <= ReaderNative.ERROR_CLOUD_FILE_LAST) return Status(DocStatus.CloudOnly, size, modified);
            if (IsOffline(file, error, token)) return new(ExtractedDoc.Failed(request.Kind, DocStatus.Unsupported, version), size, modified, false, true);
            // In use, locked, deleted meanwhile, cancelled, a bad sector or any other trouble: not the file's fault.
            return Retry(size, modified);
        }
    }

    /// <summary>
    /// Whether an error means the drive or share went away. "Path not found" means that only when its root no longer
    /// answers (probed with cancellation, so a dead server can't hold the reader); otherwise a folder was deleted.
    /// </summary>
    static bool IsOffline(string path, int error, CancellationToken ct)
    {
        if (!FolderScanner.IsOfflineError(error)) return false;
        if (error != ReaderNative.ERROR_PATH_NOT_FOUND) return true;
        string? root;
        try { root = Path.GetPathRoot(path); }
        catch (ArgumentException) { return true; }
        if (string.IsNullOrEmpty(root)) return true;
        int probe = FolderScanner.ProbeRoot(root.TrimEnd('\\'), ct);
        return probe != 0 && FolderScanner.IsOfflineError(probe);
    }

    static bool Stat(string path, out FileAttributes attributes, out long size, out long modified, out int error)
    {
        if (Native.GetFileAttributesEx(Native.LongPath(path), 0, out var d))
        {
            attributes = (FileAttributes)d.FileAttributes;
            size = ((long)d.FileSizeHigh << 32) | d.FileSizeLow;
            modified = d.LastWriteTime;
            error = 0;
            return true;
        }
        error = System.Runtime.InteropServices.Marshal.GetLastPInvokeError();
        attributes = 0; size = 0; modified = 0;
        return false;
    }

    /// <inheritdoc/>
    public bool TryStat(string fullPath, out long size, out long modifiedUtc, out FileAttributes attributes)
    {
        using var quiet = Drives.QuietErrors();
        try { return Native.TryGetMetadata(fullPath, out attributes, out size, out modifiedUtc, out _); }
        catch (Exception e) when (e is ArgumentException or IOException)
        {
            size = -1; modifiedUtc = 0; attributes = 0;
            return false;
        }
    }
}
