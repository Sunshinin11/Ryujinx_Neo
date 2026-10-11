using Ryujinx.Common;
using System;
using System.Diagnostics.CodeAnalysis;

namespace Ryujinx.Memory
{
    [SuppressMessage("Interoperability", "CA1416:Validate platform compatibility")]
    public static class MemoryManagement
    {
        public static nint Allocate(ulong size, bool forJit)
        {
            if (OperatingSystem.IsWindows())
            {
                return MemoryManagementWindows.Allocate((nint)size);
            }
            else if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() || PlatformInfo.IsBionic)
            {
                return MemoryManagementUnix.Allocate(size, forJit);
            }
            else
            {
                throw new PlatformNotSupportedException();
            }
        }

        public static nint Reserve(ulong size, bool forJit, bool viewCompatible)
        {
            if (OperatingSystem.IsWindows())
            {
                return MemoryManagementWindows.Reserve((nint)size, viewCompatible);
            }
            else if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() || PlatformInfo.IsBionic)
            {
                return MemoryManagementUnix.Reserve(size, forJit);
            }
            else
            {
                throw new PlatformNotSupportedException();
            }
        }

        /// <summary>
        /// NEOV2 NCE-LOWBASE1: reserves an exact Unix host range.
        /// </summary>
        public static nint ReserveAt(nint address, ulong size)
        {
            if (OperatingSystem.IsLinux() ||
                OperatingSystem.IsMacOS() ||
                PlatformInfo.IsBionic)
            {
                return MemoryManagementUnix.ReserveAt(address, size);
            }

            return nint.Zero;
        }

        public static void Commit(nint address, ulong size, bool forJit)
        {
            if (OperatingSystem.IsWindows())
            {
                MemoryManagementWindows.Commit(address, (nint)size);
            }
            else if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() || PlatformInfo.IsBionic)
            {
                MemoryManagementUnix.Commit(address, size, forJit);
            }
            else
            {
                throw new PlatformNotSupportedException();
            }
        }

        public static void Decommit(nint address, ulong size)
        {
            if (OperatingSystem.IsWindows())
            {
                MemoryManagementWindows.Decommit(address, (nint)size);
            }
            else if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() || PlatformInfo.IsBionic)
            {
                MemoryManagementUnix.Decommit(address, size);
            }
            else
            {
                throw new PlatformNotSupportedException();
            }
        }

        public static void MapView(nint sharedMemory, ulong srcOffset, nint address, ulong size, MemoryBlock owner)
        {
            if (OperatingSystem.IsWindows())
            {
                MemoryManagementWindows.MapView(sharedMemory, srcOffset, address, (nint)size, owner);
            }
            else if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() || PlatformInfo.IsBionic)
            {
                MemoryManagementUnix.MapView(sharedMemory, srcOffset, address, size);
            }
            else
            {
                throw new PlatformNotSupportedException();
            }
        }

        public static void UnmapView(nint sharedMemory, nint address, ulong size, MemoryBlock owner)
        {
            if (OperatingSystem.IsWindows())
            {
                MemoryManagementWindows.UnmapView(sharedMemory, address, (nint)size, owner);
            }
            else if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() || PlatformInfo.IsBionic)
            {
                MemoryManagementUnix.UnmapView(address, size);
            }
            else
            {
                throw new PlatformNotSupportedException();
            }
        }

        public static void Reprotect(nint address, ulong size, MemoryPermission permission, bool forView, bool throwOnFail)
        {
            bool result;

            if (OperatingSystem.IsWindows())
            {
                result = MemoryManagementWindows.Reprotect(address, (nint)size, permission, forView);
            }
            else if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() || PlatformInfo.IsBionic)
            {
                result = MemoryManagementUnix.Reprotect(address, size, permission);
            }
            else
            {
                throw new PlatformNotSupportedException();
            }

            if (!result && throwOnFail)
            {
                throw new MemoryProtectionException(permission);
            }
        }

        public static bool Free(nint address, ulong size)
        {
            if (OperatingSystem.IsWindows())
            {
                return MemoryManagementWindows.Free(address, (nint)size);
            }
            else if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() || PlatformInfo.IsBionic)
            {
                return MemoryManagementUnix.Free(address);
            }
            else
            {
                throw new PlatformNotSupportedException();
            }
        }

        public static nint CreateSharedMemory(ulong size, bool reserve)
        {
            if (OperatingSystem.IsWindows())
            {
                return MemoryManagementWindows.CreateSharedMemory((nint)size, reserve);
            }
            else if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() || PlatformInfo.IsBionic)
            {
                return MemoryManagementUnix.CreateSharedMemory(size, reserve);
            }
            else
            {
                throw new PlatformNotSupportedException();
            }
        }

        public static void DestroySharedMemory(nint handle)
        {
            if (OperatingSystem.IsWindows())
            {
                MemoryManagementWindows.DestroySharedMemory(handle);
            }
            else if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() || PlatformInfo.IsBionic)
            {
                MemoryManagementUnix.DestroySharedMemory(handle);
            }
            else
            {
                throw new PlatformNotSupportedException();
            }
        }

        public static nint MapSharedMemory(nint handle, ulong size)
        {
            if (OperatingSystem.IsWindows())
            {
                return MemoryManagementWindows.MapSharedMemory(handle);
            }
            else if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() || PlatformInfo.IsBionic)
            {
                return MemoryManagementUnix.MapSharedMemory(handle, size);
            }
            else
            {
                throw new PlatformNotSupportedException();
            }
        }

        public static void UnmapSharedMemory(nint address, ulong size)
        {
            if (OperatingSystem.IsWindows())
            {
                MemoryManagementWindows.UnmapSharedMemory(address);
            }
            else if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() || PlatformInfo.IsBionic)
            {
                MemoryManagementUnix.UnmapSharedMemory(address, size);
            }
            else
            {
                throw new PlatformNotSupportedException();
            }
        }
    }
}
