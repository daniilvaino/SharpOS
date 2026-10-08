using System;
using System.IO;

namespace SharpOS.AppSdk
{
    /// <summary>
    /// A file opened through the kernel (step197): read in pieces from the
    /// start, or written — created, cut or appended to — in pieces. What
    /// FileStream, File.WriteAllBytes and READ/WRITE stand on.
    /// </summary>
    public sealed unsafe class AppFile : IDisposable
    {
        public const int ModeRead = 0, ModeWrite = 1, ModeAppend = 2;

        private int _handle;
        private readonly string _path;

        private AppFile(int handle, string path)
        {
            _handle = handle;
            _path = path;
        }

        /// <summary>Opens <paramref name="path"/>; FileNotFoundException or IOException when it cannot.</summary>
        public static AppFile Open(string path, int mode)
        {
            if (path == null) throw new ArgumentNullException(nameof(path));
            path = Process.ResolvePath(path);
            AppServiceTable* t = AppRuntime.Services;
            if (t == null || t->FileOpenAddress == 0)
                throw new IOException("cannot open '" + path + "': this kernel has no file service");
            byte* ascii = stackalloc byte[path.Length + 1];
            for (int i = 0; i < path.Length; i++)
            {
                char c = path[i] == '/' ? '\\' : path[i];
                if (c > 0x7F) throw new IOException("cannot open '" + path + "': the path is not ASCII");
                ascii[i] = (byte)c;
            }
            ascii[path.Length] = 0;
            int handle;
            var status = (AppServiceStatus)((delegate* unmanaged<ulong, int, int*, int>)t->FileOpenAddress)((ulong)ascii, mode, &handle);
            if (status == AppServiceStatus.NotFound)
                throw new FileNotFoundException("Unable to find the specified file.", path);
            if (status == AppServiceStatus.InvalidParameter)
                throw new IOException("cannot open '" + path + "': " + (mode == ModeRead ? "it is a directory" : "FAT cannot hold the name, or it is a directory"));
            if (status == AppServiceStatus.LimitReached)
                throw new IOException("cannot create '" + path + "': no room in its directory or on the volume");
            if (status == AppServiceStatus.Busy)
                throw new IOException("cannot open '" + path + "' for writing: another program is writing it");
            if (status != AppServiceStatus.Ok)
                throw new IOException("cannot open '" + path + "' (status " + ((uint)status).ToString() + ")");
            return new AppFile(handle, path);
        }

        public string Path => _path;

        /// <summary>Bytes read into the span; 0 at the end.</summary>
        public int Read(Span<byte> into)
        {
            if (_handle == 0) throw new ObjectDisposedException("AppFile");
            int got;
            fixed (byte* p = into)
            {
                var status = (AppServiceStatus)((delegate* unmanaged<int, byte*, int, int*, int>)AppRuntime.Services->FileReadAddress)(_handle, p, into.Length, &got);
                if (status != AppServiceStatus.Ok) throw new IOException("cannot read '" + _path + "'");
            }
            return got;
        }

        public void Write(ReadOnlySpan<byte> data)
        {
            if (_handle == 0) throw new ObjectDisposedException("AppFile");
            if (data.Length == 0) return;
            fixed (byte* p = data)
            {
                var status = (AppServiceStatus)((delegate* unmanaged<int, byte*, int, int>)AppRuntime.Services->FileWriteAddress)(_handle, p, data.Length);
                if (status != AppServiceStatus.Ok) throw new IOException("cannot write '" + _path + "': the volume is full or failed");
            }
        }

        /// <summary>Closes the file: a written one gets its size on the disk.</summary>
        public void Dispose()
        {
            int h = _handle;
            if (h == 0) return;
            _handle = 0;
            var status = (AppServiceStatus)((delegate* unmanaged<int, int>)AppRuntime.Services->FileCloseAddress)(h);
            if (status != AppServiceStatus.Ok) throw new IOException("cannot close '" + _path + "'");
        }
    }
}
