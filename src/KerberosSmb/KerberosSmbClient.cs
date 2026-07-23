using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SMBLibrary;
using SMBLibrary.Client;

namespace KerberosSmb;

/// <summary>
/// Thin facade over SMBLibrary's SMB2Client that authenticates via Kerberos
/// (KerberosSmbAuthenticationClient) instead of SMBLibrary's built-in NTLM path.
///
/// Registered as a singleton: establishing the connection (TCP handshake, SMB negotiate, Kerberos
/// session setup and tree connect) costs several network round trips, so it is done once and the
/// connection is reused across requests. Operations are serialized with a lock because a single
/// SMB2Client connection cannot service concurrent requests, and a connection that has been
/// dropped (idle timeout, server restart) is rebuilt on demand.
/// </summary>
public sealed class KerberosSmbClient(IOptions<KerberosSmbOptions> options, ILoggerFactory loggerFactory)
    : IDisposable
{
    // SMBLibrary's default per-response timeout is 5s. A single signed WriteFile of MaxWriteSize
    // (up to ~1 MB) over a slow or lossy link can exceed that and fail with STATUS_IO_TIMEOUT,
    // which then orphans the server-side file handle. Use a more generous timeout.
    private const int ResponseTimeoutMilliseconds = 30_000;

    // Open files sharing read/write/delete (what Windows Explorer does) rather than exclusively,
    // so a handle left open by an interrupted transfer can't block a subsequent open with
    // STATUS_SHARING_VIOLATION until the server reclaims it.
    private const ShareAccess DefaultShareAccess = ShareAccess.Read | ShareAccess.Write | ShareAccess.Delete;

    private readonly KerberosSmbOptions _options = options.Value;
    private readonly Lock _connectionLock = new();
    private SMB2Client? _client;
    private ISMBFileStore? _fileStore;

    /// <summary>
    /// Establishes the shared connection if it isn't already up. Safe to call repeatedly; the
    /// file-operation methods call it themselves, so this is only needed to fail fast / warm up.
    /// </summary>
    public void Connect()
    {
        lock (_connectionLock)
        {
            EnsureConnectedLocked();
        }
    }

    public IReadOnlyList<SmbFileEntry> ListFiles() => Execute(() =>
    {
        var status = _fileStore!.CreateFile(out var directoryHandle, out _, string.Empty, AccessMask.GENERIC_READ,
            SMBLibrary.FileAttributes.Directory, ShareAccess.Read | ShareAccess.Write, CreateDisposition.FILE_OPEN,
            CreateOptions.FILE_DIRECTORY_FILE, null);
        if (status != NTStatus.STATUS_SUCCESS)
        {
            throw new IOException($"Failed to open share root for listing, status {status}");
        }

        try
        {
            // SMBLibrary's QueryDirectory pages internally and returns the final status: once every
            // entry has been read the server reports STATUS_NO_MORE_FILES, which is the normal
            // end-of-enumeration terminator (the result list is already fully populated), not an error.
            status = _fileStore.QueryDirectory(out var fileList, directoryHandle, "*", FileInformationClass.FileDirectoryInformation);
            if (status != NTStatus.STATUS_SUCCESS && status != NTStatus.STATUS_NO_MORE_FILES)
            {
                throw new IOException($"Failed to list files, status {status}");
            }

            return fileList
                .OfType<FileDirectoryInformation>()
                .Where(f => f.FileName != "." && f.FileName != "..")
                .Select(f => new SmbFileEntry
                {
                    Name = f.FileName,
                    Size = f.EndOfFile,
                    IsDirectory = (f.FileAttributes & SMBLibrary.FileAttributes.Directory) != 0,
                    LastModified = f.LastWriteTime
                })
                .ToList();
        }
        finally
        {
            _fileStore.CloseFile(directoryHandle);
        }
    });

    public byte[] DownloadFile(string fileName) => Execute(() =>
    {
        var status = _fileStore!.CreateFile(out var fileHandle, out _, fileName,
            AccessMask.GENERIC_READ | AccessMask.SYNCHRONIZE, SMBLibrary.FileAttributes.Normal, DefaultShareAccess,
            CreateDisposition.FILE_OPEN, CreateOptions.FILE_NON_DIRECTORY_FILE | CreateOptions.FILE_SYNCHRONOUS_IO_ALERT, null);
        if (status != NTStatus.STATUS_SUCCESS)
        {
            throw new FileNotFoundException($"Failed to open '{fileName}', status {status}");
        }

        try
        {
            using var stream = new MemoryStream();
            long bytesRead = 0;
            while (true)
            {
                status = _fileStore.ReadFile(out var data, fileHandle, bytesRead, (int)_client!.MaxReadSize);
                if (status != NTStatus.STATUS_SUCCESS && status != NTStatus.STATUS_END_OF_FILE)
                {
                    throw new IOException($"Failed to read '{fileName}', status {status}");
                }

                if (status == NTStatus.STATUS_END_OF_FILE || data.Length == 0)
                {
                    break;
                }

                bytesRead += data.Length;
                stream.Write(data, 0, data.Length);
            }

            return stream.ToArray();
        }
        finally
        {
            _fileStore.CloseFile(fileHandle);
        }
    });

    public void UploadFile(string fileName, byte[] content) => Execute(() =>
    {
        var status = _fileStore!.CreateFile(out var fileHandle, out _, fileName,
            AccessMask.GENERIC_WRITE | AccessMask.SYNCHRONIZE, SMBLibrary.FileAttributes.Normal, DefaultShareAccess,
            CreateDisposition.FILE_OVERWRITE_IF, CreateOptions.FILE_NON_DIRECTORY_FILE | CreateOptions.FILE_SYNCHRONOUS_IO_ALERT, null);
        if (status != NTStatus.STATUS_SUCCESS)
        {
            throw new IOException($"Failed to create '{fileName}', status {status}");
        }

        try
        {
            var offset = 0;
            var maxWriteSize = Math.Max(1, (int)_client!.MaxWriteSize);
            while (offset < content.Length)
            {
                var chunkSize = Math.Min(maxWriteSize, content.Length - offset);
                var chunk = new byte[chunkSize];
                Array.Copy(content, offset, chunk, 0, chunkSize);
                status = _fileStore.WriteFile(out _, fileHandle, offset, chunk);
                if (status != NTStatus.STATUS_SUCCESS)
                {
                    throw new IOException($"Failed to write '{fileName}', status {status}");
                }

                offset += chunkSize;
            }
        }
        finally
        {
            _fileStore.CloseFile(fileHandle);
        }
    });

    public void DeleteFile(string fileName) => Execute(() =>
    {
        var status = _fileStore!.CreateFile(out var fileHandle, out _, fileName,
            AccessMask.GENERIC_WRITE | AccessMask.DELETE | AccessMask.SYNCHRONIZE, SMBLibrary.FileAttributes.Normal,
            DefaultShareAccess, CreateDisposition.FILE_OPEN,
            CreateOptions.FILE_NON_DIRECTORY_FILE | CreateOptions.FILE_SYNCHRONOUS_IO_ALERT, null);
        if (status != NTStatus.STATUS_SUCCESS)
        {
            throw new FileNotFoundException($"Failed to open '{fileName}' for delete, status {status}");
        }

        try
        {
            var dispositionInfo = new FileDispositionInformation { DeletePending = true };
            status = _fileStore.SetFileInformation(fileHandle, dispositionInfo);
            if (status != NTStatus.STATUS_SUCCESS)
            {
                throw new IOException($"Failed to delete '{fileName}', status {status}");
            }
        }
        finally
        {
            _fileStore.CloseFile(fileHandle);
        }
    });

    /// <summary>
    /// Runs a file operation against the shared connection, serialized so concurrent requests don't
    /// interleave on one SMB2Client. If the operation fails because the connection has dropped, the
    /// connection is rebuilt once and the operation retried (the operations here are idempotent).
    /// </summary>
    private T Execute<T>(Func<T> operation)
    {
        lock (_connectionLock)
        {
            EnsureConnectedLocked();
            try
            {
                return operation();
            }
            catch (IOException ex) when (ex is not FileNotFoundException)
            {
                ResetConnectionLocked();
                EnsureConnectedLocked();
                return operation();
            }
        }
    }

    private void Execute(Action operation) => Execute(() =>
    {
        operation();
        return true;
    });

    private void EnsureConnectedLocked()
    {
        if (_fileStore != null)
        {
            return;
        }

        _client = new SMB2Client(ResponseTimeoutMilliseconds);
        if (!_client.Connect(_options.Server, SMBTransportType.DirectTCPTransport))
        {
            throw new IOException($"Failed to connect to SMB server '{_options.Server}'");
        }

        var authenticationClient = new KerberosSmbAuthenticationClient(_options.Spn, loggerFactory);
        var loginStatus = _client.Login(authenticationClient);
        if (loginStatus != NTStatus.STATUS_SUCCESS)
        {
            throw new IOException($"Kerberos SMB login to '{_options.Server}' failed with status {loginStatus}");
        }

        _fileStore = _client.TreeConnect(_options.Share, out var treeStatus);
        if (treeStatus != NTStatus.STATUS_SUCCESS || _fileStore == null)
        {
            throw new IOException($"Failed to connect to share '{_options.Share}', status {treeStatus}");
        }
    }

    private void ResetConnectionLocked()
    {
        try { _fileStore?.Disconnect(); } catch { /* already gone */ }
        try { _client?.Disconnect(); } catch { /* already gone */ }
        _fileStore = null;
        _client = null;
    }

    public void Dispose()
    {
        lock (_connectionLock)
        {
            ResetConnectionLocked();
        }
    }
}
