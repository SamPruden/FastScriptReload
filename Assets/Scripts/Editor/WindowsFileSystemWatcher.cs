﻿using Microsoft.Win32.SafeHandles;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.IO.Enumeration;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace FastScriptReload.Editor
{
    /// <summary>
    /// This is a Windows only file watcher, for use in Unity/Mono.
    /// Mono already has the cross platform FileSystemWatcher.
    /// However, it's incredibly slow on Windows.
    /// This one is fast.
    /// 
    /// This doesn't include the complete API surface of FileSystemWatcher,
    /// but those bits that are present should be compatible with FileSystemWatcher.
    /// 
    /// Events will be dispatched on a worker thread.
    /// They may be on different threads from each other, but they won't overlap in time.
    /// </summary>
    internal class WindowsFileSystemWatcher : IDisposable
    {
        public event FileSystemEventHandler Changed;
        public event FileSystemEventHandler Created;
        public event FileSystemEventHandler Deleted;
        public event RenamedEventHandler Renamed;
        public event ErrorEventHandler Error;

        public NotifyFilters NotifyFilter { get; set; } = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.DirectoryName;
        public string Filter { get; set; } = "*.*";
        public bool IncludeSubdirectories { get; set; } = false;
        
        public string Path
        {
            get => this.path;
            set
            {
                var changed = value != this.path;
                this.path = value;

                // Restart if the path changes.
                if (changed && this.EnableRaisingEvents)
                {
                    this.EnableRaisingEvents = false;
                    this.EnableRaisingEvents = true;
                }
            }
        }

        private string path;
        private InterruptibleHandle currentHandle;
        private Task monitorTask;
        private Task eventsTask;
        private bool disposed = false;

        public WindowsFileSystemWatcher()
        {
            // Prevents us from hanging domain reload in Unity.
            AppDomain.CurrentDomain.DomainUnload += this.HandleDomainUnload;
            this.eventsTask = Task.CompletedTask;
        }

        ~WindowsFileSystemWatcher()
        {
            this.Dispose();
        }

        public void Dispose()
        {
            this.EnableRaisingEvents = false;
            AppDomain.CurrentDomain.DomainUnload -= this.HandleDomainUnload;
            this.Changed = null;
            this.Created = null;
            this.Deleted = null;
            this.Renamed = null;
            this.Error = null;
            this.disposed = true;
        }

        private void HandleDomainUnload(object sender, EventArgs e)
        {
            this.Dispose();
        }


        public bool EnableRaisingEvents
        {
            get => this.currentHandle != null;
            set
            {
                if (value)
                {
                    if (this.disposed) throw new ObjectDisposedException(nameof(WindowsFileSystemWatcher));
                    if (this.currentHandle != null) return;
                    this.currentHandle = CreateDirectoryHandle(this.Path);
                    this.monitorTask = Task.Factory.StartNew(() => this.Monitor(this.currentHandle), TaskCreationOptions.LongRunning);
                }
                else
                {
                    // This cancels scheduled-but-unrun events, because they don't run if the handle is closed.
                    this.currentHandle?.Dispose();
                    this.currentHandle = null;
                    this.monitorTask?.Wait();
                    this.monitorTask = null;
                    // We don't wait for the events task, because we might be within the events task.
                    // (Ooh, the events are coming from WITHIN THE TASK. Scary.)
                    // This does leave a tiny chance that a single event could be triggered immediately after this,
                    // if we're not in the events task...
                }
            }
        }

        private static InterruptibleHandle CreateDirectoryHandle(string directory)
        {
            const int FILE_LIST_DIRECTORY = 0x0001;
            const int FILE_SHARE_READ = 0x00000001;
            const int FILE_SHARE_WRITE = 0x00000002;
            const int FILE_SHARE_DELETE = 0x00000004;
            const int OPEN_EXISTING = 3;
            const int FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;

            // There might be a way to do this without the OS call?
            var directoryHandle = CreateFile(
                lpFileName: directory,
                dwDesiredAccess: FILE_LIST_DIRECTORY,
                dwShareMode: FILE_SHARE_READ | FILE_SHARE_DELETE | FILE_SHARE_WRITE,
                lpSecurityAttributes: null,
                dwCreationDisposition: OPEN_EXISTING,
                dwFlagsAndAttributes: FILE_FLAG_BACKUP_SEMANTICS,
                hTemplateFile: new SafeFileHandle(IntPtr.Zero, false)
            );

            if (directoryHandle == null || directoryHandle.IsInvalid)
                throw new IOException("Failed to obtain handle for directory.");

            return new InterruptibleHandle(directoryHandle);
        }

        private unsafe void Monitor(InterruptibleHandle handle)
        {
            // We try to minimise the processing time taken by the monitoring thread.
            // The longer it takes, the more likely a buffer overflow.
            // (At our 64*1028 buffer size we're probably 99.9% fine anyway. Famous last words...)
            // We do this by pushing processing immediately to another thread.
            // We swap out the buffers instead of spending any time reading them.
            // This is probably a silly micro optimisation, but it feels like "the right way".

            const int MaxBufferPoolSize = 32; // Huge
            var bufferPool = new Stack<byte[]>(MaxBufferPoolSize);

            while (handle.IsOpen)
            {
                byte[] buffer;
                lock (bufferPool) if (!bufferPool.TryPop(out buffer)) buffer = new byte[64 * 1024];

                fixed (byte* bufferPointer = buffer)
                {
                    bool ok = false;
                    int size = 0;

                    try
                    {
                        ok = ReadDirectoryChangesW(
                            hDirectory: handle,
                            lpBuffer: new HandleRef(buffer, (IntPtr)bufferPointer),
                            nBufferLength: buffer.Length,
                            bWatchSubtree: this.IncludeSubdirectories ? 1 : 0,
                            dwNotifyFilter: (int)this.NotifyFilter,
                            lpBytesReturned: out size,
                            overlappedPointer: null,
                            lpCompletionRoutine: new HandleRef(null, IntPtr.Zero)
                        );
                    }
                    // The directory handle could be disposed from another thread.
                    // That's fine, we'll just end.
                    catch (ObjectDisposedException) { }
                    catch (Exception ex)
                    {
                        DispatchError(ex);
                    }

                    if (!handle.IsOpen)
                        break;

                    if (!ok)
                        DispatchError(new Win32Exception());

                    if (size == 0)
                        DispatchError(new InternalBufferOverflowException($"Too many changes at once in directory: {this.Path}."));
                }

                // Let's prevent event dispatches from overlapping or being out of order,
                // because this is closer to FileSystemWatcher's behaviour.
                // Overlapping/OOO would be a pretty easy way to get a nasty bug in user code.
                this.eventsTask = this.eventsTask.ContinueWith(_ =>
                {
                    this.ProcessBufferOnEventThread(handle, buffer);

                    // Return to pool, preventing strange leak scenarios where the pool grows unreasonably large.
                    // That could happen if the event threads run for a very long time.
                    lock (bufferPool)
                    {
                        if (bufferPool.Count < MaxBufferPoolSize) bufferPool.Push(buffer);
                    }
                });
            }

            handle.Dispose();


            void DispatchError(Exception ex)
            {
                this.eventsTask = this.eventsTask.ContinueWith(_ => this.Error?.Invoke(this, new ErrorEventArgs(ex)));
            }
        }

        private void ProcessBufferOnEventThread(InterruptibleHandle handle, ReadOnlySpan<byte> buffer)
        {
            ReadOnlySpan<char> oldName = default;
            bool oldMatch = false;

            while (true)
            {
                if (handle == null || !handle.IsOpen) break;

                // We're dealing with file names as Spans for two reasons:
                //  - FileSystemName wants them that way.
                //  - We can avoid the string allocation for files that don't match.

                var nextEntryOffset = MemoryMarshal.Read<int>(buffer);
                var action = MemoryMarshal.Read<Action>(buffer.Slice(4));
                var nameLength = MemoryMarshal.Read<int>(buffer.Slice(8));
                var name = MemoryMarshal.Cast<byte, char>(buffer.Slice(12, nameLength));
                buffer = buffer.Slice(nextEntryOffset);

                var match = string.IsNullOrEmpty(this.Filter) || FileSystemName.MatchesSimpleExpression(this.Filter, name);

                try
                {
                    switch (action)
                    {
                        case Action.RenamedOld:
                            oldName = name;
                            oldMatch = match;
                            break;

                        case Action.RenamedNew:
                            if (match | oldMatch)
                                this.Renamed?.Invoke(this, new RenamedEventArgs(WatcherChangeTypes.Renamed, this.Path, name.ToString(), oldName.ToString()));
                            break;

                        default:
                            if (!match) break;
                            var nameStr = name.ToString();

                            switch (action)
                            {
                                case Action.Added:
                                    this.Created?.Invoke(this, new FileSystemEventArgs(WatcherChangeTypes.Created, this.Path, nameStr));
                                    break;
                                case Action.Modified:
                                    this.Changed?.Invoke(this, new FileSystemEventArgs(WatcherChangeTypes.Changed, this.Path, nameStr));
                                    break;
                                case Action.Removed:
                                    this.Deleted?.Invoke(this, new FileSystemEventArgs(WatcherChangeTypes.Deleted, this.Path, nameStr));
                                    break;
                            }
                            break;
                    }
                }
                catch (Exception ex)
                {
                    this.Error?.Invoke(this, new ErrorEventArgs(new Exception("Exception in event handler.", ex)));
                }

                if (nextEntryOffset == 0) break;
            }
        }


        private class InterruptibleHandle : IDisposable
        {
            public SafeFileHandle Handle { get; }
            public bool IsOpen => !this.closed & !this.Handle.IsInvalid & !this.Handle.IsClosed;
            private bool closed;

            public InterruptibleHandle(SafeFileHandle handle)
            {
                this.Handle = handle;
            }

            public unsafe void Dispose()
            {
                this.closed = true;
                if (!(this.Handle.IsInvalid | this.Handle.IsClosed)) CancelIoEx(this.Handle, null);
                this.Handle.Dispose();
            }

            public static implicit operator SafeFileHandle(InterruptibleHandle handle)
            {
                return handle.Handle;
            }
        }


        #region Windows API
        private enum Action
        {
            Added = 1,
            Removed = 2,
            Modified = 3,
            RenamedOld = 4,
            RenamedNew = 5,
        }

        [DllImport("__Internal", CharSet = CharSet.Auto, BestFitMapping = false)]
        private static extern SafeFileHandle CreateFile(
            string lpFileName,
            int dwDesiredAccess,
            int dwShareMode,
            SECURITY_ATTRIBUTES lpSecurityAttributes,
            int dwCreationDisposition,
            int dwFlagsAndAttributes,
            SafeFileHandle hTemplateFile
        );

        [DllImport("__Internal", EntryPoint = "ReadDirectoryChangesW", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern unsafe bool ReadDirectoryChangesW(
            SafeFileHandle hDirectory,
            HandleRef lpBuffer,
            int nBufferLength,
            int bWatchSubtree,
            int dwNotifyFilter,
            out int lpBytesReturned,
            NativeOverlapped* overlappedPointer,
            HandleRef lpCompletionRoutine
        );

        [DllImport("__Internal")]
        internal static extern unsafe bool CancelIoEx(SafeHandle handle, NativeOverlapped* lpOverlapped);

        private class SECURITY_ATTRIBUTES { }
        #endregion
    }
}
