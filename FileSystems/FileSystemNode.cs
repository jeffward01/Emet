/* vi:ts=2
 */

using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Emet.FileSystems {
	public enum FileType : int {
		DoesNotExist = 0,
		Fifo = 0b1_000_000_000_000,
		CharacterSpecial = 0b10_000_000_000_000,
		Directory = 0b100_000_000_000_000,
		BlockSpecial = 0b110_000_000_000_000,
		File = 0b1_000_000_000_000_000,
		SymbolicLink = 0b1_010_000_000_000_000,
		Socket = 0b1_100_000_000_000_000,
		NodeHintNotAvailable = 0x10000,
		LinkTargetHintNotAvailable = 0x20000,
		NamedPipe = 0x40000,
		ReparsePoint = 0x80000
	}

	public class FileSystemNode {
    // These are not const in case we have to port to something that uses something else.
    ///<summary>Name of the current directory within itself</summary>
		public static readonly string CurrentDirectoryName = ".";
    ///<summary>Name of the parent directory within the current directory</summary>
		public static readonly string ParentDirectoryName = "..";

#if OSTYPE_UNIX
		internal const int FileTypeMask = 0b1_111_000_000_000_000;
#endif

		///<summary>Creates a FileSystemNode object from a handle</summary>
		///<remarks>FileSystemNode is lazy-initialized; call Refresh() to force eager initialization</remarks>
		public FileSystemNode(SafeFileHandle handle)
		{
			reference = handle;
		}

		///<summary>Reloads the file node information</summary>
		///<exception cref="System.IO.IOException">A disk IO exception occurred resolving the node</exception>
		///<returns>this</returns>
		public FileSystemNode Refresh() { _Refresh(); return this; }

		protected virtual void _Refresh() => _Refresh(reference);

		protected void _Refresh(SafeFileHandle handle)
		{
			bool success = false;
			try {
				handle.DangerousAddRef(ref success);
				if (!success) {
					if (!handle.IsInvalid)
						throw new InvalidOperationException("SafeFileHandle.DangerousAddRef failed but the handle isn't invalid.");
					errorCode = IOErrors.NoSuchHandle;
					Clear();
				}
#if OS_LINUXX64
				var statbuf = new NativeMethods.statbuf64();
				if (NativeMethods.__fxstat64(NativeMethods.statbuf_version, handle.DangerousGetHandle(), out statbuf) != 0) {
					var errno = Marshal.GetLastWin32Error();
					var ci = new System.ComponentModel.Win32Exception();
					throw new IOException(ci.Message, errno);
				}
				FillStatResult(ref statbuf);
#elif OS_MACOSX64
				var statbuf = new NativeMethods.statbuf();
				if (NativeMethods.fstat(handle.DangerousGetHandle(), out statbuf) != 0) {
					var errno = Marshal.GetLastWin32Error();
					var ci = new System.ComponentModel.Win32Exception();
					throw new IOException(ci.Message, errno);
				}
				FillStatResult(ref statbuf);
#elif OS_WIN
				LoadFromHandle(handle.DangerousGetHandle());
#else
				throw null;
#endif
			} finally {
				if (success) handle.DangerousRelease();
			}
		}

		internal void Clear()
		{
			deviceNumber = 0;
			inodeNumber = 0;
			fileType = FileType.DoesNotExist;
			links = 0;
			fileSize = 0;
			bytesUsed = 0;
			lastAccessTime = DateTime.MinValue;
			lastModificationTime = DateTime.MinValue;
			lastChangeTime = DateTime.MinValue;
			birthTime = DateTime.MinValue;
			loaded = true;
#if OS_WIN
			loaded2 = true;
#endif
		}

#if OSTYPE_UNIX
		internal static DateTime UnixTimeToDateTime(long seconds, ulong nanoseconds)
			=> NativeMethods.Epoch.AddTicks(
				unchecked(seconds * TimeSpan.TicksPerSecond
								+ (long)nanoseconds * TimeSpan.TicksPerMillisecond / 1000000));

#if OS_LINUXX64
		internal void FillStatResult(ref NativeMethods.statbuf64 statbuf)
		{
			errorCode = 0;
			deviceNumber = unchecked((long)statbuf.st_dev);
			inodeNumber = unchecked((long)statbuf.st_ino);
			fileType = (FileType)(statbuf.st_mode & FileTypeMask);
			links = unchecked((long)statbuf.st_nlink);
			fileSize = unchecked((long)statbuf.st_size);
			bytesUsed = unchecked((long)statbuf.st_blocks * 512);
			lastAccessTime = UnixTimeToDateTime(statbuf.st_atime, statbuf.st_atime_nsec);
			lastModificationTime = UnixTimeToDateTime(statbuf.st_mtime, statbuf.st_mtime_nsec);
			lastChangeTime = UnixTimeToDateTime(statbuf.st_ctime, statbuf.st_ctime_nsec);
			loaded = true;
		}
#else
		internal void FillStatResult(ref NativeMethods.statbuf statbuf)
		{
			errorCode = 0;
			deviceNumber = unchecked((long)statbuf.st_dev);
			inodeNumber = unchecked((long)statbuf.st_ino);
			fileType = (FileType)(statbuf.st_mode & FileTypeMask);
			links = unchecked((long)statbuf.st_nlink);
			fileSize = unchecked((long)statbuf.st_size);
			bytesUsed = unchecked((long)statbuf.st_blocks * 512);
			lastAccessTime = UnixTimeToDateTime(statbuf.st_atime.tv_sec, statbuf.st_atime.tv_nsec);
			lastModificationTime = UnixTimeToDateTime(statbuf.st_mtime.tv_sec, statbuf.st_mtime.tv_nsec);
			lastChangeTime = UnixTimeToDateTime(statbuf.st_ctime.tv_sec, statbuf.st_ctime.tv_nsec);
#if OS_MACOSX64
			birthTime = UnixTimeToDateTime(statbuf.st_birthtime.tv_sec, statbuf.st_ctime.tv_nsec);
#endif
			loaded = true;
		}
#endif
#endif

#if OS_WIN
		internal void FillFindDataResult(ref NativeMethods.WIN32_FIND_DATA ff)
		{
			errorCode = 0;
			fileType = FileAttributesToFileType(ff.dwFileAttributes, ff.dwReserved0);
			fileSize = unchecked((long)(((ulong)ff.nFileSizeHigh << 32) | ff.nFileSizeLow));
			birthTime = FileTimeToDateTime(ff.ftCreationTime);
			lastAccessTime = FileTimeToDateTime(ff.ftLastAccessTime);
			lastModificationTime = FileTimeToDateTime(ff.ftLastWriteTime);
			loaded = true;
			loaded2 = false;
		}

		internal static FileType FileAttributesToFileType(uint dwFileAttributes, uint dwReserved0)
		{
			if ((dwFileAttributes & NativeMethods.FILE_ATTRIBUTE_REPARSE_POINT) != 0)
				return dwReserved0 == 0xA000000C ? FileType.SymbolicLink : FileType.ReparsePoint;
			else if ((dwFileAttributes & NativeMethods.FILE_ATTRIBUTE_DIRECTORY) != 0)
				return FileType.Directory;
			else
				return FileType.File;
		}

		internal void FillMakeStuffUpBecauseInaccessible()
		{
			if (lastChangeTime == DateTime.MinValue) lastChangeTime = lastModificationTime;
			loaded2 = true;
		}

		internal uint LoadFromHandle(IntPtr handle)
		{
			var io = new NativeMethods.IO_STATUS_BLOCK();
			var volume = new NativeMethods.FILE_FS_VOLUME_INFORMATION();
			var all = new NativeMethods.FILE_ALL_INFORMATION();
			int errno;
			errno = NativeMethods.NtQueryVolumeInformationFile(handle, out io, out volume,
					Marshal.SizeOf<NativeMethods.FILE_FS_VOLUME_INFORMATION>(), NativeMethods.FileFsVolumeInformation);
			if (errno != 0) errno = NativeMethods.RtlNtStatusToDosError(errno);
			if (errno != 0 && errno != (IOErrors.InsufficientBuffer & 0xFFFF)) {
				NativeMethods.SetLastError(errno);
				var ci = new System.ComponentModel.Win32Exception();
				throw new IOException(ci.Message, unchecked((int)0x80070000 | errno));
			}
			errno = NativeMethods.NtQueryInformationFile(handle, out io, out all,
					Marshal.SizeOf<NativeMethods.FILE_ALL_INFORMATION>(), NativeMethods.FileAllInformation);
			if (errno != 0 && errno != unchecked((int)0x80000005) || all.InternalInformation.file_index == 0)
			{
				// Broken filesystem -- try the slow way
				errno = NativeMethods.NtQueryInformationFile(handle, out io, out all.StandardInformation,
						Marshal.SizeOf<NativeMethods.FILE_STANDARD_INFORMATION>(), NativeMethods.FileStandardInformation);
				if (errno != 0) {
					errno = NativeMethods.RtlNtStatusToDosError(errno);
					NativeMethods.SetLastError(errno);
					var ci = new System.ComponentModel.Win32Exception();
					throw new IOException(ci.Message, unchecked((int)0x80070000 | errno));
				}
				errno = NativeMethods.NtQueryInformationFile(handle, out io, out all.BasicInformation,
						Marshal.SizeOf<NativeMethods.FILE_BASIC_INFORMATION>(), NativeMethods.FileBasicInformation);
				if (errno != 0) {
					errno = NativeMethods.RtlNtStatusToDosError(errno);
					NativeMethods.SetLastError(errno);
					var ci = new System.ComponentModel.Win32Exception();
					throw new IOException(ci.Message, unchecked((int)0x80070000 | errno));
				}
				errno = NativeMethods.NtQueryInformationFile(handle, out io, out all.InternalInformation,
						Marshal.SizeOf<NativeMethods.FILE_INTERNAL_INFORMATION>(), NativeMethods.FileInternalInformation);
				if (errno != 0) {
					errno = NativeMethods.RtlNtStatusToDosError(errno);
					NativeMethods.SetLastError(errno);
					var ci = new System.ComponentModel.Win32Exception();
					throw new IOException(ci.Message, unchecked((int)0x80070000 | errno));
				}
			}
			birthTime = UlongToDateTime(all.BasicInformation.CreationTime);
			lastAccessTime = UlongToDateTime(all.BasicInformation.LastAccessTime);
			lastModificationTime = UlongToDateTime(all.BasicInformation.LastWriteTime);
			lastChangeTime = UlongToDateTime(all.BasicInformation.ChangeTime);
			fileType = FileAttributesToFileType(all.BasicInformation.FileAttributes, 0);
			fileSize = (long)all.StandardInformation.EndOfFile;
			bytesUsed = (long)all.StandardInformation.AllocationSize;
			links = (long)all.StandardInformation.NumberOfLinks;
			inodeNumber = unchecked((long)all.InternalInformation.file_index);
			deviceNumber = volume.VolumeSerialNumber;
			if (fileType == FileType.ReparsePoint) {
				uint buflen = 1024;
				uint hdrsize = (uint)Marshal.SizeOf<NativeMethods.REPARSE_DATA_BUFFER_SYMLINK>();
				for(;;) {
					var results = new byte[buflen];
					if (0 == NativeMethods.DeviceIoControl(handle, NativeMethods.FSCTL_GET_REPARSE_POINT,
							IntPtr.Zero, 0, results, buflen, out uint returned, IntPtr.Zero)) {
						var errno2 = (int)Marshal.GetLastWin32Error();
						if (errno2 == IOErrors.ERROR_MORE_DATA) continue; // Here's where we go around the loop
						var ci = new System.ComponentModel.Win32Exception();
						throw new IOException(ci.Message, unchecked((int)0x80070000 | errno2));
					}
					GCHandle gch;
					try {
						gch = GCHandle.Alloc(results, GCHandleType.Pinned);
						var symdata = Marshal.PtrToStructure<NativeMethods.REPARSE_DATA_BUFFER_SYMLINK>(gch.AddrOfPinnedObject());
						if (symdata.ReparseTag == 0xA000000C)
							fileType = FileType.SymbolicLink;
					} finally {
						gch.Free();
					}
					break;
				}
			}
			loaded = true;
			loaded2 = true;
			return all.BasicInformation.FileAttributes;
		}

		internal static DateTime FileTimeToDateTime(NativeMethods.FILETIME filetime)
			=> UlongToDateTime(((ulong)filetime.dwHighDateTime) << 32 | filetime.dwLowDateTime);

		internal static DateTime UlongToDateTime(ulong datetime)
			// Documentation literally says 100ns in both places
			=> NativeMethods.Epoch.AddTicks((long)datetime);
#endif

		private SafeFileHandle reference;
		private int errorCode;
		private FileType fileType;
		private long fileSize;
		private long bytesUsed;
		private long deviceNumber;
		private long inodeNumber;
		private long links;
		private DateTime lastAccessTime;
		private DateTime lastModificationTime;
		private DateTime lastChangeTime;
		private DateTime birthTime;
		private bool loaded;
#if OS_WIN
		private bool loaded2;
#endif

		///<summary>Returns the error code from stat() or the moral equivalent</summary>
		///<exception cref="System.IO.IOException">A disk IO exception occurred resolving the node</exception>
		///<remarks>Returns IOErrors.Success if the node exists</remarks>
		public int ErrorCode { get { if (!loaded) _Refresh(); return errorCode; } }
		///<summary>Returns the type of the file system node</summary>
		///<exception cref="System.IO.IOException">A disk IO exception occurred resolving the node</exception>
		public virtual FileType FileType { get { if (!loaded) _Refresh(); return fileType; } }
		///<summary>Returns the number of hard links to this file</summary>
		///<exception cref="System.IO.IOException">A disk IO exception occurred resolving the node</exception>
#if OS_WIN
		public long LinkCount { get { if (!loaded2) _Refresh(); return links; } }
#elif OSTYPE_UNIX
		public long LinkCount { get { if (!loaded) _Refresh(); return links; } }
#else
		public long LinkCount => throw null;
#endif
		///<summary>Returns the identifier of the device the file is on</summary>
		///<exception cref="System.IO.IOException">A disk IO exception occurred resolving the node</exception>
		///<remarks>If this returns DoesNotExist, check ErrorCode to find out why</remarks>
#if OS_WIN
		public long DeviceNumber { get { if (!loaded2) _Refresh(); return deviceNumber; } }
#elif OSTYPE_UNIX
		public long DeviceNumber { get { if (!loaded) _Refresh(); return deviceNumber; } }
#else
		public long DeviceNumber => throw null;
#endif
		///<summary>Returns the identifier of the file on its device</summary>
		///<exception cref="System.IO.IOException">A disk IO exception occurred resolving the node</exception>
		///<remarks>Symbolic links on Windows like to return the inode number of the backing file from the Native API calls</remarks>
#if OS_WIN
		public long InodeNumber { get { if (!loaded2) _Refresh(); return inodeNumber; } }
#elif OSTYPE_UNIX
		public long InodeNumber { get { if (!loaded) _Refresh(); return inodeNumber; } }
#else
		public long InodeNumber => throw null;
#endif
		///<summary>Returns the size of the file on disk</summary>
		///<remarks>Not useful for block or character devices</remarks>
		///<exception cref="System.IO.IOException">A disk IO exception occurred resolving the node</exception>
		public long FileSize { get { if (!loaded) _Refresh(); return fileSize; } }
		///<summary>Returns the number of bytes allocated to the file</summary>
		///<exception cref="System.IO.IOException">A disk IO exception occurred resolving the node</exception>
#if OS_WIN
		public long BytesUsed { get { if (!loaded2) _Refresh(); return bytesUsed; } }
#elif OSTYPE_UNIX
		public long BytesUsed { get { if (!loaded) _Refresh(); return bytesUsed; } }
#else
		public long BytesUsed => throw null;
#endif
		///<summary>Returns the last access time, if available</summary>
		///<remarks>If the filesystem doesn't support it, returns whatever fake result the filesystem driver provided</remarks>
		///<exception cref="System.IO.IOException">A disk IO exception occurred resolving the node</exception>
		public DateTime LastAccessTimeUTC { get { if (!loaded) _Refresh(); return lastAccessTime; } }
		///<summary>Returns the last modification time of the file</summary>
		///<remarks>If this is a removable device, this might return a time in a time zone other than declared</remarks>
		///<exception cref="System.IO.IOException">A disk IO exception occurred resolving the node</exception>
		public DateTime LastModificationTimeUTC { get { if (!loaded) _Refresh(); return lastModificationTime; } }
		///<summary>Returns the secure last change time, if available</summary>
		///<remarks>If the filesystem doesn't support it, returns whatever fake result the filesystem driver provided</remarks>
		///<exception cref="System.IO.IOException">A disk IO exception occurred resolving the node</exception>
#if OS_WIN
		public DateTime LastChangeTimeUTC { get { if (!loaded2) _Refresh(); return lastChangeTime; } }
#elif OSTYPE_UNIX
		public DateTime LastChangeTimeUTC { get { if (!loaded) _Refresh(); return lastChangeTime; } }
#else
		public DateTime LastChangeTimeUTC => throw null;
#endif
#if OS_LINUXX64
		public DateTime CreationTimeUTC => LastChangeTimeUTC;
#elif OS_MACOSX64
		public DateTime CreationTimeUTC { get { if (!loaded) _Refresh(); return birthTime; } }
#elif OS_WIN
		public DateTime CreationTimeUTC { get { if (!loaded) _Refresh(); return birthTime; } }
#else
		///<summary>Returns the secure last change time, if available</summary>
		///<remarks>If the filesystem doesn't support it, returns whatever fake result the filesystem driver provided;
		///however if the OS doesn't support it it returns ctime</remakrs>
		///<exception cref="System.IO.IOException">A disk IO exception occurred resolving the node</exception>
		public DateTime CreationTimeUTC => throw null;
#endif
	}
}