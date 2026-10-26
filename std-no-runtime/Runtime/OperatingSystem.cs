// Ported from dotnet/runtime release/8.0 (MIT):
//   src/libraries/System.Private.CoreLib/src/System/OperatingSystem.cs
// The static platform checks, verbatim: no TARGET_* constant is defined here, so every
// Is<Platform>() is false, as upstream's #else branches say; IsOSPlatform answers to
// "SHARPOS". Cuts (marked "SharpOS"): the instance part (no Version / PlatformID),
// IsOSVersionAtLeast (no Environment.OSVersion), [NonVersionable] (12x) and
// [SupportedOSPlatformGuard] (2x) attributes.

// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace System
{
    public sealed class OperatingSystem // SharpOS cut: ISerializable, ICloneable (instance part cut)
    {
        private const string OSPlatformName =
#if TARGET_BROWSER
        "BROWSER"
#elif TARGET_WASI
        "WASI"
#elif TARGET_WINDOWS
        "WINDOWS"
#elif TARGET_OSX
        "OSX"
#elif TARGET_MACCATALYST
        "MACCATALYST"
#elif TARGET_IOS
        "IOS"
#elif TARGET_TVOS
        "TVOS"
#elif TARGET_ANDROID
        "ANDROID"
#elif TARGET_LINUX
        "LINUX"
#elif TARGET_FREEBSD
        "FREEBSD"
#elif TARGET_NETBSD
        "NETBSD"
#elif TARGET_ILLUMOS
        "ILLUMOS"
#elif TARGET_SOLARIS
        "SOLARIS"
#else
        "SHARPOS" // SharpOS: was #error Unknown OS — no TARGET_* constant is defined for this OS
#endif
        ;

        // SharpOS cut: the instance part (ctor, Platform, ServicePack, Version, VersionString, Clone,
        // GetObjectData) — std has no System.Version / PlatformID / ISerializable, and no
        // Environment.OSVersion that would hand one out.

        /// <summary>
        /// Indicates whether the current application is running on the specified platform.
        /// </summary>
        /// <param name="platform">Case-insensitive platform name. Examples: Browser, Linux, FreeBSD, Android, iOS, macOS, tvOS, watchOS, Windows.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsOSPlatform(string platform)
        {
            ArgumentNullException.ThrowIfNull(platform);

            return platform.Equals(OSPlatformName, StringComparison.OrdinalIgnoreCase)
#if TARGET_OSX
            || platform.Equals("MACOS", StringComparison.OrdinalIgnoreCase)
#elif TARGET_MACCATALYST
            || platform.Equals("IOS", StringComparison.OrdinalIgnoreCase)
#endif
            ;
        }

        /// <summary>
        /// Check for the OS with a >= version comparison. Used to guard APIs that were added in the given OS release.
        /// </summary>
        /// <param name="platform">Case-insensitive platform name. Examples: Browser, Linux, FreeBSD, Android, iOS, macOS, tvOS, watchOS, Windows.</param>
        /// <param name="major">Major OS version number.</param>
        /// <param name="minor">Minor OS version number (optional).</param>
        /// <param name="build">Build OS version number (optional).</param>
        /// <param name="revision">Revision OS version number (optional).</param>
        public static bool IsOSPlatformVersionAtLeast(string platform, int major, int minor = 0, int build = 0, int revision = 0)
            => IsOSPlatform(platform) && IsOSVersionAtLeast(major, minor, build, revision);

        /// <summary>
        /// Indicates whether the current application is running as WASM in a Browser.
        /// </summary>
        public static bool IsBrowser() =>
#if TARGET_BROWSER
            true;
#else
            false;
#endif

        /// <summary>
        /// Indicates whether the current application is running as WASI.
        /// </summary>
        public static bool IsWasi() =>
#if TARGET_WASI
            true;
#else
            false;
#endif

        /// <summary>
        /// Indicates whether the current application is running on Linux.
        /// </summary>
        public static bool IsLinux() =>
#if TARGET_LINUX && !TARGET_ANDROID
            true;
#else
            false;
#endif

        /// <summary>
        /// Indicates whether the current application is running on FreeBSD.
        /// </summary>
        public static bool IsFreeBSD() =>
#if TARGET_FREEBSD
            true;
#else
            false;
#endif

        /// <summary>
        /// Check for the FreeBSD version (returned by 'uname') with a >= version comparison. Used to guard APIs that were added in the given FreeBSD release.
        /// </summary>
        public static bool IsFreeBSDVersionAtLeast(int major, int minor = 0, int build = 0, int revision = 0)
            => IsFreeBSD() && IsOSVersionAtLeast(major, minor, build, revision);

        /// <summary>
        /// Indicates whether the current application is running on Android.
        /// </summary>
        public static bool IsAndroid() =>
#if TARGET_ANDROID
            true;
#else
            false;
#endif

        /// <summary>
        /// Check for the Android API level (returned by 'ro.build.version.sdk') with a >= version comparison. Used to guard APIs that were added in the given Android release.
        /// </summary>
        public static bool IsAndroidVersionAtLeast(int major, int minor = 0, int build = 0, int revision = 0)
            => IsAndroid() && IsOSVersionAtLeast(major, minor, build, revision);

        /// <summary>
        /// Indicates whether the current application is running on iOS or MacCatalyst.
        /// </summary>
        public static bool IsIOS() =>
#if TARGET_IOS || TARGET_MACCATALYST
            true;
#else
            false;
#endif

        /// <summary>
        /// Check for the iOS/MacCatalyst version (returned by 'libobjc.get_operatingSystemVersion') with a >= version comparison. Used to guard APIs that were added in the given iOS release.
        /// </summary>
        public static bool IsIOSVersionAtLeast(int major, int minor = 0, int build = 0)
            => IsIOS() && IsOSVersionAtLeast(major, minor, build, 0);

        /// <summary>
        /// Indicates whether the current application is running on macOS.
        /// </summary>
        public static bool IsMacOS() =>
#if TARGET_OSX
            true;
#else
            false;
#endif

        internal static bool IsOSXLike() =>
#if TARGET_OSX || TARGET_MACCATALYST || TARGET_IOS || TARGET_TVOS
            true;
#else
            false;
#endif

        /// <summary>
        /// Check for the macOS version (returned by 'libobjc.get_operatingSystemVersion') with a >= version comparison. Used to guard APIs that were added in the given macOS release.
        /// </summary>
        public static bool IsMacOSVersionAtLeast(int major, int minor = 0, int build = 0)
            => IsMacOS() && IsOSVersionAtLeast(major, minor, build, 0);

        /// <summary>
        /// Indicates whether the current application is running on Mac Catalyst.
        /// </summary>
        public static bool IsMacCatalyst() =>
#if TARGET_MACCATALYST
            true;
#else
            false;
#endif

        /// <summary>
        /// Check for the Mac Catalyst version (iOS version as presented in Apple documentation) with a >= version comparison. Used to guard APIs that were added in the given Mac Catalyst release.
        /// </summary>
        public static bool IsMacCatalystVersionAtLeast(int major, int minor = 0, int build = 0)
            => IsMacCatalyst() && IsOSVersionAtLeast(major, minor, build, 0);

        /// <summary>
        /// Indicates whether the current application is running on tvOS.
        /// </summary>
        public static bool IsTvOS() =>
#if TARGET_TVOS
            true;
#else
            false;
#endif

        /// <summary>
        /// Check for the tvOS version (returned by 'libobjc.get_operatingSystemVersion') with a >= version comparison. Used to guard APIs that were added in the given tvOS release.
        /// </summary>
        public static bool IsTvOSVersionAtLeast(int major, int minor = 0, int build = 0)
            => IsTvOS() && IsOSVersionAtLeast(major, minor, build, 0);

        /// <summary>
        /// Indicates whether the current application is running on watchOS.
        /// </summary>
        public static bool IsWatchOS() =>
#if TARGET_WATCHOS
            true;
#else
            false;
#endif

        /// <summary>
        /// Check for the watchOS version (returned by 'libobjc.get_operatingSystemVersion') with a >= version comparison. Used to guard APIs that were added in the given watchOS release.
        /// </summary>
        public static bool IsWatchOSVersionAtLeast(int major, int minor = 0, int build = 0)
            => IsWatchOS() && IsOSVersionAtLeast(major, minor, build, 0);

        /// <summary>
        /// Indicates whether the current application is running on Windows.
        /// </summary>
        public static bool IsWindows() =>
#if TARGET_WINDOWS
            true;
#else
            false;
#endif

        /// <summary>
        /// Check for the Windows version (returned by 'RtlGetVersion') with a >= version comparison. Used to guard APIs that were added in the given Windows release.
        /// </summary>
        public static bool IsWindowsVersionAtLeast(int major, int minor = 0, int build = 0, int revision = 0)
            => IsWindows() && IsOSVersionAtLeast(major, minor, build, revision);

        private static bool IsOSVersionAtLeast(int major, int minor, int build, int revision)
        {
            // SharpOS cut: upstream compares with Environment.OSVersion.Version; std reports no OS
            // version, so no version check passes. Every caller first asks Is<Platform>(), which is
            // false here for every named platform.
            return false;
        }
    }
}
