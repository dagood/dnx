using Microsoft.Framework.Internal;
using NuGet;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Microsoft.Dnx.Runtime.Sources.Impl
{
    internal struct RuntimeInfo : IEquatable<RuntimeInfo>
    {
        public static readonly RuntimeInfo Empty = new RuntimeInfo();

        /// <summary>
        /// Gets the full name of the runtime.
        /// </summary>
        public string Name { get; }

        /// <summary>
        /// Gets the type of CLR that the DNX runtime targets.
        /// </summary>
        public ClrType ClrType { get; }

        /// <summary>
        /// Gets the type of Operating System that the DNX runtime targets.
        /// </summary>
        public RuntimeOperatingSystem OperatingSystem { get; }

        /// <summary>
        /// Gets the version of Operating System that the DNX runtime targets.
        /// </summary>
        // This is a string because we should not assume that OS versions will be System.Version-compatible or even SemVer-compatible
        public string OperatingSystemVersion { get; }

        /// <summary>
        /// Gets the architecture that the DNX runtime targets.
        /// </summary>
        public Architecture Architecture { get; }

        /// <summary>
        /// Gets the version of the runtime
        /// </summary>
        public SemanticVersion Version { get; }

        public bool IsEmpty
        {
            get { return Equals(Empty); }
        }

        public RuntimeInfo(string name, ClrType clrType, RuntimeOperatingSystem operatingSystem, string operatingSystemVersion, Architecture architecture, SemanticVersion version)
        {
            Name = name;
            ClrType = clrType;
            OperatingSystem = operatingSystem;
            OperatingSystemVersion = operatingSystemVersion;
            Architecture = architecture;
            Version = version;
        }

        public bool Equals(RuntimeInfo other)
        {
            return ClrType == other.ClrType &&
                OperatingSystem == other.OperatingSystem &&
                Architecture == other.Architecture &&
                Version.Equals(other.Version) &&
                string.Equals(Name, other.Name, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(OperatingSystemVersion, other.OperatingSystemVersion, StringComparison.OrdinalIgnoreCase);
        }


        public override bool Equals(object obj)
        {
            return obj != null && Equals((RuntimeInfo)obj);
        }

        public override int GetHashCode()
        {
            return HashCodeCombiner.Start()
                .Add(Name)
                .Add(ClrType)
                .Add(OperatingSystem)
                .Add(OperatingSystemVersion)
                .Add(Architecture)
                .Add(Version)
                .CombinedHash;
        }

        public static RuntimeInfo GetFromName(string runtimeName)
        {
            var parts = runtimeName.Split(new[] { '.' }, 2);
            if (parts.Length != 2)
            {
                return Empty;
            }
            var version = parts[1];
            parts = parts[0].Split(new[] { '-' }, 4);

            if (!string.Equals(parts[0], Constants.RuntimeShortName, StringComparison.Ordinal))
            {
                return Empty;
            }

            string clrTypeString;
            string operatingSystemString;
            string architectureString;
            if (parts.Length == 2) // dnx-mono
            {
                clrTypeString = parts[1];
                operatingSystemString = string.Empty;
                architectureString = string.Empty;
            }
            else if (parts.Length == 4) // dnx-[clr]-[os]-[arch]
            {
                clrTypeString = parts[1];
                operatingSystemString = parts[2];
                architectureString = parts[3];
            }
            else
            {
                // Invalid
                return Empty;
            }

            return new RuntimeInfo(
                runtimeName,
                ParseClrType(clrTypeString),
                ParseOsType(operatingSystemString),
                ParseOsVersion(operatingSystemString),
                ParseArch(architectureString),
                SemanticVersion.Parse(version));
        }

        private static Architecture ParseArch(string architectureString)
        {
            if (string.IsNullOrEmpty(architectureString))
            {
                return Architecture.Any;
            }
            else if (string.Equals(architectureString, "x86", StringComparison.OrdinalIgnoreCase))
            {
                return Architecture.X86;
            }
            else if (string.Equals(architectureString, "x64", StringComparison.OrdinalIgnoreCase))
            {
                return Architecture.X64;
            }
            else if (string.Equals(architectureString, "arm", StringComparison.OrdinalIgnoreCase))
            {
                return Architecture.Arm;
            }
            else if (string.Equals(architectureString, "arm64", StringComparison.OrdinalIgnoreCase))
            {
                return Architecture.Arm64;
            }
            return Architecture.Unknown;
        }

        private static RuntimeOperatingSystem ParseOsType(string operatingSystemString)
        {
            if (string.IsNullOrEmpty(operatingSystemString))
            {
                return RuntimeOperatingSystem.Unspecified;
            }

            // Read to the first digit
            string osName = new string(operatingSystemString.TakeWhile(c => !char.IsDigit(c)).ToArray());
            if (string.IsNullOrEmpty(osName))
            {
                return RuntimeOperatingSystem.Unspecified;
            }
            else if (string.Equals(osName, "win", StringComparison.OrdinalIgnoreCase))
            {
                return RuntimeOperatingSystem.Windows;
            }
            else if (string.Equals(osName, "linux", StringComparison.OrdinalIgnoreCase))
            {
                return RuntimeOperatingSystem.Linux;
            }
            else if (string.Equals(osName, "debian", StringComparison.OrdinalIgnoreCase))
            {
                return RuntimeOperatingSystem.Debian;
            }
            else if (string.Equals(osName, "centos", StringComparison.OrdinalIgnoreCase))
            {
                return RuntimeOperatingSystem.CentOS;
            }
            else if (string.Equals(osName, "darwin", StringComparison.OrdinalIgnoreCase))
            {
                return RuntimeOperatingSystem.Darwin;
            }
            return RuntimeOperatingSystem.Unknown;
        }

        private static string ParseOsVersion(string operatingSystemString)
        {
            var chars = operatingSystemString?.SkipWhile(c => !char.IsDigit(c))?.ToArray();
            return chars == null ? string.Empty : new string(chars);
        }

        private static ClrType ParseClrType(string clrTypeString)
        {
            if (string.IsNullOrEmpty(clrTypeString))
            {
                return ClrType.Unspecified;
            }
            else if (string.Equals(clrTypeString, "clr", StringComparison.OrdinalIgnoreCase))
            {
                return ClrType.DesktopClr;
            }
            else if (string.Equals(clrTypeString, "coreclr", StringComparison.OrdinalIgnoreCase))
            {
                return ClrType.CoreClr;
            }
            else if (string.Equals(clrTypeString, "mono", StringComparison.OrdinalIgnoreCase))
            {
                return ClrType.Mono;
            }
            return ClrType.Unknown;
        }
    }

    /// <summary>
    /// Indicates a CLR type that a DNX runtime can target.
    /// </summary>
    internal enum ClrType
    {
        /// <summary>
        /// Indicates that the runtime does not specify a CLR type
        /// </summary>
        Unspecified = 0,

        /// <summary>
        /// Indicates that the runtime specifies an unknown CLR type
        /// </summary>
        Unknown,

        /// <summary>
        /// Indicates the full Desktop CLR, that runs on Windows only.
        /// </summary>
        DesktopClr,

        /// <summary>
        /// Indicates the cross-platform Core CLR.
        /// </summary>
        CoreClr,

        /// <summary>
        /// Indicates the Mono open-source CLR implementation.
        /// </summary>
        Mono
    }

    /// <summary>
    /// Indicates an operating system that a DNX runtime can target.
    /// </summary>
    internal enum RuntimeOperatingSystem
    {
        /// <summary>
        /// Indicates that the runtime does not specify an Operating System
        /// </summary>
        Unspecified = 0,

        /// <summary>
        /// Indicates that the runtime specifies an unknown Operating System
        /// </summary>
        Unknown,

        /// <summary>
        /// Indicates any Windows Operating System.
        /// </summary>
        Windows,

        /// <summary>
        /// Indicates an general Linux-based Operating System.
        /// </summary>
        Linux,

        /// <summary>
        /// Indicates a Debian-based Linux Operating System.
        /// </summary>
        Debian,

        /// <summary>
        /// Indicates a CentOS-based Linux Operating System.
        /// </summary>
        CentOS,

        /// <summary>
        /// Indicates a Mac OS X (Darwin) Operating System.
        /// </summary>
        Darwin
    }

    /// <summary>
    /// Indicates an architecture that a DNX runtime can target.
    /// </summary>
    internal enum Architecture
    {
        /// <summary>
        /// Indicates that the runtime does not specify an architecture
        /// </summary>
        Unspecified = 0,

        /// <summary>
        /// Indicates that the runtime specifies an unknown architecture
        /// </summary>
        Unknown,

        /// <summary>
        /// Indicates that the DNX is architecture independent.
        /// </summary>
        Any,

        /// <summary>
        /// Indicates the 32-bit x86 Architecture
        /// </summary>
        X86,

        /// <summary>
        /// Indicates the 64-bit x64 Architecture
        /// </summary>
        X64,

        /// <summary>
        /// Indicates the 32-bit ARM Architecture (AArch32)
        /// </summary>
        Arm,

        /// <summary>
        /// Indicates the 64-bit ARM Architecture (AArch64)
        /// </summary>
        Arm64
    }
}
