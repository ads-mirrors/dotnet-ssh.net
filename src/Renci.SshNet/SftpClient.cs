﻿#nullable enable
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Renci.SshNet.Abstractions;
using Renci.SshNet.Common;
using Renci.SshNet.Sftp;

namespace Renci.SshNet
{
    /// <summary>
    /// Implementation of the SSH File Transfer Protocol (SFTP) over SSH.
    /// </summary>
    public class SftpClient : BaseClient, ISftpClient
    {
        private static readonly Encoding Utf8NoBOM = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

        /// <summary>
        /// Holds the <see cref="ISftpSession"/> instance that is used to communicate to the
        /// SFTP server.
        /// </summary>
        private ISftpSession? _sftpSession;

        /// <summary>
        /// Holds the operation timeout.
        /// </summary>
        private int _operationTimeout;

        /// <summary>
        /// Holds the size of the buffer.
        /// </summary>
        private uint _bufferSize;

        /// <summary>
        /// Gets or sets the operation timeout.
        /// </summary>
        /// <value>
        /// The timeout to wait until an operation completes. The default value is negative
        /// one (-1) milliseconds, which indicates an infinite timeout period.
        /// </value>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="value"/> represents a value that is less than -1 or greater than <see cref="int.MaxValue"/> milliseconds.</exception>
        public TimeSpan OperationTimeout
        {
            get
            {
                return TimeSpan.FromMilliseconds(_operationTimeout);
            }
            set
            {
                _operationTimeout = value.AsTimeout(nameof(OperationTimeout));

                if (_sftpSession is { } sftpSession)
                {
                    sftpSession.OperationTimeout = _operationTimeout;
                }
            }
        }

        /// <summary>
        /// Gets or sets the maximum size of the buffer in bytes.
        /// </summary>
        /// <value>
        /// The size of the buffer. The default buffer size is 32768 bytes (32 KB).
        /// </value>
        /// <remarks>
        /// <para>
        /// For write operations, this limits the size of the payload for
        /// individual SSH_FXP_WRITE messages. The actual size is always
        /// capped at the maximum packet size supported by the peer
        /// (minus the size of protocol fields).
        /// </para>
        /// <para>
        /// For read operations, this controls the size of the payload which
        /// is requested from the peer in a SSH_FXP_READ message. The peer
        /// will send the requested number of bytes in a SSH_FXP_DATA message,
        /// possibly split over multiple SSH_MSG_CHANNEL_DATA messages.
        /// </para>
        /// <para>
        /// To optimize the size of the SSH packets sent by the peer,
        /// the actual requested size will take into account the size of the
        /// SSH_FXP_DATA protocol fields.
        /// </para>
        /// <para>
        /// The size of the each individual SSH_FXP_DATA message is limited to the
        /// local maximum packet size of the channel, which is set to <c>64 KB</c>
        /// for SSH.NET. However, the peer can limit this even further.
        /// </para>
        /// </remarks>
        /// <exception cref="ObjectDisposedException">The method was called after the client was disposed.</exception>
        public uint BufferSize
        {
            get
            {
                CheckDisposed();
                return _bufferSize;
            }
            set
            {
                CheckDisposed();
                _bufferSize = value;
            }
        }

        /// <summary>
        /// Gets a value indicating whether this client is connected to the server and
        /// the SFTP session is open.
        /// </summary>
        /// <value>
        /// <see langword="true"/> if this client is connected and the SFTP session is open; otherwise, <see langword="false"/>.
        /// </value>
        /// <exception cref="ObjectDisposedException">The method was called after the client was disposed.</exception>
        public override bool IsConnected
        {
            get
            {
                return base.IsConnected && _sftpSession?.IsOpen == true;
            }
        }

        /// <summary>
        /// Gets remote working directory.
        /// </summary>
        /// <exception cref="SshConnectionException">Client is not connected.</exception>
        /// <exception cref="ObjectDisposedException">The method was called after the client was disposed.</exception>
        public string WorkingDirectory
        {
            get
            {
                CheckDisposed();

                if (_sftpSession is null)
                {
                    throw new SshConnectionException("Client not connected.");
                }

                return _sftpSession.WorkingDirectory;
            }
        }

        /// <summary>
        /// Gets sftp protocol version.
        /// </summary>
        /// <exception cref="SshConnectionException">Client is not connected.</exception>
        /// <exception cref="ObjectDisposedException">The method was called after the client was disposed.</exception>
        public int ProtocolVersion
        {
            get
            {
                CheckDisposed();

                if (_sftpSession is null)
                {
                    throw new SshConnectionException("Client not connected.");
                }

                return (int)_sftpSession.ProtocolVersion;
            }
        }

        /// <summary>
        /// Gets the current SFTP session.
        /// </summary>
        /// <value>
        /// The current SFTP session.
        /// </value>
        internal ISftpSession? SftpSession
        {
            get { return _sftpSession; }
        }

        #region Constructors

        /// <summary>
        /// Initializes a new instance of the <see cref="SftpClient"/> class.
        /// </summary>
        /// <param name="connectionInfo">The connection info.</param>
        /// <exception cref="ArgumentNullException"><paramref name="connectionInfo"/> is <see langword="null"/>.</exception>
        public SftpClient(ConnectionInfo connectionInfo)
            : this(connectionInfo, ownsConnectionInfo: false)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="SftpClient"/> class.
        /// </summary>
        /// <param name="host">Connection host.</param>
        /// <param name="port">Connection port.</param>
        /// <param name="username">Authentication username.</param>
        /// <param name="password">Authentication password.</param>
        /// <exception cref="ArgumentNullException"><paramref name="password"/> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentException"><paramref name="host"/> is invalid. <para>-or-</para> <paramref name="username"/> is <see langword="null"/> or contains only whitespace characters.</exception>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="port"/> is not within <see cref="IPEndPoint.MinPort"/> and <see cref="IPEndPoint.MaxPort"/>.</exception>
        [SuppressMessage("Microsoft.Reliability", "CA2000:DisposeObjectsBeforeLosingScope", Justification = "Disposed in Dispose(bool) method.")]
        public SftpClient(string host, int port, string username, string password)
            : this(new PasswordConnectionInfo(host, port, username, password), ownsConnectionInfo: true)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="SftpClient"/> class.
        /// </summary>
        /// <param name="host">Connection host.</param>
        /// <param name="username">Authentication username.</param>
        /// <param name="password">Authentication password.</param>
        /// <exception cref="ArgumentNullException"><paramref name="password"/> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentException"><paramref name="host"/> is invalid. <para>-or-</para> <paramref name="username"/> is <see langword="null"/> contains only whitespace characters.</exception>
        public SftpClient(string host, string username, string password)
            : this(host, ConnectionInfo.DefaultPort, username, password)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="SftpClient"/> class.
        /// </summary>
        /// <param name="host">Connection host.</param>
        /// <param name="port">Connection port.</param>
        /// <param name="username">Authentication username.</param>
        /// <param name="keyFiles">Authentication private key file(s) .</param>
        /// <exception cref="ArgumentNullException"><paramref name="keyFiles"/> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentException"><paramref name="host"/> is invalid. <para>-or-</para> <paramref name="username"/> is <see langword="null"/> or contains only whitespace characters.</exception>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="port"/> is not within <see cref="IPEndPoint.MinPort"/> and <see cref="IPEndPoint.MaxPort"/>.</exception>
        [SuppressMessage("Microsoft.Reliability", "CA2000:DisposeObjectsBeforeLosingScope", Justification = "Disposed in Dispose(bool) method.")]
        public SftpClient(string host, int port, string username, params IPrivateKeySource[] keyFiles)
            : this(new PrivateKeyConnectionInfo(host, port, username, keyFiles), ownsConnectionInfo: true)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="SftpClient"/> class.
        /// </summary>
        /// <param name="host">Connection host.</param>
        /// <param name="username">Authentication username.</param>
        /// <param name="keyFiles">Authentication private key file(s) .</param>
        /// <exception cref="ArgumentNullException"><paramref name="keyFiles"/> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentException"><paramref name="host"/> is invalid. <para>-or-</para> <paramref name="username"/> is <see langword="null"/> or contains only whitespace characters.</exception>
        public SftpClient(string host, string username, params IPrivateKeySource[] keyFiles)
            : this(host, ConnectionInfo.DefaultPort, username, keyFiles)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="SftpClient"/> class.
        /// </summary>
        /// <param name="connectionInfo">The connection info.</param>
        /// <param name="ownsConnectionInfo">Specified whether this instance owns the connection info.</param>
        /// <exception cref="ArgumentNullException"><paramref name="connectionInfo"/> is <see langword="null"/>.</exception>
        /// <remarks>
        /// If <paramref name="ownsConnectionInfo"/> is <see langword="true"/>, the connection info will be disposed when this
        /// instance is disposed.
        /// </remarks>
        private SftpClient(ConnectionInfo connectionInfo, bool ownsConnectionInfo)
            : this(connectionInfo, ownsConnectionInfo, new ServiceFactory())
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="SftpClient"/> class.
        /// </summary>
        /// <param name="connectionInfo">The connection info.</param>
        /// <param name="ownsConnectionInfo">Specified whether this instance owns the connection info.</param>
        /// <param name="serviceFactory">The factory to use for creating new services.</param>
        /// <exception cref="ArgumentNullException"><paramref name="connectionInfo"/> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentNullException"><paramref name="serviceFactory"/> is <see langword="null"/>.</exception>
        /// <remarks>
        /// If <paramref name="ownsConnectionInfo"/> is <see langword="true"/>, the connection info will be disposed when this
        /// instance is disposed.
        /// </remarks>
        internal SftpClient(ConnectionInfo connectionInfo, bool ownsConnectionInfo, IServiceFactory serviceFactory)
            : base(connectionInfo, ownsConnectionInfo, serviceFactory)
        {
            _operationTimeout = Timeout.Infinite;
            _bufferSize = 1024 * 32;
        }

        #endregion Constructors

        /// <summary>
        /// Changes remote directory to path.
        /// </summary>
        /// <param name="path">New directory path.</param>
        /// <exception cref="ArgumentNullException"><paramref name="path"/> is <see langword="null"/>.</exception>
        /// <exception cref="SshConnectionException">Client is not connected.</exception>
        /// <exception cref="SftpPermissionDeniedException">Permission to change directory denied by remote host. <para>-or-</para> A SSH command was denied by the server.</exception>
        /// <exception cref="SftpPathNotFoundException"><paramref name="path"/> was not found on the remote host.</exception>
        /// <exception cref="SshException">A SSH error where <see cref="Exception.Message"/> is the message from the remote host.</exception>
        /// <exception cref="ObjectDisposedException">The method was called after the client was disposed.</exception>
        public void ChangeDirectory(string path)
        {
            CheckDisposed();
            ThrowHelper.ThrowIfNull(path);

            if (_sftpSession is null)
            {
                throw new SshConnectionException("Client not connected.");
            }

            _sftpSession.ChangeDirectory(path);
        }

        /// <summary>
        /// Asynchronously requests to change the current working directory to the specified path.
        /// </summary>
        /// <param name="path">The new working directory.</param>
        /// <param name="cancellationToken">The token to monitor for cancellation requests.</param>
        /// <returns>A <see cref="Task"/> that tracks the asynchronous change working directory request.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="path"/> is <see langword="null"/>.</exception>
        /// <exception cref="SshConnectionException">Client is not connected.</exception>
        /// <exception cref="SftpPermissionDeniedException">Permission to change directory denied by remote host. <para>-or-</para> A SSH command was denied by the server.</exception>
        /// <exception cref="SftpPathNotFoundException"><paramref name="path"/> was not found on the remote host.</exception>
        /// <exception cref="SshException">A SSH error where <see cref="Exception.Message"/> is the message from the remote host.</exception>
        /// <exception cref="ObjectDisposedException">The method was called after the client was disposed.</exception>
        public Task ChangeDirectoryAsync(string path, CancellationToken cancellationToken = default)
        {
            CheckDisposed();
            ThrowHelper.ThrowIfNull(path);

            if (_sftpSession is null)
            {
                throw new SshConnectionException("Client not connected.");
            }

            cancellationToken.ThrowIfCancellationRequested();

            return _sftpSession.ChangeDirectoryAsync(path, cancellationToken);
        }

        /// <summary>
        /// Changes permissions of file(s) to specified mode.
        /// </summary>
        /// <param name="path">File(s) path, may match multiple files.</param>
        /// <param name="mode">The mode.</param>
        /// <exception cref="ArgumentNullException"><paramref name="path"/> is <see langword="null"/>.</exception>
        /// <exception cref="SshConnectionException">Client is not connected.</exception>
        /// <exception cref="SftpPermissionDeniedException">Permission to change permission on the path(s) was denied by the remote host. <para>-or-</para> A SSH command was denied by the server.</exception>
        /// <exception cref="SftpPathNotFoundException"><paramref name="path"/> was not found on the remote host.</exception>
        /// <exception cref="SshException">A SSH error where <see cref="Exception.Message"/> is the message from the remote host.</exception>
        /// <exception cref="ObjectDisposedException">The method was called after the client was disposed.</exception>
        public void ChangePermissions(string path, short mode)
        {
            var file = Get(path);
            file.SetPermissions(mode);
        }

