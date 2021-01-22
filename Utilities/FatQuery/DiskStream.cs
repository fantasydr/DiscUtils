using Microsoft.Win32.SafeHandles;
using System;
using System.IO;
using System.Runtime.InteropServices;

namespace FatQuery
{
    /// <summary>
    /// P/Invoke wrappers around Win32 functions and constants.
    /// </summary>
    internal partial class DeviceIO
    {
        #region Constants used in unmanaged functions

        public const uint FILE_SHARE_READ = 0x00000001;
        public const uint FILE_SHARE_WRITE = 0x00000002;
        public const uint FILE_SHARE_DELETE = 0x00000004;
        public const uint OPEN_EXISTING = 3;

        public const uint GENERIC_READ = (0x80000000);
        public const uint GENERIC_WRITE = (0x40000000);

        public const uint FILE_FLAG_NO_BUFFERING = 0x20000000;
        public const uint FILE_FLAG_WRITE_THROUGH = 0x80000000;
        public const uint FILE_READ_ATTRIBUTES = (0x0080);
        public const uint FILE_WRITE_ATTRIBUTES = 0x0100;
        public const uint ERROR_INSUFFICIENT_BUFFER = 122;

        #endregion

        #region Unamanged function declarations

        [DllImport("kernel32.dll", SetLastError = true)]
        public static unsafe extern SafeFileHandle CreateFile(
            string FileName,
            uint DesiredAccess,
            uint ShareMode,
            IntPtr SecurityAttributes,
            uint CreationDisposition,
            uint FlagsAndAttributes,
            IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CloseHandle(SafeFileHandle hHandle);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool DeviceIoControl(
            SafeFileHandle hDevice,
            uint dwIoControlCode,
            IntPtr lpInBuffer,
            uint nInBufferSize,
            [Out] IntPtr lpOutBuffer,
            uint nOutBufferSize,
            ref uint lpBytesReturned,
            IntPtr lpOverlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern unsafe bool WriteFile(
            SafeFileHandle hFile,
            byte* pBuffer,
            uint NumberOfBytesToWrite,
            uint* pNumberOfBytesWritten,
            IntPtr Overlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern unsafe bool ReadFile(
            SafeFileHandle hFile,
            byte* pBuffer,
            uint NumberOfBytesToRead,
            uint* pNumberOfBytesRead,
            IntPtr Overlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool SetFilePointerEx(
            SafeFileHandle hFile,
            long liDistanceToMove,
            out long lpNewFilePointer,
            uint dwMoveMethod);

        [DllImport("kernel32.dll")]
        public static extern bool FlushFileBuffers(
            SafeFileHandle hFile);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        public static extern bool GetDiskFreeSpace(string lpRootPathName,
           out uint lpSectorsPerCluster,
           out uint lpBytesPerSector,
           out uint lpNumberOfFreeClusters,
           out uint lpTotalNumberOfClusters);


        #endregion

    }

    public struct DiskInfo
    {
        public ulong Size;

        public string RootPathName;
        public uint SectorsPerCluster;
        public uint BytesPerSector;
        public uint NumberOfFreeClusters;
        public uint TotalNumberOfClusters;

        public DiskInfo(string rootPathName)
        {
            DeviceIO.GetDiskFreeSpace(rootPathName, out this.SectorsPerCluster, out this.BytesPerSector, out this.NumberOfFreeClusters, out this.TotalNumberOfClusters);

            this.RootPathName = rootPathName;
            this.Size = (ulong)BytesPerSector * (ulong)SectorsPerCluster * (ulong)TotalNumberOfClusters;
        }
    }

    public class DiskStream : Stream
    {
        private string _diskID;
        private DiskInfo _diskInfo;
        private SafeFileHandle _fileHandle;

        private long _currentPosition;
        private byte[] _sectorBuffer;

        public DiskInfo DiskInfo
        {
            get { return this._diskInfo; }
        }

        public DiskStream(string driveLetter)
        {
            this._diskID = string.Format(@"\\.\{0}:", driveLetter);
            this._diskInfo = new DiskInfo(_diskID + @"\");
            this._fileHandle = this.OpenFile(_diskID);
            this._sectorBuffer = new byte[this.DiskInfo.BytesPerSector];
        }

        private SafeFileHandle OpenFile(string diskID)
        {
            SafeFileHandle ptr = DeviceIO.CreateFile(
                diskID,
                DeviceIO.GENERIC_READ,
                DeviceIO.FILE_SHARE_READ,
                IntPtr.Zero,
                DeviceIO.OPEN_EXISTING,
                0,
                IntPtr.Zero);

            if (ptr.IsInvalid)
            {
                Marshal.ThrowExceptionForHR(Marshal.GetHRForLastWin32Error());
            }

            return ptr;
        }

        public override bool CanRead => true;
        public override bool CanWrite => false;
        public override bool CanSeek => true;
        public override long Length => (long)this._diskInfo.Size;
        public override long Position
        { 
            get
            {
                return (long)_currentPosition;
            }
            set
            {
                Seek(value, SeekOrigin.Begin);
            }
        }

        public override void Flush()
        {
            // not required, since FILE_FLAG_WRITE_THROUGH and FILE_FLAG_NO_BUFFERING are used
            //if (!Unmanaged.FlushFileBuffers(this.fileHandle))
            //    Marshal.ThrowExceptionForHR(Marshal.GetHRForLastWin32Error());
        }

        public override void Close()
        {
            if (this._fileHandle != null)
            {
                DeviceIO.CloseHandle(this._fileHandle);
                this._fileHandle.SetHandleAsInvalid();
                this._fileHandle = null;
            }
            base.Close();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException("Setting the length is not supported with DiskStream objects.");
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var bufferLength = (uint)_sectorBuffer.Length; // should be bytePerSector

            // TODO: optimize the sector aligned position
            var filepos = this.GetFilePosition();
            if (filepos <= _currentPosition)
            {
                filepos += ReadInternal(_sectorBuffer, 0, bufferLength);
                if (filepos < _currentPosition)
                    throw new InvalidOperationException("Invalid gap between file pos and stream pos");
            }

            var bufferOffset = (uint)(_currentPosition % bufferLength);
            var nextPos = _currentPosition + count;

            if (nextPos <= filepos) // less than one sector
            {
                _currentPosition = nextPos;
                Array.Copy(_sectorBuffer, bufferOffset, buffer, offset, count);
            }
            else
            {
                var leftBytes = (uint)count;
                var targetOffset = (uint)offset;

                var firstPart = bufferLength - bufferOffset;
                Array.Copy(_sectorBuffer, bufferOffset, buffer, targetOffset, firstPart);
                targetOffset += firstPart;
                _currentPosition += firstPart;
                leftBytes -= firstPart;

                var wholeblocks = (leftBytes / bufferLength) * bufferLength;
                if (wholeblocks > 0)
                {
                    var readedBlock = ReadInternal(buffer, targetOffset, wholeblocks);
                    if(readedBlock != wholeblocks)
                        throw new InvalidOperationException("Wrong stream position");

                    targetOffset += wholeblocks;
                    _currentPosition += wholeblocks;
                    leftBytes -= wholeblocks;
                }

                if(leftBytes > 0)
                {
                    var readed = ReadInternal(_sectorBuffer, 0, bufferLength);
                    if (readed != bufferLength)
                        throw new InvalidOperationException("Wrong stream position");

                    Array.Copy(_sectorBuffer, 0, buffer, targetOffset, leftBytes);
                    _currentPosition += leftBytes;
                }
            }

            return count;
        }

        private unsafe uint ReadInternal(byte[] buffer, uint offset, uint count)
        {
            uint n = 0;
            fixed (byte* p = buffer)
            {
                if (!DeviceIO.ReadFile(this._fileHandle, p + offset, count, &n, IntPtr.Zero))
                    Marshal.ThrowExceptionForHR(Marshal.GetHRForLastWin32Error());
            }
            return n;
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotImplementedException("Write is not supported for disk stream");
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            var nextPosition = (long)_currentPosition;

            if (origin == SeekOrigin.Begin)
                nextPosition = offset;
            else if (origin == SeekOrigin.End)
                nextPosition = (long)this.DiskInfo.Size + offset;
            else // if (origin == SeekOrigin.Current)
                nextPosition += offset;

            if (nextPosition > (this.Length - 1))
                throw new EndOfStreamException("Cannot set position beyond the end of the disk.");

            _currentPosition = nextPosition;
            long sectorAlignPosition = GetSectorAlignPosition();

            if (origin == SeekOrigin.Begin)
                return SeekFilePosition(sectorAlignPosition, origin);
            else if (origin == SeekOrigin.End)
                return SeekFilePosition(sectorAlignPosition - (long)this.DiskInfo.Size, origin);
            else // if (origin == SeekOrigin.Current)
                return SeekFilePosition(sectorAlignPosition - GetFilePosition(), origin);
        }

        private long GetSectorAlignPosition()
        {
            var bytePerSector = this.DiskInfo.BytesPerSector;
            var sector = _currentPosition / bytePerSector;
            return sector * bytePerSector;
        }

        private long GetFilePosition()
        {
            long n = 0;
            if (!DeviceIO.SetFilePointerEx(this._fileHandle, 0, out n, (uint)SeekOrigin.Current))
                Marshal.ThrowExceptionForHR(Marshal.GetHRForLastWin32Error());

            return n;
        }

        private long SeekFilePosition(long offset, SeekOrigin origin)
        {
            long n = 0;
            if (!DeviceIO.SetFilePointerEx(this._fileHandle, offset, out n, (uint)origin))
                Marshal.ThrowExceptionForHR(Marshal.GetHRForLastWin32Error());

            return n;
        }
    }
}
