using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace SCPSL_ModPatch.CommandLine
{
    internal readonly record struct NativeFileIdentity
    {
        private readonly ulong _volumeSerialNumber;
        private readonly ulong _fileIdLow;
        private readonly ulong _fileIdHigh;

        private NativeFileIdentity(FileIdInformation information)
        {
            _volumeSerialNumber = information.VolumeSerialNumber;
            _fileIdLow = information.FileIdLow;
            _fileIdHigh = information.FileIdHigh;
        }

        internal static NativeFileIdentity? ReadIfPresent(string path)
        {
            // Follow reparse points, including file symlinks and directory junctions.
            // Backup semantics lets us inspect and reject directory destinations too.
            using SafeFileHandle handle = CreateFile(path, 0x80 /* FILE_READ_ATTRIBUTES */,
                FileShare.Read | FileShare.Write | FileShare.Delete, IntPtr.Zero,
                3 /* OPEN_EXISTING */, 0x02000000 /* FILE_FLAG_BACKUP_SEMANTICS */, IntPtr.Zero);
            if (handle.IsInvalid)
            {
                int error = Marshal.GetLastWin32Error();
                if (error is 2 or 3) // ERROR_FILE_NOT_FOUND / ERROR_PATH_NOT_FOUND
                    return null;
                throw InspectionFailure(path, error);
            }

            if (!GetFileAttributeTagInformation(handle, 9 /* FileAttributeTagInfo */, out FileAttributeTagInformation attributes,
                (uint)Marshal.SizeOf<FileAttributeTagInformation>()))
                throw InspectionFailure(path, Marshal.GetLastWin32Error());
            if ((attributes.FileAttributes & FileAttributes.Directory) != 0)
                return null;

            // Keep all 128 file-ID bits: truncation can collide on filesystems such as ReFS.
            if (!GetFileIdInformation(handle, 18 /* FileIdInfo */, out FileIdInformation identity,
                (uint)Marshal.SizeOf<FileIdInformation>()))
                throw InspectionFailure(path, Marshal.GetLastWin32Error());
            return new NativeFileIdentity(identity);
        }

        private static Win32Exception InspectionFailure(string path, int error) =>
            new(error, $"Could not read file identity for '{path}' (Win32 error {error}).");

        [StructLayout(LayoutKind.Sequential)]
        private struct FileAttributeTagInformation
        {
            public FileAttributes FileAttributes;
            public uint ReparseTag;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct FileIdInformation
        {
            public ulong VolumeSerialNumber;
            public ulong FileIdLow;
            public ulong FileIdHigh;
        }

        [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(string path, uint desiredAccess, FileShare shareMode,
            IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

        [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetFileAttributeTagInformation(SafeFileHandle handle, int informationClass,
            out FileAttributeTagInformation information, uint bufferSize);

        [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetFileIdInformation(SafeFileHandle handle, int informationClass,
            out FileIdInformation information, uint bufferSize);
    }
}