        /// <summary>
        /// Creates remote directory specified by path.
        /// </summary>
        /// <param name="path">Directory path to create.</param>
        /// <exception cref="ArgumentException"><paramref name="path"/> is <see langword="null"/> or contains only whitespace characters.</exception>
        /// <exception cref="SshConnectionException">Client is not connected.</exception>
        /// <exception cref="SftpPermissionDeniedException">Permission to create the directory was denied by the remote host. <para>-or-</para> A SSH command was denied by the server.</exception>
        /// <exception cref="SshException">A SSH error where <see cref="Exception.Message"/> is the message from the remote host.</exception>
        /// <exception cref="ObjectDisposedException">The method was called after the client was disposed.</exception>
        public void CreateDirectory(string path)
        {
            CheckDisposed();
            ThrowHelper.ThrowIfNullOrWhiteSpace(path);

            if (_sftpSession is null)
            {
                throw new SshConnectionException("Client not connected.");
            }

            var fullPath = _sftpSession.GetCanonicalPath(path);

            _sftpSession.RequestMkDir(fullPath);
        }

        /// <summary>
        /// Asynchronously requests to create a remote directory specified by path.
        /// </summary>
        /// <param name="path">Directory path to create.</param>
        /// <param name="cancellationToken">The <see cref="CancellationToken"/> to observe.</param>
        /// <returns>A <see cref="Task"/> that represents the asynchronous create directory operation.</returns>
        /// <exception cref="ArgumentException"><paramref name="path"/> is <see langword="null"/> or contains only whitespace characters.</exception>
        /// <exception cref="SshConnectionException">Client is not connected.</exception>
        /// <exception cref="SftpPermissionDeniedException">Permission to create the directory was denied by the remote host. <para>-or-</para> A SSH command was denied by the server.</exception>
        /// <exception cref="SshException">A SSH error where <see cref="Exception.Message"/> is the message from the remote host.</exception>
        /// <exception cref="ObjectDisposedException">The method was called after the client was disposed.</exception>
        public async Task CreateDirectoryAsync(string path, CancellationToken cancellationToken = default)
        {
            CheckDisposed();
            ThrowHelper.ThrowIfNullOrWhiteSpace(path);

            if (_sftpSession is null)
            {
                throw new SshConnectionException("Client not connected.");
            }

            var fullPath = await _sftpSession.GetCanonicalPathAsync(path, cancellationToken).ConfigureAwait(false);

            await _sftpSession.RequestMkDirAsync(fullPath, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Deletes remote directory specified by path.
        /// </summary>
        /// <param name="path">Directory to be deleted path.</param>
        /// <exception cref="ArgumentException"><paramref name="path"/> is <see langword="null"/> or contains only whitespace characters.</exception>
        /// <exception cref="SshConnectionException">Client is not connected.</exception>
        /// <exception cref="SftpPathNotFoundException"><paramref name="path"/> was not found on the remote host.</exception>
        /// <exception cref="SftpPermissionDeniedException">Permission to delete the directory was denied by the remote host. <para>-or-</para> A SSH command was denied by the server.</exception>
        /// <exception cref="SshException">A SSH error where <see cref="Exception.Message"/> is the message from the remote host.</exception>
        /// <exception cref="ObjectDisposedException">The method was called after the client was disposed.</exception>
        public void DeleteDirectory(string path)
        {
            CheckDisposed();
            ThrowHelper.ThrowIfNullOrWhiteSpace(path);

            if (_sftpSession is null)
            {
                throw new SshConnectionException("Client not connected.");
            }

            var fullPath = _sftpSession.GetCanonicalPath(path);

            _sftpSession.RequestRmDir(fullPath);
        }

        /// <inheritdoc />
        public async Task DeleteDirectoryAsync(string path, CancellationToken cancellationToken = default)
        {
            CheckDisposed();
            ThrowHelper.ThrowIfNullOrWhiteSpace(path);

            if (_sftpSession is null)
            {
                throw new SshConnectionException("Client not connected.");
            }

            cancellationToken.ThrowIfCancellationRequested();

            var fullPath = await _sftpSession.GetCanonicalPathAsync(path, cancellationToken).ConfigureAwait(false);

            await _sftpSession.RequestRmDirAsync(fullPath, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Deletes remote file specified by path.
        /// </summary>
        /// <param name="path">File to be deleted path.</param>
        /// <exception cref="ArgumentException"><paramref name="path"/> is <see langword="null"/> or contains only whitespace characters.</exception>
        /// <exception cref="SshConnectionException">Client is not connected.</exception>
        /// <exception cref="SftpPathNotFoundException"><paramref name="path"/> was not found on the remote host.</exception>
        /// <exception cref="SftpPermissionDeniedException">Permission to delete the file was denied by the remote host. <para>-or-</para> A SSH command was denied by the server.</exception>
        /// <exception cref="SshException">A SSH error where <see cref="Exception.Message"/> is the message from the remote host.</exception>
        /// <exception cref="ObjectDisposedException">The method was called after the client was disposed.</exception>
        public void DeleteFile(string path)
        {
            CheckDisposed();
            ThrowHelper.ThrowIfNullOrWhiteSpace(path);

            if (_sftpSession is null)
            {
                throw new SshConnectionException("Client not connected.");
            }

            var fullPath = _sftpSession.GetCanonicalPath(path);

            _sftpSession.RequestRemove(fullPath);
        }

        /// <inheritdoc />
        public async Task DeleteFileAsync(string path, CancellationToken cancellationToken)
        {
            CheckDisposed();
            ThrowHelper.ThrowIfNullOrWhiteSpace(path);

            if (_sftpSession is null)
            {
                throw new SshConnectionException("Client not connected.");
            }

            cancellationToken.ThrowIfCancellationRequested();

            var fullPath = await _sftpSession.GetCanonicalPathAsync(path, cancellationToken).ConfigureAwait(false);
            await _sftpSession.RequestRemoveAsync(fullPath, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Renames remote file from old path to new path.
        /// </summary>
        /// <param name="oldPath">Path to the old file location.</param>
        /// <param name="newPath">Path to the new file location.</param>
        /// <exception cref="ArgumentNullException"><paramref name="oldPath"/> is <see langword="null"/>. <para>-or-</para> or <paramref name="newPath"/> is <see langword="null"/>.</exception>
        /// <exception cref="SshConnectionException">Client is not connected.</exception>
        /// <exception cref="SftpPermissionDeniedException">Permission to rename the file was denied by the remote host. <para>-or-</para> A SSH command was denied by the server.</exception>
        /// <exception cref="SshException">A SSH error where <see cref="Exception.Message"/> is the message from the remote host.</exception>
        /// <exception cref="ObjectDisposedException">The method was called after the client was disposed.</exception>
        public void RenameFile(string oldPath, string newPath)
        {
            RenameFile(oldPath, newPath, isPosix: false);
        }

        /// <summary>
        /// Renames remote file from old path to new path.
        /// </summary>
        /// <param name="oldPath">Path to the old file location.</param>
        /// <param name="newPath">Path to the new file location.</param>
        /// <param name="isPosix">if set to <see langword="true"/> then perform a posix rename.</param>
        /// <exception cref="ArgumentNullException"><paramref name="oldPath" /> is <see langword="null"/>. <para>-or-</para> or <paramref name="newPath" /> is <see langword="null"/>.</exception>
        /// <exception cref="SshConnectionException">Client is not connected.</exception>
        /// <exception cref="SftpPermissionDeniedException">Permission to rename the file was denied by the remote host. <para>-or-</para> A SSH command was denied by the server.</exception>
        /// <exception cref="SshException">A SSH error where <see cref="Exception.Message" /> is the message from the remote host.</exception>
        /// <exception cref="ObjectDisposedException">The method was called after the client was disposed.</exception>
        public void RenameFile(string oldPath, string newPath, bool isPosix)
        {
            CheckDisposed();
            ThrowHelper.ThrowIfNull(oldPath);
            ThrowHelper.ThrowIfNull(newPath);

            if (_sftpSession is null)
            {
                throw new SshConnectionException("Client not connected.");
            }

            var oldFullPath = _sftpSession.GetCanonicalPath(oldPath);

            var newFullPath = _sftpSession.GetCanonicalPath(newPath);

            if (isPosix)
            {
                _sftpSession.RequestPosixRename(oldFullPath, newFullPath);
            }
            else
            {
                _sftpSession.RequestRename(oldFullPath, newFullPath);
            }
        }

        /// <summary>
        /// Asynchronously renames remote file from old path to new path.
        /// </summary>
        /// <param name="oldPath">Path to the old file location.</param>
        /// <param name="newPath">Path to the new file location.</param>
        /// <param name="cancellationToken">The <see cref="CancellationToken"/> to observe.</param>
        /// <returns>A <see cref="Task"/> that represents the asynchronous rename operation.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="oldPath"/> is <see langword="null"/>. <para>-or-</para> or <paramref name="newPath"/> is <see langword="null"/>.</exception>
        /// <exception cref="SshConnectionException">Client is not connected.</exception>
        /// <exception cref="SftpPermissionDeniedException">Permission to rename the file was denied by the remote host. <para>-or-</para> A SSH command was denied by the server.</exception>
        /// <exception cref="SshException">A SSH error where <see cref="Exception.Message"/> is the message from the remote host.</exception>
        /// <exception cref="ObjectDisposedException">The method was called after the client was disposed.</exception>
        public async Task RenameFileAsync(string oldPath, string newPath, CancellationToken cancellationToken)
        {
            CheckDisposed();
            ThrowHelper.ThrowIfNull(oldPath);
            ThrowHelper.ThrowIfNull(newPath);

            if (_sftpSession is null)
            {
                throw new SshConnectionException("Client not connected.");
            }

            cancellationToken.ThrowIfCancellationRequested();

            var oldFullPath = await _sftpSession.GetCanonicalPathAsync(oldPath, cancellationToken).ConfigureAwait(false);
            var newFullPath = await _sftpSession.GetCanonicalPathAsync(newPath, cancellationToken).ConfigureAwait(false);
            await _sftpSession.RequestRenameAsync(oldFullPath, newFullPath, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Creates a symbolic link from old path to new path.
        /// </summary>
        /// <param name="path">The old path.</param>
        /// <param name="linkPath">The new path.</param>
        /// <exception cref="ArgumentException"><paramref name="path"/> is <see langword="null"/>. <para>-or-</para> <paramref name="linkPath"/> is <see langword="null"/> or contains only whitespace characters.</exception>
        /// <exception cref="SshConnectionException">Client is not connected.</exception>
        /// <exception cref="SftpPermissionDeniedException">Permission to create the symbolic link was denied by the remote host. <para>-or-</para> A SSH command was denied by the server.</exception>
        /// <exception cref="SshException">A SSH error where <see cref="Exception.Message"/> is the message from the remote host.</exception>
        /// <exception cref="ObjectDisposedException">The method was called after the client was disposed.</exception>
        public void SymbolicLink(string path, string linkPath)
        {
            CheckDisposed();
            ThrowHelper.ThrowIfNullOrWhiteSpace(path);
            ThrowHelper.ThrowIfNullOrWhiteSpace(linkPath);

            if (_sftpSession is null)
            {
                throw new SshConnectionException("Client not connected.");
            }

            var fullPath = _sftpSession.GetCanonicalPath(path);

            var linkFullPath = _sftpSession.GetCanonicalPath(linkPath);

            _sftpSession.RequestSymLink(fullPath, linkFullPath);
        }

        /// <summary>
        /// Retrieves list of files in remote directory.
        /// </summary>
        /// <param name="path">The path.</param>
        /// <param name="listCallback">The list callback.</param>
        /// <returns>
        /// A list of files.
        /// </returns>
        /// <exception cref="ArgumentNullException"><paramref name="path" /> is <see langword="null"/>.</exception>
        /// <exception cref="SshConnectionException">Client is not connected.</exception>
        /// <exception cref="SftpPermissionDeniedException">Permission to list the contents of the directory was denied by the remote host. <para>-or-</para> A SSH command was denied by the server.</exception>
        /// <exception cref="SshException">A SSH error where <see cref="Exception.Message" /> is the message from the remote host.</exception>
        /// <exception cref="ObjectDisposedException">The method was called after the client was disposed.</exception>
        public IEnumerable<ISftpFile> ListDirectory(string path, Action<int>? listCallback = null)
        {
            CheckDisposed();

            return InternalListDirectory(path, asyncResult: null, listCallback);
        }

        /// <summary>
        /// Asynchronously enumerates the files in remote directory.
        /// </summary>
        /// <param name="path">The path.</param>
        /// <param name="cancellationToken">The <see cref="CancellationToken"/> to observe.</param>
        /// <returns>
        /// An <see cref="IAsyncEnumerable{T}"/> of <see cref="ISftpFile"/> that represents the asynchronous enumeration operation.
        /// The enumeration contains an async stream of <see cref="ISftpFile"/> for the files in the directory specified by <paramref name="path" />.
        /// </returns>
        /// <exception cref="ArgumentNullException"><paramref name="path" /> is <see langword="null"/>.</exception>
        /// <exception cref="SshConnectionException">Client is not connected.</exception>
        /// <exception cref="SftpPermissionDeniedException">Permission to list the contents of the directory was denied by the remote host. <para>-or-</para> A SSH command was denied by the server.</exception>
        /// <exception cref="SshException">A SSH error where <see cref="Exception.Message" /> is the message from the remote host.</exception>
        /// <exception cref="ObjectDisposedException">The method was called after the client was disposed.</exception>
        public async IAsyncEnumerable<ISftpFile> ListDirectoryAsync(string path, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            CheckDisposed();
            ThrowHelper.ThrowIfNull(path);

            if (_sftpSession is null)
            {
                throw new SshConnectionException("Client not connected.");
            }

            cancellationToken.ThrowIfCancellationRequested();

            var fullPath = await _sftpSession.GetCanonicalPathAsync(path, cancellationToken).ConfigureAwait(false);

            var handle = await _sftpSession.RequestOpenDirAsync(fullPath, cancellationToken).ConfigureAwait(false);
            try
            {
                var basePath = (fullPath[fullPath.Length - 1] == '/') ?
                    fullPath :
                    fullPath + '/';

                while (true)
                {
                    var files = await _sftpSession.RequestReadDirAsync(handle, cancellationToken).ConfigureAwait(false);
                    if (files is null)
                    {
                        break;
                    }

                    foreach (var file in files)
                    {
                        yield return new SftpFile(_sftpSession, basePath + file.Key, file.Value);
                    }
                }
            }
            finally
            {
                await _sftpSession.RequestCloseAsync(handle, cancellationToken).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Begins an asynchronous operation of retrieving list of files in remote directory.
        /// </summary>
        /// <param name="path">The path.</param>
        /// <param name="asyncCallback">The method to be called when the asynchronous write operation is completed.</param>
        /// <param name="state">A user-provided object that distinguishes this particular asynchronous write request from other requests.</param>
        /// <param name="listCallback">The list callback.</param>
        /// <returns>
        /// An <see cref="IAsyncResult" /> that references the asynchronous operation.
        /// </returns>
        /// <exception cref="ObjectDisposedException">The method was called after the client was disposed.</exception>
        public IAsyncResult BeginListDirectory(string path, AsyncCallback? asyncCallback, object? state, Action<int>? listCallback = null)
        {
            CheckDisposed();

            var asyncResult = new SftpListDirectoryAsyncResult(asyncCallback, state);

            ThreadAbstraction.ExecuteThread(() =>
            {
                try
                {
                    var result = InternalListDirectory(path, asyncResult, listCallback);

                    asyncResult.SetAsCompleted(result, completedSynchronously: false);
                }
                catch (Exception exp)
                {
                    asyncResult.SetAsCompleted(exp, completedSynchronously: false);
                }
            });

            return asyncResult;
        }

        /// <summary>
        /// Ends an asynchronous operation of retrieving list of files in remote directory.
        /// </summary>
        /// <param name="asyncResult">The pending asynchronous SFTP request.</param>
        /// <returns>
        /// A list of files.
        /// </returns>
        /// <exception cref="ArgumentException">The <see cref="IAsyncResult"/> object did not come from the corresponding async method on this type.<para>-or-</para><see cref="EndListDirectory(IAsyncResult)"/> was called multiple times with the same <see cref="IAsyncResult"/>.</exception>
        public IEnumerable<ISftpFile> EndListDirectory(IAsyncResult asyncResult)
        {
            if (asyncResult is not SftpListDirectoryAsyncResult ar || ar.EndInvokeCalled)
            {
                throw new ArgumentException("Either the IAsyncResult object did not come from the corresponding async method on this type, or EndExecute was called multiple times with the same IAsyncResult.");
            }

            // Wait for operation to complete, then return result or throw exception
            return ar.EndInvoke();
        }

        /// <summary>
        /// Gets reference to remote file or directory.
        /// </summary>
        /// <param name="path">The path.</param>
        /// <returns>
        /// A reference to <see cref="ISftpFile"/> file object.
        /// </returns>
        /// <exception cref="SshConnectionException">Client is not connected.</exception>
        /// <exception cref="SftpPathNotFoundException"><paramref name="path"/> was not found on the remote host.</exception>
        /// <exception cref="ArgumentNullException"><paramref name="path" /> is <see langword="null"/>.</exception>
        /// <exception cref="ObjectDisposedException">The method was called after the client was disposed.</exception>
        public ISftpFile Get(string path)
        {
            CheckDisposed();
            ThrowHelper.ThrowIfNull(path);

            if (_sftpSession is null)
            {
                throw new SshConnectionException("Client not connected.");
            }

            var fullPath = _sftpSession.GetCanonicalPath(path);

            var attributes = _sftpSession.RequestLStat(fullPath);

            return new SftpFile(_sftpSession, fullPath, attributes);
        }

        /// <summary>
        /// Gets reference to remote file or directory.
        /// </summary>
        /// <param name="path">The path.</param>
        /// <param name="cancellationToken">The <see cref="CancellationToken"/> to observe.</param>
        /// <returns>
        /// A <see cref="Task{ISftpFile}"/> that represents the get operation.
        /// The task result contains the reference to <see cref="ISftpFile"/> file object.
        /// </returns>
        /// <exception cref="SshConnectionException">Client is not connected.</exception>
        /// <exception cref="SftpPathNotFoundException"><paramref name="path"/> was not found on the remote host.</exception>
        /// <exception cref="ArgumentNullException"><paramref name="path" /> is <see langword="null"/>.</exception>
        /// <exception cref="ObjectDisposedException">The method was called after the client was disposed.</exception>
        public async Task<ISftpFile> GetAsync(string path, CancellationToken cancellationToken)
        {
            CheckDisposed();
            ThrowHelper.ThrowIfNull(path);

            if (_sftpSession is null)
            {
                throw new SshConnectionException("Client not connected.");
            }

            cancellationToken.ThrowIfCancellationRequested();

            var fullPath = await _sftpSession.GetCanonicalPathAsync(path, cancellationToken).ConfigureAwait(false);

            var attributes = await _sftpSession.RequestLStatAsync(fullPath, cancellationToken).ConfigureAwait(false);

            return new SftpFile(_sftpSession, fullPath, attributes);
        }

        /// <summary>
        /// Checks whether file or directory exists.
        /// </summary>
        /// <param name="path">The path.</param>
        /// <returns>
        /// <see langword="true"/> if directory or file exists; otherwise <see langword="false"/>.
        /// </returns>
        /// <exception cref="ArgumentException"><paramref name="path"/> is <see langword="null"/> or contains only whitespace characters.</exception>
        /// <exception cref="SshConnectionException">Client is not connected.</exception>
        /// <exception cref="SftpPermissionDeniedException">Permission to perform the operation was denied by the remote host. <para>-or-</para> A SSH command was denied by the server.</exception>
        /// <exception cref="SshException">A SSH error where <see cref="Exception.Message"/> is the message from the remote host.</exception>
        /// <exception cref="ObjectDisposedException">The method was called after the client was disposed.</exception>
        public bool Exists(string path)
        {
            CheckDisposed();
            ThrowHelper.ThrowIfNullOrWhiteSpace(path);

            if (_sftpSession is null)
            {
                throw new SshConnectionException("Client not connected.");
            }

            var fullPath = _sftpSession.GetCanonicalPath(path);

            /*
             * Using SSH_FXP_REALPATH is not an alternative as the SFTP specification has not always
             * been clear on how the server should respond when the specified path is not present on
             * the server:
             *
             * SSH 1 to 4:
             * No mention of how the server should respond if the path is not present on the server.
             *
             * SSH 5:
             * The server SHOULD fail the request if the path is not present on the server.
             *
             * SSH 6:
             * Draft 06: The server SHOULD fail the request if the path is not present on the server.
             * Draft 07 to 13: The server MUST NOT fail the request if the path does not exist.
             *
             * Note that SSH 6 (draft 06 and forward) allows for more control options, but we
             * currently only support up to v3.
             */

            try
            {
                _ = _sftpSession.RequestLStat(fullPath);
                return true;
            }
            catch (SftpPathNotFoundException)
            {
                return false;
            }
        }

        /// <summary>
        /// Checks whether file or directory exists.
        /// </summary>
        /// <param name="path">The path.</param>
        /// <param name="cancellationToken">The <see cref="CancellationToken"/> to observe.</param>
        /// <returns>
        /// A <see cref="Task{T}"/> that represents the exists operation.
        /// The task result contains <see langword="true"/> if directory or file exists; otherwise <see langword="false"/>.
        /// </returns>
        /// <exception cref="ArgumentException"><paramref name="path"/> is <see langword="null"/> or contains only whitespace characters.</exception>
        /// <exception cref="SshConnectionException">Client is not connected.</exception>
        /// <exception cref="SftpPermissionDeniedException">Permission to perform the operation was denied by the remote host. <para>-or-</para> A SSH command was denied by the server.</exception>
        /// <exception cref="SshException">A SSH error where <see cref="Exception.Message"/> is the message from the remote host.</exception>
        /// <exception cref="ObjectDisposedException">The method was called after the client was disposed.</exception>
        public async Task<bool> ExistsAsync(string path, CancellationToken cancellationToken = default)
        {
            CheckDisposed();
            ThrowHelper.ThrowIfNullOrWhiteSpace(path);

            if (_sftpSession is null)
            {
                throw new SshConnectionException("Client not connected.");
            }

            cancellationToken.ThrowIfCancellationRequested();

            var fullPath = await _sftpSession.GetCanonicalPathAsync(path, cancellationToken).ConfigureAwait(false);

            /*
             * Using SSH_FXP_REALPATH is not an alternative as the SFTP specification has not always
             * been clear on how the server should respond when the specified path is not present on
             * the server:
             *
             * SSH 1 to 4:
             * No mention of how the server should respond if the path is not present on the server.
             *
             * SSH 5:
             * The server SHOULD fail the request if the path is not present on the server.
             *
             * SSH 6:
             * Draft 06: The server SHOULD fail the request if the path is not present on the server.
             * Draft 07 to 13: The server MUST NOT fail the request if the path does not exist.
             *
             * Note that SSH 6 (draft 06 and forward) allows for more control options, but we
             * currently only support up to v3.
             */

            try
            {
                _ = await _sftpSession.RequestLStatAsync(fullPath, cancellationToken).ConfigureAwait(false);
                return true;
            }
            catch (SftpPathNotFoundException)
            {
                return false;
            }
        }

        /// <inheritdoc />
        public void DownloadFile(string path, Stream output, Action<ulong>? downloadCallback = null)
        {
            ThrowHelper.ThrowIfNullOrWhiteSpace(path);
            ThrowHelper.ThrowIfNull(output);
            CheckDisposed();

            InternalDownloadFile(
                path,
                output,
                asyncResult: null,
                downloadCallback,
                isAsync: false,
                CancellationToken.None).GetAwaiter().GetResult();
        }

        /// <inheritdoc />
        public Task DownloadFileAsync(string path, Stream output, CancellationToken cancellationToken = default)
        {
            ThrowHelper.ThrowIfNullOrWhiteSpace(path);
            ThrowHelper.ThrowIfNull(output);
            CheckDisposed();

            return InternalDownloadFile(
                path,
                output,
                asyncResult: null,
                downloadCallback: null,
                isAsync: true,
                cancellationToken);
        }

        /// <summary>
        /// Begins an asynchronous file downloading into the stream.
        /// </summary>
        /// <param name="path">The path.</param>
        /// <param name="output">The output.</param>
        /// <returns>
        /// An <see cref="IAsyncResult" /> that references the asynchronous operation.
        /// </returns>
        /// <exception cref="ArgumentNullException"><paramref name="output" /> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentException"><paramref name="path" /> is <see langword="null"/> or contains only whitespace characters.</exception>
        /// <exception cref="SshConnectionException">Client is not connected.</exception>
        /// <exception cref="SftpPermissionDeniedException">Permission to perform the operation was denied by the remote host. <para>-or-</para> A SSH command was denied by the server.</exception>
        /// <exception cref="SshException">A SSH error where <see cref="Exception.Message" /> is the message from the remote host.</exception>
        /// <exception cref="ObjectDisposedException">The method was called after the client was disposed.</exception>
        /// <remarks>
        /// Method calls made by this method to <paramref name="output" />, may under certain conditions result in exceptions thrown by the stream.
        /// </remarks>
        public IAsyncResult BeginDownloadFile(string path, Stream output)
        {
            return BeginDownloadFile(path, output, asyncCallback: null, state: null);
        }

        /// <summary>
        /// Begins an asynchronous file downloading into the stream.
        /// </summary>
        /// <param name="path">The path.</param>
        /// <param name="output">The output.</param>
        /// <param name="asyncCallback">The method to be called when the asynchronous write operation is completed.</param>
        /// <returns>
        /// An <see cref="IAsyncResult" /> that references the asynchronous operation.
        /// </returns>
        /// <exception cref="ArgumentNullException"><paramref name="output" /> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentException"><paramref name="path" /> is <see langword="null"/> or contains only whitespace characters.</exception>
        /// <exception cref="SshConnectionException">Client is not connected.</exception>
        /// <exception cref="SftpPermissionDeniedException">Permission to perform the operation was denied by the remote host. <para>-or-</para> A SSH command was denied by the server.</exception>
        /// <exception cref="SshException">A SSH error where <see cref="Exception.Message" /> is the message from the remote host.</exception>
        /// <exception cref="ObjectDisposedException">The method was called after the client was disposed.</exception>
        /// <remarks>
        /// Method calls made by this method to <paramref name="output" />, may under certain conditions result in exceptions thrown by the stream.
        /// </remarks>
        public IAsyncResult BeginDownloadFile(string path, Stream output, AsyncCallback? asyncCallback)
        {
            return BeginDownloadFile(path, output, asyncCallback, state: null);
        }

        /// <summary>
        /// Begins an asynchronous file downloading into the stream.
        /// </summary>
        /// <param name="path">The path.</param>
        /// <param name="output">The output.</param>
        /// <param name="asyncCallback">The method to be called when the asynchronous write operation is completed.</param>
        /// <param name="state">A user-provided object that distinguishes this particular asynchronous write request from other requests.</param>
        /// <param name="downloadCallback">The download callback.</param>
        /// <returns>
        /// An <see cref="IAsyncResult" /> that references the asynchronous operation.
        /// </returns>
        /// <exception cref="ArgumentNullException"><paramref name="output" /> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentException"><paramref name="path" /> is <see langword="null"/> or contains only whitespace characters.</exception>
        /// <exception cref="ObjectDisposedException">The method was called after the client was disposed.</exception>
        /// <remarks>
        /// Method calls made by this method to <paramref name="output" />, may under certain conditions result in exceptions thrown by the stream.
        /// </remarks>
        public IAsyncResult BeginDownloadFile(string path, Stream output, AsyncCallback? asyncCallback, object? state, Action<ulong>? downloadCallback = null)
        {
            ThrowHelper.ThrowIfNullOrWhiteSpace(path);
            ThrowHelper.ThrowIfNull(output);
            CheckDisposed();

            var asyncResult = new SftpDownloadAsyncResult(asyncCallback, state);

            _ = DoDownloadAndSetResult();

            async Task DoDownloadAndSetResult()
            {
                try
                {
                    await InternalDownloadFile(
                        path,
                        output,
                        asyncResult,
                        downloadCallback,
                        isAsync: true,
                        CancellationToken.None).ConfigureAwait(false);

                    asyncResult.SetAsCompleted(exception: null, completedSynchronously: false);
                }
                catch (Exception exp)
                {
                    asyncResult.SetAsCompleted(exp, completedSynchronously: false);
                }
            }

            return asyncResult;
        }

        /// <summary>
        /// Ends an asynchronous file downloading into the stream.
        /// </summary>
        /// <param name="asyncResult">The pending asynchronous SFTP request.</param>
        /// <exception cref="ArgumentException">The <see cref="IAsyncResult"/> object did not come from the corresponding async method on this type.<para>-or-</para><see cref="EndDownloadFile(IAsyncResult)"/> was called multiple times with the same <see cref="IAsyncResult"/>.</exception>
        /// <exception cref="SshConnectionException">Client is not connected.</exception>
        /// <exception cref="SftpPermissionDeniedException">Permission to perform the operation was denied by the remote host. <para>-or-</para> A SSH command was denied by the server.</exception>
        /// <exception cref="SftpPathNotFoundException">The path was not found on the remote host.</exception>
        /// <exception cref="SshException">A SSH error where <see cref="Exception.Message" /> is the message from the remote host.</exception>
        public void EndDownloadFile(IAsyncResult asyncResult)
        {
            if (asyncResult is not SftpDownloadAsyncResult ar || ar.EndInvokeCalled)
            {
                throw new ArgumentException("Either the IAsyncResult object did not come from the corresponding async method on this type, or EndExecute was called multiple times with the same IAsyncResult.");
            }

            // Wait for operation to complete, then return result or throw exception
            ar.EndInvoke();
        }

        /// <inheritdoc/>
        public void UploadFile(Stream input, string path, Action<ulong>? uploadCallback = null)
        {
            UploadFile(input, path, canOverride: true, uploadCallback);
        }

        /// <inheritdoc/>
        public void UploadFile(Stream input, string path, bool canOverride, Action<ulong>? uploadCallback = null)
        {
            ThrowHelper.ThrowIfNull(input);
            ThrowHelper.ThrowIfNullOrWhiteSpace(path);
            CheckDisposed();

            var flags = Flags.Write | Flags.Truncate;

            if (canOverride)
            {
                flags |= Flags.CreateNewOrOpen;
            }
            else
            {
                flags |= Flags.CreateNew;
            }

            InternalUploadFile(
                input,
                path,
                flags,
                asyncResult: null,
                uploadCallback,
                isAsync: false,
                CancellationToken.None).GetAwaiter().GetResult();
        }

        /// <inheritdoc />
        public Task UploadFileAsync(Stream input, string path, CancellationToken cancellationToken = default)
        {
            ThrowHelper.ThrowIfNull(input);
            ThrowHelper.ThrowIfNullOrWhiteSpace(path);
            CheckDisposed();

            return InternalUploadFile(
                input,
                path,
                Flags.Write | Flags.Truncate | Flags.CreateNewOrOpen,
                asyncResult: null,
                uploadCallback: null,
                isAsync: true,
                cancellationToken);
        }

        /// <summary>
        /// Begins an asynchronous uploading the stream into remote file.
        /// </summary>
        /// <param name="input">Data input stream.</param>
        /// <param name="path">Remote file path.</param>
        /// <returns>
        /// An <see cref="IAsyncResult" /> that references the asynchronous operation.
        /// </returns>
        /// <exception cref="ArgumentNullException"><paramref name="input" /> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentException"><paramref name="path" /> is <see langword="null"/> or contains only whitespace characters.</exception>
        /// <exception cref="SshConnectionException">Client is not connected.</exception>
        /// <exception cref="SftpPermissionDeniedException">Permission to list the contents of the directory was denied by the remote host. <para>-or-</para> A SSH command was denied by the server.</exception>
        /// <exception cref="SshException">A SSH error where <see cref="Exception.Message" /> is the message from the remote host.</exception>
        /// <exception cref="ObjectDisposedException">The method was called after the client was disposed.</exception>
        /// <remarks>
        /// <para>
        /// Method calls made by this method to <paramref name="input" />, may under certain conditions result in exceptions thrown by the stream.
        /// </para>
        /// <para>
        /// If the remote file already exists, it is overwritten and truncated.
        /// </para>
        /// </remarks>
        public IAsyncResult BeginUploadFile(Stream input, string path)
        {
            return BeginUploadFile(input, path, canOverride: true, asyncCallback: null, state: null);
        }

        /// <summary>
        /// Begins an asynchronous uploading the stream into remote file.
        /// </summary>
        /// <param name="input">Data input stream.</param>
        /// <param name="path">Remote file path.</param>
        /// <param name="asyncCallback">The method to be called when the asynchronous write operation is completed.</param>
        /// <returns>
        /// An <see cref="IAsyncResult" /> that references the asynchronous operation.
        /// </returns>
        /// <exception cref="ArgumentNullException"><paramref name="input" /> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentException"><paramref name="path" /> is <see langword="null"/> or contains only whitespace characters.</exception>
        /// <exception cref="SshConnectionException">Client is not connected.</exception>
        /// <exception cref="SftpPermissionDeniedException">Permission to list the contents of the directory was denied by the remote host. <para>-or-</para> A SSH command was denied by the server.</exception>
        /// <exception cref="SshException">A SSH error where <see cref="Exception.Message" /> is the message from the remote host.</exception>
        /// <exception cref="ObjectDisposedException">The method was called after the client was disposed.</exception>
        /// <remarks>
        /// <para>
        /// Method calls made by this method to <paramref name="input" />, may under certain conditions result in exceptions thrown by the stream.
        /// </para>
        /// <para>
        /// If the remote file already exists, it is overwritten and truncated.
        /// </para>
        /// </remarks>
        public IAsyncResult BeginUploadFile(Stream input, string path, AsyncCallback? asyncCallback)
        {
            return BeginUploadFile(input, path, canOverride: true, asyncCallback, state: null);
        }

        /// <summary>
        /// Begins an asynchronous uploading the stream into remote file.
        /// </summary>
        /// <param name="input">Data input stream.</param>
        /// <param name="path">Remote file path.</param>
        /// <param name="asyncCallback">The method to be called when the asynchronous write operation is completed.</param>
        /// <param name="state">A user-provided object that distinguishes this particular asynchronous write request from other requests.</param>
        /// <param name="uploadCallback">The upload callback.</param>
        /// <returns>
        /// An <see cref="IAsyncResult" /> that references the asynchronous operation.
        /// </returns>
        /// <exception cref="ArgumentNullException"><paramref name="input" /> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentException"><paramref name="path" /> is <see langword="null"/> or contains only whitespace characters.</exception>
        /// <exception cref="SshConnectionException">Client is not connected.</exception>
        /// <exception cref="SftpPermissionDeniedException">Permission to list the contents of the directory was denied by the remote host. <para>-or-</para> A SSH command was denied by the server.</exception>
        /// <exception cref="SshException">A SSH error where <see cref="Exception.Message" /> is the message from the remote host.</exception>
        /// <exception cref="ObjectDisposedException">The method was called after the client was disposed.</exception>
        /// <remarks>
        /// <para>
        /// Method calls made by this method to <paramref name="input" />, may under certain conditions result in exceptions thrown by the stream.
        /// </para>
        /// <para>
        /// If the remote file already exists, it is overwritten and truncated.
        /// </para>
        /// </remarks>
        public IAsyncResult BeginUploadFile(Stream input, string path, AsyncCallback? asyncCallback, object? state, Action<ulong>? uploadCallback = null)
        {
            return BeginUploadFile(input, path, canOverride: true, asyncCallback, state, uploadCallback);
        }

        /// <summary>
        /// Begins an asynchronous uploading the stream into remote file.
        /// </summary>
        /// <param name="input">Data input stream.</param>
        /// <param name="path">Remote file path.</param>
        /// <param name="canOverride">Specified whether an existing file can be overwritten.</param>
        /// <param name="asyncCallback">The method to be called when the asynchronous write operation is completed.</param>
        /// <param name="state">A user-provided object that distinguishes this particular asynchronous write request from other requests.</param>
        /// <param name="uploadCallback">The upload callback.</param>
        /// <returns>
        /// An <see cref="IAsyncResult" /> that references the asynchronous operation.
        /// </returns>
        /// <exception cref="ArgumentNullException"><paramref name="input" /> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentException"><paramref name="path" /> is <see langword="null"/> or contains only whitespace characters.</exception>
        /// <exception cref="ObjectDisposedException">The method was called after the client was disposed.</exception>
        /// <remarks>
        /// <para>
        /// Method calls made by this method to <paramref name="input" />, may under certain conditions result in exceptions thrown by the stream.
        /// </para>
        /// <para>
        /// When <paramref name="path"/> refers to an existing file, set <paramref name="canOverride"/> to <see langword="true"/> to overwrite and truncate that file.
        /// If <paramref name="canOverride"/> is <see langword="false"/>, the upload will fail and <see cref="EndUploadFile(IAsyncResult)"/> will throw an
        /// <see cref="SshException"/>.
        /// </para>
        /// </remarks>
        public IAsyncResult BeginUploadFile(Stream input, string path, bool canOverride, AsyncCallback? asyncCallback, object? state, Action<ulong>? uploadCallback = null)
        {
            ThrowHelper.ThrowIfNull(input);
            ThrowHelper.ThrowIfNullOrWhiteSpace(path);
            CheckDisposed();

            var flags = Flags.Write | Flags.Truncate;

            if (canOverride)
            {
                flags |= Flags.CreateNewOrOpen;
            }
            else
            {
                flags |= Flags.CreateNew;
            }

            var asyncResult = new SftpUploadAsyncResult(asyncCallback, state);

            _ = DoUploadAndSetResult();

            async Task DoUploadAndSetResult()
            {
                try
                {
                    await InternalUploadFile(
                        input,
                        path,
                        flags,
                        asyncResult,
                        uploadCallback,
                        isAsync: true,
                        CancellationToken.None).ConfigureAwait(false);

                    asyncResult.SetAsCompleted(exception: null, completedSynchronously: false);
                }
                catch (Exception exp)
                {
                    asyncResult.SetAsCompleted(exp, completedSynchronously: false);
                }
            }

            return asyncResult;
        }

        /// <summary>
        /// Ends an asynchronous uploading the stream into remote file.
        /// </summary>
        /// <param name="asyncResult">The pending asynchronous SFTP request.</param>
        /// <exception cref="ArgumentException">The <see cref="IAsyncResult"/> object did not come from the corresponding async method on this type.<para>-or-</para><see cref="EndUploadFile(IAsyncResult)"/> was called multiple times with the same <see cref="IAsyncResult"/>.</exception>
        /// <exception cref="SshConnectionException">Client is not connected.</exception>
        /// <exception cref="SftpPathNotFoundException">The directory of the file was not found on the remote host.</exception>
        /// <exception cref="SftpPermissionDeniedException">Permission to upload the file was denied by the remote host. <para>-or-</para> A SSH command was denied by the server.</exception>
        /// <exception cref="SshException">A SSH error where <see cref="Exception.Message" /> is the message from the remote host.</exception>
        public void EndUploadFile(IAsyncResult asyncResult)
        {
            if (asyncResult is not SftpUploadAsyncResult ar || ar.EndInvokeCalled)
            {
                throw new ArgumentException("Either the IAsyncResult object did not come from the corresponding async method on this type, or EndExecute was called multiple times with the same IAsyncResult.");
            }

            // Wait for operation to complete, then return result or throw exception
            ar.EndInvoke();
        }

        /// <summary>
        /// Gets status using statvfs@openssh.com request.
        /// </summary>
        /// <param name="path">The path.</param>
        /// <returns>
        /// A <see cref="SftpFileSystemInformation"/> instance that contains file status information.
        /// </returns>
        /// <exception cref="SshConnectionException">Client is not connected.</exception>
        /// <exception cref="ArgumentNullException"><paramref name="path" /> is <see langword="null"/>.</exception>
        /// <exception cref="ObjectDisposedException">The method was called after the client was disposed.</exception>
        public SftpFileSystemInformation GetStatus(string path)
        {
            CheckDisposed();
            ThrowHelper.ThrowIfNull(path);

            if (_sftpSession is null)
            {
                throw new SshConnectionException("Client not connected.");
            }

            var fullPath = _sftpSession.GetCanonicalPath(path);

            return _sftpSession.RequestStatVfs(fullPath);
        }

        /// <summary>
        /// Asynchronously gets status using statvfs@openssh.com request.
        /// </summary>
        /// <param name="path">The path.</param>
        /// <param name="cancellationToken">The <see cref="CancellationToken"/> to observe.</param>
        /// <returns>
        /// A <see cref="Task{SftpFileSystemInformation}"/> that represents the status operation.
        /// The task result contains the <see cref="SftpFileSystemInformation"/> instance that contains file status information.
        /// </returns>
        /// <exception cref="SshConnectionException">Client is not connected.</exception>
        /// <exception cref="ArgumentNullException"><paramref name="path" /> is <see langword="null"/>.</exception>
        /// <exception cref="ObjectDisposedException">The method was called after the client was disposed.</exception>
        public async Task<SftpFileSystemInformation> GetStatusAsync(string path, CancellationToken cancellationToken)
        {
            CheckDisposed();
            ThrowHelper.ThrowIfNull(path);

            if (_sftpSession is null)
            {
                throw new SshConnectionException("Client not connected.");
            }

            cancellationToken.ThrowIfCancellationRequested();

            var fullPath = await _sftpSession.GetCanonicalPathAsync(path, cancellationToken).ConfigureAwait(false);
            return await _sftpSession.RequestStatVfsAsync(fullPath, cancellationToken).ConfigureAwait(false);
        }

        #region File Methods

        /// <summary>
        /// Appends lines to a file, creating the file if it does not already exist.
        /// </summary>
        /// <param name="path">The file to append the lines to. The file is created if it does not already exist.</param>
        /// <param name="contents">The lines to append to the file.</param>
        /// <exception cref="ArgumentNullException"><paramref name="path"/> is <see langword="null"/>. <para>-or-</para> <paramref name="contents"/> is <see langword="null"/>.</exception>
        /// <exception cref="SshConnectionException">Client is not connected.</exception>
        /// <exception cref="SftpPathNotFoundException">The specified path is invalid, or its directory was not found on the remote host.</exception>
        /// <exception cref="ObjectDisposedException">The method was called after the client was disposed.</exception>
        /// <remarks>
        /// The characters are written to the file using UTF-8 encoding without a byte-order mark (BOM).
        /// </remarks>
        public void AppendAllLines(string path, IEnumerable<string> contents)
        {
            CheckDisposed();
            ThrowHelper.ThrowIfNull(contents);

            using (var stream = AppendText(path))
            {
                foreach (var line in contents)
                {
                    stream.WriteLine(line);
                }
            }
        }

        /// <summary>
        /// Appends lines to a file by using a specified encoding, creating the file if it does not already exist.
        /// </summary>
        /// <param name="path">The file to append the lines to. The file is created if it does not already exist.</param>
        /// <param name="contents">The lines to append to the file.</param>
        /// <param name="encoding">The character encoding to use.</param>
        /// <exception cref="ArgumentNullException"><paramref name="path"/> is <see langword="null"/>. <para>-or-</para> <paramref name="contents"/> is <see langword="null"/>. <para>-or-</para> <paramref name="encoding"/> is <see langword="null"/>.</exception>
        /// <exception cref="SshConnectionException">Client is not connected.</exception>
        /// <exception cref="SftpPathNotFoundException">The specified path is invalid, or its directory was not found on the remote host.</exception>
        /// <exception cref="ObjectDisposedException">The method was called after the client was disposed.</exception>
        public void AppendAllLines(string path, IEnumerable<string> contents, Encoding encoding)
        {
            CheckDisposed();
            ThrowHelper.ThrowIfNull(contents);

            using (var stream = AppendText(path, encoding))
            {
                foreach (var line in contents)
                {
                    stream.WriteLine(line);
                }
            }
        }

        /// <summary>
        /// Appends the specified string to the file, creating the file if it does not already exist.
        /// </summary>
        /// <param name="path">The file to append the specified string to.</param>
        /// <param name="contents">The string to append to the file.</param>
        /// <exception cref="ArgumentNullException"><paramref name="path"/> is <see langword="null"/>. <para>-or-</para> <paramref name="contents"/> is <see langword="null"/>.</exception>
        /// <exception cref="SshConnectionException">Client is not connected.</exception>
        /// <exception cref="SftpPathNotFoundException">The specified path is invalid, or its directory was not found on the remote host.</exception>
        /// <exception cref="ObjectDisposedException">The method was called after the client was disposed.</exception>
        /// <remarks>
        /// The characters are written to the file using UTF-8 encoding without a Byte-Order Mark (BOM).
        /// </remarks>
        public void AppendAllText(string path, string contents)
        {
            using (var stream = AppendText(path))
            {
                stream.Write(contents);
            }
        }

        /// <summary>
        /// Appends the specified string to the file, creating the file if it does not already exist.
        /// </summary>
        /// <param name="path">The file to append the specified string to.</param>
        /// <param name="contents">The string to append to the file.</param>
        /// <param name="encoding">The character encoding to use.</param>
        /// <exception cref="ArgumentNullException"><paramref name="path"/> is <see langword="null"/>. <para>-or-</para> <paramref name="contents"/> is <see langword="null"/>. <para>-or-</para> <paramref name="encoding"/> is <see langword="null"/>.</exception>
        /// <exception cref="SshConnectionException">Client is not connected.</exception>
        /// <exception cref="SftpPathNotFoundException">The specified path is invalid, or its directory was not found on the remote host.</exception>
        /// <exception cref="ObjectDisposedException">The method was called after the client was disposed.</exception>
        public void AppendAllText(string path, string contents, Encoding encoding)
        {
            using (var stream = AppendText(path, encoding))
            {
                stream.Write(contents);
            }
        }

        /// <summary>
        /// Creates a <see cref="StreamWriter"/> that appends UTF-8 encoded text to the specified file,
        /// creating the file if it does not already exist.
        /// </summary>
        /// <param name="path">The path to the file to append to.</param>
        /// <returns>
        /// A <see cref="StreamWriter"/> that appends text to a file using UTF-8 encoding without a
        /// Byte-Order Mark (BOM).
        /// </returns>
        /// <exception cref="ArgumentNullException"><paramref name="path"/> is <see langword="null"/>.</exception>
        /// <exception cref="SshConnectionException">Client is not connected.</exception>
        /// <exception cref="SftpPathNotFoundException">The specified path is invalid, or its directory was not found on the remote host.</exception>
        /// <exception cref="ObjectDisposedException">The method was called after the client was disposed.</exception>
        public StreamWriter AppendText(string path)
        {
            return AppendText(path, Utf8NoBOM);
        }

        /// <summary>
        /// Creates a <see cref="StreamWriter"/> that appends text to a file using the specified
        /// encoding, creating the file if it does not already exist.
        /// </summary>
        /// <param name="path">The path to the file to append to.</param>
        /// <param name="encoding">The character encoding to use.</param>
        /// <returns>
        /// A <see cref="StreamWriter"/> that appends text to a file using the specified encoding.
        /// </returns>
        /// <exception cref="ArgumentNullException"><paramref name="path"/> is <see langword="null"/>. <para>-or-</para> <paramref name="encoding"/> is <see langword="null"/>.</exception>
        /// <exception cref="SshConnectionException">Client is not connected.</exception>
        /// <exception cref="SftpPathNotFoundException">The specified path is invalid, or its directory was not found on the remote host.</exception>
        /// <exception cref="ObjectDisposedException">The method was called after the client was disposed.</exception>
        public StreamWriter AppendText(string path, Encoding encoding)
        {
            CheckDisposed();
            ThrowHelper.ThrowIfNull(encoding);

            return new StreamWriter(Open(path, FileMode.Append, FileAccess.Write), encoding);
        }

        /// <summary>
        /// Creates or overwrites a file in the specified path.
        /// </summary>
        /// <param name="path">The path and name of the file to create.</param>
        /// <returns>
        /// A <see cref="SftpFileStream"/> that provides read/write access to the file specified in path.
        /// </returns>
        /// <exception cref="ArgumentNullException"><paramref name="path"/> is <see langword="null"/>.</exception>
        /// <exception cref="SshConnectionException">Client is not connected.</exception>
        /// <exception cref="SftpPathNotFoundException">The specified path is invalid, or its directory was not found on the remote host.</exception>
        /// <exception cref="ObjectDisposedException">The method was called after the client was disposed.</exception>
        /// <remarks>
        /// If the target file already exists, it is first truncated to zero bytes.
        /// </remarks>
        public SftpFileStream Create(string path)
        {
            return Create(path, (int)_bufferSize);
        }

        /// <summary>
        /// Creates or overwrites the specified file.
        /// </summary>
        /// <param name="path">The path and name of the file to create.</param>
        /// <param name="bufferSize">The maximum number of bytes buffered for reads and writes to the file.</param>
        /// <returns>
        /// A <see cref="SftpFileStream"/> that provides read/write access to the file specified in path.
        /// </returns>
        /// <exception cref="ArgumentNullException"><paramref name="path"/> is <see langword="null"/>.</exception>
        /// <exception cref="SshConnectionException">Client is not connected.</exception>
        /// <exception cref="SftpPathNotFoundException">The specified path is invalid, or its directory was not found on the remote host.</exception>
        /// <exception cref="ObjectDisposedException">The method was called after the client was disposed.</exception>
        /// <remarks>
        /// If the target file already exists, it is first truncated to zero bytes.
        /// </remarks>
        public SftpFileStream Create(string path, int bufferSize)
        {
            CheckDisposed();

            return SftpFileStream.Open(_sftpSession, path, FileMode.Create, FileAccess.ReadWrite, bufferSize);
        }

        /// <inheritdoc/>
        public StreamWriter CreateText(string path)
        {
            return CreateText(path, Utf8NoBOM);
        }

        /// <inheritdoc/>
        public StreamWriter CreateText(string path, Encoding encoding)
        {
            CheckDisposed();

            return new StreamWriter(Open(path, FileMode.Create, FileAccess.Write), encoding);
        }

        /// <summary>
        /// Deletes the specified file or directory.
        /// </summary>
        /// <param name="path">The name of the file or directory to be deleted. Wildcard characters are not supported.</param>
        /// <exception cref="ArgumentNullException"><paramref name="path"/> is <see langword="null"/>.</exception>
        /// <exception cref="SshConnectionException">Client is not connected.</exception>
        /// <exception cref="SftpPathNotFoundException"><paramref name="path"/> was not found on the remote host.</exception>
        /// <exception cref="ObjectDisposedException">The method was called after the client was disposed.</exception>
        public void Delete(string path)
        {
            var file = Get(path);
            file.Delete();
        }

        /// <inheritdoc />
        public async Task DeleteAsync(string path, CancellationToken cancellationToken = default)
        {
            var file = await GetAsync(path, cancellationToken).ConfigureAwait(false);
            await file.DeleteAsync(cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Returns the date and time the specified file or directory was last accessed.
        /// </summary>
        /// <param name="path">The file or directory for which to obtain access date and time information.</param>
        /// <returns>
        /// A <see cref="DateTime"/> structure set to the date and time that the specified file or directory was last accessed.
        /// This value is expressed in local time.
        /// </returns>
        /// <exception cref="ArgumentNullException"><paramref name="path"/> is <see langword="null"/>.</exception>
        /// <exception cref="SshConnectionException">Client is not connected.</exception>
        /// <exception cref="ObjectDisposedException">The method was called after the client was disposed.</exception>
        public DateTime GetLastAccessTime(string path)
        {
            var file = Get(path);
            return file.LastAccessTime;
        }

        /// <summary>
        /// Returns the date and time, in coordinated universal time (UTC), that the specified file or directory was last accessed.
        /// </summary>
        /// <param name="path">The file or directory for which to obtain access date and time information.</param>
        /// <returns>
        /// A <see cref="DateTime"/> structure set to the date and time that the specified file or directory was last accessed.
        /// This value is expressed in UTC time.
        /// </returns>
        /// <exception cref="ArgumentNullException"><paramref name="path"/> is <see langword="null"/>.</exception>
        /// <exception cref="SshConnectionException">Client is not connected.</exception>
        /// <exception cref="ObjectDisposedException">The method was called after the client was disposed.</exception>
        public DateTime GetLastAccessTimeUtc(string path)
        {
            var lastAccessTime = GetLastAccessTime(path);
            return lastAccessTime.ToUniversalTime();
        }

        /// <summary>
        /// Returns the date and time the specified file or directory was last written to.
        /// </summary>
        /// <param name="path">The file or directory for which to obtain write date and time information.</param>
        /// <returns>
        /// A <see cref="DateTime"/> structure set to the date and time that the specified file or directory was last written to.
        /// This value is expressed in local time.
        /// </returns>
        /// <exception cref="ArgumentNullException"><paramref name="path"/> is <see langword="null"/>.</exception>
        /// <exception cref="SshConnectionException">Client is not connected.</exception>
        /// <exception cref="ObjectDisposedException">The method was called after the client was disposed.</exception>
        public DateTime GetLastWriteTime(string path)
        {
            var file = Get(path);
            return file.LastWriteTime;
        }

        /// <summary>
        /// Returns the date and time, in coordinated universal time (UTC), that the specified file or directory was last written to.
        /// </summary>
        /// <param name="path">The file or directory for which to obtain write date and time information.</param>
        /// <returns>
        /// A <see cref="DateTime"/> structure set to the date and time that the specified file or directory was last written to.
        /// This value is expressed in UTC time.
        /// </returns>
        /// <exception cref="ArgumentNullException"><paramref name="path"/> is <see langword="null"/>.</exception>
        /// <exception cref="SshConnectionException">Client is not connected.</exception>
        /// <exception cref="ObjectDisposedException">The method was called after the client was disposed.</exception>
        public DateTime GetLastWriteTimeUtc(string path)
        {
            var lastWriteTime = GetLastWriteTime(path);
            return lastWriteTime.ToUniversalTime();
        }

        /// <summary>
        /// Opens a <see cref="SftpFileStream"/> on the specified path with read/write access.
        /// </summary>
        /// <param name="path">The file to open.</param>
        /// <param name="mode">A <see cref="FileMode"/> value that specifies whether a file is created if one does not exist, and determines whether the contents of existing files are retained or overwritten.</param>
        /// <returns>
        /// An unshared <see cref="SftpFileStream"/> that provides access to the specified file, with the specified mode and read/write access.
        /// </returns>
        /// <exception cref="ArgumentNullException"><paramref name="path"/> is <see langword="null"/>.</exception>
        /// <exception cref="SshConnectionException">Client is not connected.</exception>
        /// <exception cref="ObjectDisposedException">The method was called after the client was disposed.</exception>
        public SftpFileStream Open(string path, FileMode mode)
        {
            return Open(path, mode, FileAccess.ReadWrite);
        }

        /// <summary>
        /// Opens a <see cref="SftpFileStream"/> on the specified path, with the specified mode and access.
        /// </summary>
        /// <param name="path">The file to open.</param>
        /// <param name="mode">A <see cref="FileMode"/> value that specifies whether a file is created if one does not exist, and determines whether the contents of existing files are retained or overwritten.</param>
        /// <param name="access">A <see cref="FileAccess"/> value that specifies the operations that can be performed on the file.</param>
        /// <returns>
        /// An unshared <see cref="SftpFileStream"/> that provides access to the specified file, with the specified mode and access.
        /// </returns>
        /// <exception cref="ArgumentNullException"><paramref name="path"/> is <see langword="null"/>.</exception>
        /// <exception cref="SshConnectionException">Client is not connected.</exception>
        /// <exception cref="ObjectDisposedException">The method was called after the client was disposed.</exception>
        public SftpFileStream Open(string path, FileMode mode, FileAccess access)
        {
            CheckDisposed();

            return SftpFileStream.Open(_sftpSession, path, mode, access, (int)_bufferSize);
        }

        /// <summary>
        /// Asynchronously opens a <see cref="SftpFileStream"/> on the specified path, with the specified mode and access.
        /// </summary>
        /// <param name="path">The file to open.</param>
        /// <param name="mode">A <see cref="FileMode"/> value that specifies whether a file is created if one does not exist, and determines whether the contents of existing files are retained or overwritten.</param>
        /// <param name="access">A <see cref="FileAccess"/> value that specifies the operations that can be performed on the file.</param>
        /// <param name="cancellationToken">The <see cref="CancellationToken"/> to observe.</param>
        /// <returns>
        /// A <see cref="Task{SftpFileStream}"/> that represents the asynchronous open operation.
        /// The task result contains the <see cref="SftpFileStream"/> that provides access to the specified file, with the specified mode and access.
        /// </returns>
        /// <exception cref="ArgumentNullException"><paramref name="path"/> is <see langword="null"/>.</exception>
        /// <exception cref="SshConnectionException">Client is not connected.</exception>
        /// <exception cref="ObjectDisposedException">The method was called after the client was disposed.</exception>
        public Task<SftpFileStream> OpenAsync(string path, FileMode mode, FileAccess access, CancellationToken cancellationToken)
        {
            CheckDisposed();

            return SftpFileStream.OpenAsync(_sftpSession, path, mode, access, (int)_bufferSize, cancellationToken);
        }

        /// <summary>
        /// Opens an existing file for reading.
        /// </summary>
        /// <param name="path">The file to be opened for reading.</param>
        /// <returns>
        /// A read-only <see cref="SftpFileStream"/> on the specified path.
        /// </returns>
        /// <exception cref="ArgumentNullException"><paramref name="path"/> is <see langword="null"/>.</exception>
        /// <exception cref="SshConnectionException">Client is not connected.</exception>
        /// <exception cref="ObjectDisposedException">The method was called after the client was disposed.</exception>
        public SftpFileStream OpenRead(string path)
        {
            return Open(path, FileMode.Open, FileAccess.Read);
        }

        /// <summary>
        /// Opens an existing UTF-8 encoded text file for reading.
        /// </summary>
        /// <param name="path">The file to be opened for reading.</param>
        /// <returns>
        /// A <see cref="StreamReader"/> on the specified path.
        /// </returns>
        /// <exception cref="ArgumentNullException"><paramref name="path"/> is <see langword="null"/>.</exception>
        /// <exception cref="SshConnectionException">Client is not connected.</exception>
        /// <exception cref="ObjectDisposedException">The method was called after the client was disposed.</exception>
        public StreamReader OpenText(string path)
        {
            return new StreamReader(OpenRead(path), Encoding.UTF8);
        }

        /// <summary>
        /// Opens a file for writing.
        /// </summary>
        /// <param name="path">The file to be opened for writing.</param>
        /// <returns>
        /// An unshared <see cref="SftpFileStream"/> object on the specified path with <see cref="FileAccess.Write"/> access.
        /// </returns>
        /// <exception cref="ArgumentNullException"><paramref name="path"/> is <see langword="null"/>.</exception>
        /// <exception cref="SshConnectionException">Client is not connected.</exception>
        /// <exception cref="ObjectDisposedException">The method was called after the client was disposed.</exception>
        /// <remarks>
        /// If the file does not exist, it is created.
        /// </remarks>
        public SftpFileStream OpenWrite(string path)
        {
            return Open(path, FileMode.OpenOrCreate, FileAccess.Write);
        }

        /// <summary>
        /// Opens a binary file, reads the contents of the file into a byte array, and closes the file.
        /// </summary>
        /// <param name="path">The file to open for reading.</param>
        /// <returns>
        /// A byte array containing the contents of the file.
        /// </returns>
        /// <exception cref="ArgumentNullException"><paramref name="path"/> is <see langword="null"/>.</exception>
        /// <exception cref="SshConnectionException">Client is not connected.</exception>
        /// <exception cref="ObjectDisposedException">The method was called after the client was disposed.</exception>
        public byte[] ReadAllBytes(string path)
        {
            using (var stream = OpenRead(path))
            {
                byte[] buffer;

                if (stream.CanSeek)
                {
                    buffer = new byte[stream.Length];
                    stream.ReadExactly(buffer, 0, buffer.Length);
                }
                else
                {
                    MemoryStream ms = new();
                    stream.CopyTo(ms);
                    buffer = ms.ToArray();
                }

                return buffer;
            }
        }

        /// <summary>
        /// Opens a text file, reads all lines of the file using UTF-8 encoding, and closes the file.
        /// </summary>
        /// <param name="path">The file to open for reading.</param>
        /// <returns>
        /// A string array containing all lines of the file.
        /// </returns>
        /// <exception cref="ArgumentNullException"><paramref name="path"/> is <see langword="null"/>.</exception>
        /// <exception cref="SshConnectionException">Client is not connected.</exception>
        /// <exception cref="ObjectDisposedException">The method was called after the client was disposed.</exception>
        public string[] ReadAllLines(string path)
        {
            return ReadAllLines(path, Encoding.UTF8);
        }

        /// <summary>
        /// Opens a file, reads all lines of the file with the specified encoding, and closes the file.
        /// </summary>
        /// <param name="path">The file to open for reading.</param>
        /// <param name="encoding">The encoding applied to the contents of the file.</param>
        /// <returns>
        /// A string array containing all lines of the file.
        /// </returns>
        /// <exception cref="ArgumentNullException"><paramref name="path"/> is <see langword="null"/>.</exception>
        /// <exception cref="SshConnectionException">Client is not connected.</exception>
        /// <exception cref="ObjectDisposedException">The method was called after the client was disposed.</exception>
        public string[] ReadAllLines(string path, Encoding encoding)
        {
            return ReadLines(path, encoding).ToArray();
        }

        /// <summary>
        /// Opens a text file, reads all lines of the file with the UTF-8 encoding, and closes the file.
        /// </summary>
        /// <param name="path">The file to open for reading.</param>
        /// <returns>
        /// A string containing all lines of the file.
        /// </returns>
        /// <exception cref="ArgumentNullException"><paramref name="path"/> is <see langword="null"/>.</exception>
        /// <exception cref="SshConnectionException">Client is not connected.</exception>
        /// <exception cref="ObjectDisposedException">The method was called after the client was disposed.</exception>
        public string ReadAllText(string path)
        {
            return ReadAllText(path, Encoding.UTF8);
        }

        /// <summary>
        /// Opens a file, reads all lines of the file with the specified encoding, and closes the file.
        /// </summary>
        /// <param name="path">The file to open for reading.</param>
        /// <param name="encoding">The encoding applied to the contents of the file.</param>
        /// <returns>
        /// A string containing all lines of the file.
        /// </returns>
        /// <exception cref="ArgumentNullException"><paramref name="path"/> is <see langword="null"/>.</exception>
        /// <exception cref="SshConnectionException">Client is not connected.</exception>
        /// <exception cref="ObjectDisposedException">The method was called after the client was disposed.</exception>
        public string ReadAllText(string path, Encoding encoding)
        {
            using var sr = new StreamReader(OpenRead(path), encoding);
            return sr.ReadToEnd();
        }

        /// <summary>
        /// Reads the lines of a file with the UTF-8 encoding.
        /// </summary>
        /// <param name="path">The file to read.</param>
        /// <returns>
        /// The lines of the file.
        /// </returns>
        /// <remarks>
        /// The lines are enumerated lazily. The opening of the file and any resulting exceptions occur
        /// upon enumeration.
        /// </remarks>
        /// <exception cref="ArgumentNullException"><paramref name="path"/> is <see langword="null"/>. Thrown eagerly.</exception>
        /// <exception cref="SshConnectionException">Client is not connected upon enumeration.</exception>
        /// <exception cref="ObjectDisposedException">The return value is enumerated after the client is disposed.</exception>
        public IEnumerable<string> ReadLines(string path)
        {
            return ReadLines(path, Encoding.UTF8);
        }

        /// <summary>
        /// Read the lines of a file that has a specified encoding.
        /// </summary>
        /// <param name="path">The file to read.</param>
        /// <param name="encoding">The encoding that is applied to the contents of the file.</param>
        /// <returns>
        /// The lines of the file.
        /// </returns>
        /// <remarks>
        /// The lines are enumerated lazily. The opening of the file and any resulting exceptions occur
        /// upon enumeration.
        /// </remarks>
        /// <exception cref="ArgumentNullException"><paramref name="path"/> is <see langword="null"/>. Thrown eagerly.</exception>
        /// <exception cref="SshConnectionException">Client is not connected upon enumeration.</exception>
        /// <exception cref="ObjectDisposedException">The return value is enumerated after the client is disposed.</exception>
        public IEnumerable<string> ReadLines(string path, Encoding encoding)
        {
            // We allow this usage exception to throw eagerly...
            ThrowHelper.ThrowIfNull(path);

            // ... but other exceptions will throw lazily i.e. inside the state machine created
            // by yield. We could choose to open the file eagerly as well in order to throw
            // file-related exceptions eagerly (matching what File.ReadLines does), but this
            // complicates double enumeration, and introduces the problem that File.ReadLines
            // has whereby the file is not closed if the return value is not enumerated.
            return Enumerate();

            IEnumerable<string> Enumerate()
            {
                using var sr = new StreamReader(OpenRead(path), encoding);

                string? line;

                while ((line = sr.ReadLine()) != null)
                {
                    yield return line;
                }
            }
        }

        /// <summary>
        /// Sets the date and time the specified file was last accessed.
        /// </summary>
        /// <param name="path">The file for which to set the access date and time information.</param>
        /// <param name="lastAccessTime">A <see cref="DateTime"/> containing the value to set for the last access date and time of path. This value is expressed in local time.</param>
        public void SetLastAccessTime(string path, DateTime lastAccessTime)
        {
            var attributes = GetAttributes(path);
            attributes.LastAccessTime = lastAccessTime;
            SetAttributes(path, attributes);
        }

        /// <summary>
        /// Sets the date and time, in coordinated universal time (UTC), that the specified file was last accessed.
        /// </summary>
        /// <param name="path">The file for which to set the access date and time information.</param>
        /// <param name="lastAccessTimeUtc">A <see cref="DateTime"/> containing the value to set for the last access date and time of path. This value is expressed in UTC time.</param>
        public void SetLastAccessTimeUtc(string path, DateTime lastAccessTimeUtc)
        {
            var attributes = GetAttributes(path);
            attributes.LastAccessTimeUtc = lastAccessTimeUtc;
            SetAttributes(path, attributes);
        }

        /// <summary>
        /// Sets the date and time that the specified file was last written to.
        /// </summary>
        /// <param name="path">The file for which to set the date and time information.</param>
        /// <param name="lastWriteTime">A <see cref="DateTime"/> containing the value to set for the last write date and time of path. This value is expressed in local time.</param>
        public void SetLastWriteTime(string path, DateTime lastWriteTime)
        {
            var attributes = GetAttributes(path);
            attributes.LastWriteTime = lastWriteTime;
            SetAttributes(path, attributes);
        }

        /// <summary>
        /// Sets the date and time, in coordinated universal time (UTC), that the specified file was last written to.
        /// </summary>
        /// <param name="path">The file for which to set the date and time information.</param>
        /// <param name="lastWriteTimeUtc">A <see cref="DateTime"/> containing the value to set for the last write date and time of path. This value is expressed in UTC time.</param>
        public void SetLastWriteTimeUtc(string path, DateTime lastWriteTimeUtc)
        {
            var attributes = GetAttributes(path);
            attributes.LastWriteTimeUtc = lastWriteTimeUtc;
            SetAttributes(path, attributes);
        }

        /// <inheritdoc/>
        public void WriteAllBytes(string path, byte[] bytes)
        {
            ThrowHelper.ThrowIfNull(bytes);

            UploadFile(new MemoryStream(bytes), path);
        }

        /// <inheritdoc/>
        public void WriteAllLines(string path, IEnumerable<string> contents)
        {
            WriteAllLines(path, contents, Utf8NoBOM);
        }

        /// <inheritdoc/>
        public void WriteAllLines(string path, string[] contents)
        {
            WriteAllLines(path, contents, Utf8NoBOM);
        }

        /// <inheritdoc/>
        public void WriteAllLines(string path, IEnumerable<string> contents, Encoding encoding)
        {
            using (var stream = CreateText(path, encoding))
            {
                foreach (var line in contents)
                {
                    stream.WriteLine(line);
                }
            }
        }

        /// <inheritdoc/>
        public void WriteAllLines(string path, string[] contents, Encoding encoding)
        {
            WriteAllLines(path, (IEnumerable<string>)contents, encoding);
        }

        /// <inheritdoc/>
        public void WriteAllText(string path, string contents)
        {
            using (var stream = CreateText(path))
            {
                stream.Write(contents);
            }
        }

        /// <inheritdoc/>
        public void WriteAllText(string path, string contents, Encoding encoding)
        {
            using (var stream = CreateText(path, encoding))
            {
                stream.Write(contents);
            }
        }

        /// <summary>
        /// Gets the <see cref="SftpFileAttributes"/> of the file on the path.
        /// </summary>
        /// <param name="path">The path to the file.</param>
        /// <returns>
        /// The <see cref="SftpFileAttributes"/> of the file on the path.
        /// </returns>
        /// <exception cref="ArgumentNullException"><paramref name="path"/> is <see langword="null"/>.</exception>
        /// <exception cref="SshConnectionException">Client is not connected.</exception>
        /// <exception cref="SftpPathNotFoundException"><paramref name="path"/> was not found on the remote host.</exception>
        /// <exception cref="ObjectDisposedException">The method was called after the client was disposed.</exception>
        public SftpFileAttributes GetAttributes(string path)
        {
            CheckDisposed();

            if (_sftpSession is null)
            {
                throw new SshConnectionException("Client not connected.");
            }

            var fullPath = _sftpSession.GetCanonicalPath(path);

            return _sftpSession.RequestLStat(fullPath);
        }

        /// <summary>
        /// Gets the <see cref="SftpFileAttributes"/> of the file on the path.
        /// </summary>
        /// <param name="path">The path to the file.</param>
        /// <param name="cancellationToken">The <see cref="CancellationToken"/> to observe.</param>
        /// <returns>
        /// A <see cref="Task{SftpFileAttributes}"/> that represents the attribute retrieval operation.
        /// The task result contains the <see cref="SftpFileAttributes"/> of the file on the path.
        /// </returns>
        /// <exception cref="ArgumentNullException"><paramref name="path"/> is <see langword="null"/>.</exception>
        /// <exception cref="SshConnectionException">Client is not connected.</exception>
        /// <exception cref="SftpPathNotFoundException"><paramref name="path"/> was not found on the remote host.</exception>
        /// <exception cref="ObjectDisposedException">The method was called after the client was disposed.</exception>
        public async Task<SftpFileAttributes> GetAttributesAsync(string path, CancellationToken cancellationToken)
        {
            CheckDisposed();

            if (_sftpSession is null)
            {
                throw new SshConnectionException("Client not connected.");
            }

            var fullPath = await _sftpSession.GetCanonicalPathAsync(path, cancellationToken).ConfigureAwait(false);

            return await _sftpSession.RequestLStatAsync(fullPath, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Sets the specified <see cref="SftpFileAttributes"/> of the file on the specified path.
        /// </summary>
        /// <param name="path">The path to the file.</param>
        /// <param name="fileAttributes">The desired <see cref="SftpFileAttributes"/>.</param>
        /// <exception cref="ArgumentNullException"><paramref name="path"/> is <see langword="null"/>.</exception>
        /// <exception cref="SshConnectionException">Client is not connected.</exception>
        /// <exception cref="ObjectDisposedException">The method was called after the client was disposed.</exception>
        public void SetAttributes(string path, SftpFileAttributes fileAttributes)
        {
            CheckDisposed();

            if (_sftpSession is null)
            {
                throw new SshConnectionException("Client not connected.");
            }

            var fullPath = _sftpSession.GetCanonicalPath(path);

            _sftpSession.RequestSetStat(fullPath, fileAttributes);
        }

        #endregion // File Methods

        #region SynchronizeDirectories

        /// <summary>
        /// Synchronizes the directories.
        /// </summary>
        /// <param name="sourcePath">The source path.</param>
        /// <param name="destinationPath">The destination path.</param>
        /// <param name="searchPattern">The search pattern.</param>
        /// <returns>
        /// A list of uploaded files.
        /// </returns>
        /// <exception cref="ArgumentNullException"><paramref name="sourcePath"/> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentException"><paramref name="destinationPath"/> is <see langword="null"/> or contains only whitespace.</exception>
        /// <exception cref="SftpPathNotFoundException"><paramref name="destinationPath"/> was not found on the remote host.</exception>
        /// <exception cref="SshException">If a problem occurs while copying the file.</exception>
        public IEnumerable<FileInfo> SynchronizeDirectories(string sourcePath, string destinationPath, string searchPattern)
        {
            ThrowHelper.ThrowIfNull(sourcePath);
            ThrowHelper.ThrowIfNullOrWhiteSpace(destinationPath);

            return InternalSynchronizeDirectories(sourcePath, destinationPath, searchPattern, asyncResult: null);
        }

        /// <summary>
        /// Begins the synchronize directories.
        /// </summary>
        /// <param name="sourcePath">The source path.</param>
        /// <param name="destinationPath">The destination path.</param>
        /// <param name="searchPattern">The search pattern.</param>
        /// <param name="asyncCallback">The async callback.</param>
        /// <param name="state">The state.</param>
        /// <returns>
        /// An <see cref="IAsyncResult" /> that represents the asynchronous directory synchronization.
        /// </returns>
        /// <exception cref="ArgumentNullException"><paramref name="sourcePath"/> or <paramref name="searchPattern"/> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentException"><paramref name="destinationPath"/> is <see langword="null"/> or contains only whitespace.</exception>
        /// <exception cref="SshException">If a problem occurs while copying the file.</exception>
        public IAsyncResult BeginSynchronizeDirectories(string sourcePath, string destinationPath, string searchPattern, AsyncCallback? asyncCallback, object? state)
        {
            ThrowHelper.ThrowIfNull(sourcePath);
            ThrowHelper.ThrowIfNullOrWhiteSpace(destinationPath);
            ThrowHelper.ThrowIfNull(searchPattern);

            var asyncResult = new SftpSynchronizeDirectoriesAsyncResult(asyncCallback, state);

            ThreadAbstraction.ExecuteThread(() =>
                {
                    try
                    {
                        var result = InternalSynchronizeDirectories(sourcePath, destinationPath, searchPattern, asyncResult);

                        asyncResult.SetAsCompleted(result, completedSynchronously: false);
                    }
                    catch (Exception exp)
                    {
                        asyncResult.SetAsCompleted(exp, completedSynchronously: false);
                    }
                });

            return asyncResult;
        }

        /// <summary>
        /// Ends the synchronize directories.
        /// </summary>
        /// <param name="asyncResult">The async result.</param>
        /// <returns>
        /// A list of uploaded files.
        /// </returns>
        /// <exception cref="ArgumentException">The <see cref="IAsyncResult"/> object did not come from the corresponding async method on this type.<para>-or-</para><see cref="EndSynchronizeDirectories(IAsyncResult)"/> was called multiple times with the same <see cref="IAsyncResult"/>.</exception>
        /// <exception cref="SftpPathNotFoundException">The destination path was not found on the remote host.</exception>
        public IEnumerable<FileInfo> EndSynchronizeDirectories(IAsyncResult asyncResult)
        {
            if (asyncResult is not SftpSynchronizeDirectoriesAsyncResult ar || ar.EndInvokeCalled)
            {
                throw new ArgumentException("Either the IAsyncResult object did not come from the corresponding async method on this type, or EndExecute was called multiple times with the same IAsyncResult.");
            }

            // Wait for operation to complete, then return result or throw exception
            return ar.EndInvoke();
        }

        private List<FileInfo> InternalSynchronizeDirectories(string sourcePath, string destinationPath, string searchPattern, SftpSynchronizeDirectoriesAsyncResult? asyncResult)
        {
            if (!Directory.Exists(sourcePath))
            {
                throw new FileNotFoundException($"Source directory not found: {sourcePath}");
            }

            var uploadedFiles = new List<FileInfo>();

            var sourceDirectory = new DirectoryInfo(sourcePath);

            using (var sourceFiles = sourceDirectory.EnumerateFiles(searchPattern).GetEnumerator())
            {
                if (!sourceFiles.MoveNext())
                {
                    return uploadedFiles;
                }

                #region Existing Files at The Destination

                var destFiles = InternalListDirectory(destinationPath, asyncResult: null, listCallback: null);
                var destDict = new Dictionary<string, ISftpFile>();
                foreach (var destFile in destFiles)
                {
                    if (destFile.IsDirectory)
                    {
                        continue;
                    }

                    destDict.Add(destFile.Name, destFile);
                }

                #endregion

                #region Upload the difference

                const Flags uploadFlag = Flags.Write | Flags.Truncate | Flags.CreateNewOrOpen;
                do
                {
                    var localFile = sourceFiles.Current;
                    if (localFile is null)
                    {
                        continue;
                    }

                    var isDifferent = true;
                    if (destDict.TryGetValue(localFile.Name, out var remoteFile))
                    {
                        // File exists at the destination, use filesize to detect if there's a difference
                        isDifferent = localFile.Length != remoteFile.Length;
                    }

                    if (isDifferent)
                    {
                        var remoteFileName = $"{destinationPath}/{localFile.Name}";
                        try
                        {
                            using (var file = File.OpenRead(localFile.FullName))
                            {
                                InternalUploadFile(
                                    file,
                                    remoteFileName,
                                    uploadFlag,
                                    asyncResult: null,
                                    uploadCallback: null,
                                    isAsync: false,
                                    CancellationToken.None).GetAwaiter().GetResult();
                            }

                            uploadedFiles.Add(localFile);

                            asyncResult?.Update(uploadedFiles.Count);
                        }
                        catch (Exception ex)
                        {
                            throw new SshException($"Failed to upload {localFile.FullName} to {remoteFileName}", ex);
                        }
                    }
                }
                while (sourceFiles.MoveNext());
            }

            #endregion

            return uploadedFiles;
        }

        #endregion

        /// <summary>
        /// Internals the list directory.
        /// </summary>
        /// <param name="path">The path.</param>
        /// <param name="asyncResult">An <see cref="IAsyncResult"/> that references the asynchronous request.</param>
        /// <param name="listCallback">The list callback.</param>
        /// <returns>
        /// A list of files in the specified directory.
        /// </returns>
        /// <exception cref="ArgumentNullException"><paramref name="path" /> is <see langword="null"/>.</exception>
        /// <exception cref="SshConnectionException">Client not connected.</exception>
        private List<ISftpFile> InternalListDirectory(string path, SftpListDirectoryAsyncResult? asyncResult, Action<int>? listCallback)
        {
            ThrowHelper.ThrowIfNull(path);

            if (_sftpSession is null)
            {
                throw new SshConnectionException("Client not connected.");
            }

            var fullPath = _sftpSession.GetCanonicalPath(path);

            var handle = _sftpSession.RequestOpenDir(fullPath);

            var basePath = fullPath;

#if NET
            if (!basePath.EndsWith('/'))
#else
            if (!basePath.EndsWith("/", StringComparison.Ordinal))
#endif
            {
                basePath = $"{fullPath}/";
            }

            var result = new List<ISftpFile>();

            var files = _sftpSession.RequestReadDir(handle);

            while (files is not null)
            {
                foreach (var f in files)
                {
                    result.Add(new SftpFile(_sftpSession,
                                            $"{basePath}{f.Key}",
                                            f.Value));
                }

                asyncResult?.Update(result.Count);

                // Call callback to report number of files read
                if (listCallback is not null)
                {
                    // Execute callback on different thread
                    ThreadAbstraction.ExecuteThread(() => listCallback(result.Count));
                }

                files = _sftpSession.RequestReadDir(handle);
            }

            _sftpSession.RequestClose(handle);

            return result;
        }

#pragma warning disable S6966 // Awaitable method should be used
        private async Task InternalDownloadFile(
            string path,
            Stream output,
            SftpDownloadAsyncResult? asyncResult,
            Action<ulong>? downloadCallback,
            bool isAsync,
            CancellationToken cancellationToken)
        {
            Debug.Assert(!string.IsNullOrWhiteSpace(path));
            Debug.Assert(output is not null);
            Debug.Assert(isAsync || cancellationToken == default);

            if (_sftpSession is null)
            {
                throw new SshConnectionException("Client not connected.");
            }

            SftpFileStream sftpStream;

            if (isAsync)
            {
                var fullPath = await _sftpSession.GetCanonicalPathAsync(path, cancellationToken).ConfigureAwait(false);

                sftpStream = await SftpFileStream.OpenAsync(
                    _sftpSession,
                    fullPath,
                    FileMode.Open,
                    FileAccess.Read,
                    (int)_bufferSize,
                    cancellationToken,
                    isDownloadFile: true).ConfigureAwait(false);
            }
            else
            {
                var fullPath = _sftpSession.GetCanonicalPath(path);

                sftpStream = SftpFileStream.Open(
                    _sftpSession,
                    fullPath,
                    FileMode.Open,
                    FileAccess.Read,
                    (int)_bufferSize,
                    isDownloadFile: true);
            }

            // The below is effectively sftpStream.CopyTo{Async}(output) with consideration
            // for downloadCallback/asyncResult.

            var buffer = ArrayPool<byte>.Shared.Rent(81920);
            try
            {
                ulong totalBytesRead = 0;
                while (true)
                {
                    // Cancel download
                    if (asyncResult is not null && asyncResult.IsDownloadCanceled)
                    {
                        break;
                    }

                    var bytesRead = isAsync
#if NET
                        ? await sftpStream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)
#else
                        ? await sftpStream.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false)
#endif
                        : sftpStream.Read(buffer, 0, buffer.Length);

                    if (bytesRead == 0)
                    {
                        break;
                    }

                    if (isAsync)
                    {
#if NET
                        await output.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken).ConfigureAwait(false);
#else
                        await output.WriteAsync(buffer, 0, bytesRead, cancellationToken).ConfigureAwait(false);
#endif
                    }
                    else
                    {
                        output.Write(buffer, 0, bytesRead);
                    }

                    totalBytesRead += (ulong)bytesRead;

                    asyncResult?.Update(totalBytesRead);

                    if (downloadCallback is not null)
                    {
                        // Copy offset to ensure it's not modified between now and execution of callback
                        var downloadOffset = totalBytesRead;

                        // Execute callback on different thread
                        ThreadAbstraction.ExecuteThread(() => { downloadCallback(downloadOffset); });
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);

                if (isAsync)
                {
                    await sftpStream.DisposeAsync().ConfigureAwait(false);
                }
                else
                {
                    sftpStream.Dispose();
                }
            }
        }
#pragma warning restore S6966 // Awaitable method should be used

#pragma warning disable S6966 // Awaitable method should be used
        private async Task InternalUploadFile(
            Stream input,
            string path,
            Flags flags,
            SftpUploadAsyncResult? asyncResult,
            Action<ulong>? uploadCallback,
            bool isAsync,
            CancellationToken cancellationToken)
        {
            Debug.Assert(isAsync || cancellationToken == default);

            if (_sftpSession is null)
            {
                throw new SshConnectionException("Client not connected.");
            }

            string fullPath;
            byte[] handle;

            if (isAsync)
            {
                fullPath = await _sftpSession.GetCanonicalPathAsync(path, cancellationToken).ConfigureAwait(false);
                handle = await _sftpSession.RequestOpenAsync(fullPath, flags, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                fullPath = _sftpSession.GetCanonicalPath(path);
                handle = _sftpSession.RequestOpen(fullPath, flags);
            }

            ulong offset = 0;

            // create buffer of optimal length
            var buffer = new byte[_sftpSession.CalculateOptimalWriteLength(_bufferSize, handle)];

            var expectedResponses = 0;

            // We will send out all the write requests without waiting for each response.
            // Afterwards, we may wait on this handle until all responses are received
            // or an error has occurred.
            using var mres = new ManualResetEventSlim(initialState: false);

            ExceptionDispatchInfo? exception = null;

            while (true)
            {
                var bytesRead = isAsync
#if NET
                    ? await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)
#else
                    ? await input.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false)
#endif
                    : input.Read(buffer, 0, buffer.Length);

                if (bytesRead == 0)
                {
                    break;
                }

                if (asyncResult is not null && asyncResult.IsUploadCanceled)
                {
                    break;
                }

                exception?.Throw();

                var writtenBytes = offset + (ulong)bytesRead;

                _ = Interlocked.Increment(ref expectedResponses);
                mres.Reset();

                _sftpSession.RequestWrite(handle, offset, buffer, offset: 0, bytesRead, wait: null, s =>
                {
                    var setHandle = false;

                    try
                    {
                        if (Sftp.SftpSession.GetSftpException(s) is Exception ex)
                        {
                            exception = ExceptionDispatchInfo.Capture(ex);
                        }

                        if (exception is not null)
                        {
                            setHandle = true;
                            return;
                        }

                        Debug.Assert(s.StatusCode == StatusCode.Ok);

                        asyncResult?.Update(writtenBytes);

                        // Call callback to report number of bytes written
                        if (uploadCallback is not null)
                        {
                            // Execute callback on different thread
                            ThreadAbstraction.ExecuteThread(() => uploadCallback(writtenBytes));
                        }
                    }
                    finally
                    {
                        if (Interlocked.Decrement(ref expectedResponses) == 0 || setHandle)
                        {
                            mres.Set();
                        }
                    }
                });

                offset += (ulong)bytesRead;
            }

            // Make sure the read of exception cannot be executed ahead of
            // the read of expectedResponses so that we do not miss an
            // exception.

            if (Volatile.Read(ref expectedResponses) != 0)
            {
                if (isAsync)
                {
                    await _sftpSession.WaitOnHandleAsync(mres.WaitHandle, _operationTimeout, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    _sftpSession.WaitOnHandle(mres.WaitHandle, _operationTimeout);
                }
            }

            exception?.Throw();

            if (isAsync)
            {
                await _sftpSession.RequestCloseAsync(handle, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                _sftpSession.RequestClose(handle);
            }
        }
#pragma warning restore S6966 // Awaitable method should be used

        /// <summary>
        /// Called when client is connected to the server.
        /// </summary>
        protected override void OnConnected()
        {
            base.OnConnected();

            _sftpSession?.Dispose();
            _sftpSession = CreateAndConnectToSftpSession();
        }

        /// <summary>
        /// Called when client is disconnecting from the server.
        /// </summary>
        protected override void OnDisconnecting()
        {
            base.OnDisconnecting();

            // disconnect, dispose and dereference the SFTP session since we create a new SFTP session
            // on each connect
            var sftpSession = _sftpSession;
            if (sftpSession is not null)
            {
                _sftpSession = null;
                sftpSession.Dispose();
            }
        }

        /// <summary>
        /// Releases unmanaged and - optionally - managed resources.
        /// </summary>
        /// <param name="disposing"><see langword="true"/> to release both managed and unmanaged resources; <see langword="false"/> to release only unmanaged resources.</param>
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);

            if (disposing)
            {
                var sftpSession = _sftpSession;
                if (sftpSession is not null)
                {
                    _sftpSession = null;
                    sftpSession.Dispose();
                }
            }
        }

        private ISftpSession CreateAndConnectToSftpSession()
        {
            var sftpSession = ServiceFactory.CreateSftpSession(Session,
                                                               _operationTimeout,
                                                               ConnectionInfo.Encoding,
                                                               ServiceFactory.CreateSftpResponseFactory());
            try
            {
                sftpSession.Connect();
                return sftpSession;
            }
            catch
            {
                sftpSession.Dispose();
                throw;
            }
        }
    }
}
