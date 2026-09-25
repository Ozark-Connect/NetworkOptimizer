using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using NetworkOptimizer.Sqm.Models;
using Xunit;

namespace NetworkOptimizer.Sqm.Tests;

/// <summary>
/// Exercises only the generated managed-speedtest installer. Network clients and dpkg-deb are
/// stubbed; the .deb fixture is a local tarball that the dpkg-deb stub extracts into the expected
/// package layout. Archive creation and SHA-256 checks use local real tools.
/// </summary>
public sealed class ManagedSpeedtestInstallerTests
{
    private const string ExpectedVersionLine = "Speedtest by Ookla 1.2.0.84 (ea6b6773cf)";
    private const string DeploymentSentinel = "DEPLOYMENT_CONTINUED";

    [Fact]
    public void InstallManagedSpeedtest_PrimaryTarballActivatesVerifiedBinary()
    {
        if (OperatingSystem.IsWindows() || !File.Exists("/bin/bash"))
            return;

        using var fixture = InstallerFixture.Create();

        var result = fixture.Run(primaryStatus: "200");

        result.ExitCode.Should().Be(0, result.CombinedOutput);
        File.ReadAllBytes(fixture.ManagedBinaryPath).Should().Equal(fixture.BinaryBytes);
        result.CurlUrls.Should().ContainSingle().Which.Should().Contain("install.speedtest.net");
        result.CombinedOutput.Should().Contain(DeploymentSentinel);
        fixture.AssertNoStagingRemains();
    }

    [Theory]
    [InlineData("403", "aarch64", "arm64")]
    [InlineData("404", "x86_64", "amd64")]
    [InlineData("404", "aarch64", "arm64")]
    [InlineData("410", "armv7l", "armhf")]
    public void InstallManagedSpeedtest_UsesVerifiedPackagecloudPackageForMissingTarball(string status, string arch, string packageArch)
    {
        if (OperatingSystem.IsWindows() || !File.Exists("/bin/bash"))
            return;

        using var fixture = InstallerFixture.Create();

        var result = fixture.Run(primaryStatus: status, arch: arch);

        result.ExitCode.Should().Be(0, result.CombinedOutput);
        File.ReadAllBytes(fixture.ManagedBinaryPath).Should().Equal(fixture.BinaryBytes);
        result.CurlUrls.Should().HaveCount(2);
        result.CurlUrls[0].Should().Contain("install.speedtest.net");
        result.CurlUrls[1].Should().Contain("packagecloud.io/ookla/speedtest-cli/packages/debian/bookworm/");
        result.CurlUrls[1].Should().Contain($"_{packageArch}.deb/download.deb");
        File.Exists(fixture.DpkgMarkerPath).Should().BeTrue();
        result.CombinedOutput.Should().Contain(DeploymentSentinel);
        fixture.AssertNoStagingRemains();
    }

    [Theory]
    [InlineData("500")]
    [InlineData("000")]
    public void InstallManagedSpeedtest_NonNotFoundDownloadFailureDoesNotTryPackageFallback(string status)
    {
        if (OperatingSystem.IsWindows() || !File.Exists("/bin/bash"))
            return;

        using var fixture = InstallerFixture.Create();
        fixture.WriteOldManagedBinary();

        var result = fixture.Run(primaryStatus: status);

        result.ExitCode.Should().Be(0, result.CombinedOutput);
        result.CurlUrls.Should().ContainSingle();
        fixture.AssertOldBinaryPreserved();
        result.CombinedOutput.Should().Contain(DeploymentSentinel);
        fixture.AssertNoStagingRemains();
    }

    [Fact]
    public void InstallManagedSpeedtest_UnavailableFallbackPreservesExistingBinaryAndContinues()
    {
        if (OperatingSystem.IsWindows() || !File.Exists("/bin/bash"))
            return;

        using var fixture = InstallerFixture.Create();
        fixture.WriteOldManagedBinary();
        var result = fixture.Run(primaryStatus: "404", fallbackStatus: "404");

        result.ExitCode.Should().Be(0, result.CombinedOutput);
        result.CurlUrls.Should().HaveCount(2);
        fixture.AssertOldBinaryPreserved();
        File.Exists(fixture.DpkgMarkerPath).Should().BeFalse();
        result.CombinedOutput.Should().Contain(DeploymentSentinel);
        fixture.AssertNoStagingRemains();
    }

    [Fact]
    public void InstallManagedSpeedtest_BadPrimaryArchiveHashDoesNotTryPackageFallback()
    {
        if (OperatingSystem.IsWindows() || !File.Exists("/bin/bash"))
            return;

        using var fixture = InstallerFixture.Create();
        fixture.WriteOldManagedBinary();
        fixture.CorruptPrimaryArchive();

        var result = fixture.Run(primaryStatus: "200");

        result.ExitCode.Should().Be(0, result.CombinedOutput);
        result.CurlUrls.Should().ContainSingle();
        fixture.AssertOldBinaryPreserved();
        File.Exists(fixture.DpkgMarkerPath).Should().BeFalse();
        result.CombinedOutput.Should().Contain(DeploymentSentinel);
        fixture.AssertNoStagingRemains();
    }

    [Fact]
    public void InstallManagedSpeedtest_BadFallbackPackageHashDoesNotExtractOrActivate()
    {
        if (OperatingSystem.IsWindows() || !File.Exists("/bin/bash"))
            return;

        using var fixture = InstallerFixture.Create();
        fixture.WriteOldManagedBinary();

        var result = fixture.Run(primaryStatus: "404", badPackageHash: true);

        result.ExitCode.Should().Be(0, result.CombinedOutput);
        result.CurlUrls.Should().HaveCount(2);
        fixture.AssertOldBinaryPreserved();
        File.Exists(fixture.DpkgMarkerPath).Should().BeFalse();
        result.CombinedOutput.Should().Contain(DeploymentSentinel);
        fixture.AssertNoStagingRemains();
    }

    [Fact]
    public void InstallManagedSpeedtest_BadFallbackBinaryHashDoesNotActivate()
    {
        if (OperatingSystem.IsWindows() || !File.Exists("/bin/bash"))
            return;

        using var fixture = InstallerFixture.Create();
        fixture.WriteOldManagedBinary();

        var result = fixture.Run(primaryStatus: "404", badBinaryHash: true);

        result.ExitCode.Should().Be(0, result.CombinedOutput);
        File.Exists(fixture.DpkgMarkerPath).Should().BeTrue();
        fixture.AssertOldBinaryPreserved();
        result.CombinedOutput.Should().Contain(DeploymentSentinel);
        fixture.AssertNoStagingRemains();
    }

    [Fact]
    public void InstallManagedSpeedtest_ExtractionFailureIsBestEffortAndPreservesExistingBinary()
    {
        if (OperatingSystem.IsWindows() || !File.Exists("/bin/bash"))
            return;

        using var fixture = InstallerFixture.Create();
        fixture.WriteOldManagedBinary();

        var result = fixture.Run(primaryStatus: "404", failDpkg: true);

        result.ExitCode.Should().Be(0, result.CombinedOutput);
        File.Exists(fixture.DpkgMarkerPath).Should().BeTrue();
        fixture.AssertOldBinaryPreserved();
        result.CombinedOutput.Should().Contain(DeploymentSentinel);
        fixture.AssertNoStagingRemains();
    }

    [Fact]
    public void InstallManagedSpeedtest_AlreadyValidManagedBinarySkipsDownloads()
    {
        if (OperatingSystem.IsWindows() || !File.Exists("/bin/bash"))
            return;

        using var fixture = InstallerFixture.Create();
        Directory.CreateDirectory(Path.GetDirectoryName(fixture.ManagedBinaryPath)!);
        File.WriteAllBytes(fixture.ManagedBinaryPath, fixture.BinaryBytes);
        File.SetUnixFileMode(fixture.ManagedBinaryPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var result = fixture.Run(primaryStatus: "500");

        result.ExitCode.Should().Be(0, result.CombinedOutput);
        result.CurlUrls.Should().BeEmpty();
        File.ReadAllBytes(fixture.ManagedBinaryPath).Should().Equal(fixture.BinaryBytes);
        result.CombinedOutput.Should().Contain(DeploymentSentinel);
        fixture.AssertNoStagingRemains();
    }

    private sealed class InstallerFixture : IDisposable
    {
        private readonly string _root;
        private readonly string _stubDirectory;
        private readonly string _fixtureDirectory;
        private readonly string _scriptPath;
        private readonly string _primaryArchivePath;
        private readonly string _packageArchivePath;
        private readonly string _curlLogPath;
        private readonly string _logPath;

        private InstallerFixture(string root)
        {
            _root = root;
            _stubDirectory = Path.Combine(root, "bin");
            _fixtureDirectory = Path.Combine(root, "fixtures");
            _scriptPath = Path.Combine(root, "installer.sh");
            _primaryArchivePath = Path.Combine(_fixtureDirectory, "speedtest.tgz");
            _packageArchivePath = Path.Combine(_fixtureDirectory, "speedtest.deb");
            _curlLogPath = Path.Combine(root, "curl-urls.txt");
            _logPath = Path.Combine(root, "installer.log");
            Directory.CreateDirectory(_stubDirectory);
            Directory.CreateDirectory(_fixtureDirectory);

            BinaryBytes = Encoding.UTF8.GetBytes($"#!/bin/sh\nprintf '%s\\n' '{ExpectedVersionLine}'\n");
            BinarySha256 = Convert.ToHexString(SHA256.HashData(BinaryBytes)).ToLowerInvariant();
            var binarySource = Path.Combine(_fixtureDirectory, "speedtest");
            File.WriteAllBytes(binarySource, BinaryBytes);
            MakeExecutable(binarySource);

            EnsureSha256sum(_stubDirectory);
            CreateTarArchive(_primaryArchivePath, _fixtureDirectory, "speedtest");

            var packageRoot = Path.Combine(_fixtureDirectory, "package-root");
            var packageBinary = Path.Combine(packageRoot, "usr", "bin", "speedtest");
            Directory.CreateDirectory(Path.GetDirectoryName(packageBinary)!);
            File.Copy(binarySource, packageBinary);
            MakeExecutable(packageBinary);
            CreateTarArchive(_packageArchivePath, packageRoot, "usr");

            PackageSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(_packageArchivePath))).ToLowerInvariant();
            PrimarySha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(_primaryArchivePath))).ToLowerInvariant();
            WriteStubs();
            WriteInstaller();
        }

        public byte[] BinaryBytes { get; }
        public string BinarySha256 { get; }
        public string PackageSha256 { get; }
        public string PrimarySha256 { get; }
        public string ManagedBinaryPath => Path.Combine(_root, "data", "network-optimizer", "bin", "speedtest");
        public string DpkgMarkerPath => Path.Combine(_root, "dpkg-deb-called");

        public static InstallerFixture Create()
        {
            var root = Path.Combine(Path.GetTempPath(), $"managed-speedtest-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            return new InstallerFixture(root);
        }

        public InstallerResult Run(
            string primaryStatus,
            string fallbackStatus = "200",
            bool badPackageHash = false,
            bool badBinaryHash = false,
            bool failDpkg = false,
            string arch = "x86_64")
        {
            var generated = File.ReadAllText(_scriptPath);
            if (badPackageHash)
                generated = generated.Replace(PackageSha256, new string('0', 64), StringComparison.Ordinal);
            if (badBinaryHash)
                generated = generated.Replace(BinarySha256, new string('0', 64), StringComparison.Ordinal);
            File.WriteAllText(_scriptPath, generated);

            var psi = new ProcessStartInfo(BashPath(), _scriptPath)
            {
                WorkingDirectory = _root,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            var currentPath = Environment.GetEnvironmentVariable("PATH") ?? "";
            psi.Environment["PATH"] = _stubDirectory + Path.PathSeparator + currentPath;
            psi.Environment["STUB_ARCH"] = arch;
            psi.Environment["CURL_LOG"] = _curlLogPath;
            psi.Environment["PRIMARY_STATUS"] = primaryStatus;
            psi.Environment["FALLBACK_STATUS"] = fallbackStatus;
            psi.Environment["PRIMARY_ARCHIVE"] = _primaryArchivePath;
            psi.Environment["PACKAGE_ARCHIVE"] = _packageArchivePath;
            psi.Environment["DPKG_MARKER"] = DpkgMarkerPath;
            psi.Environment["DPKG_FAIL"] = failDpkg ? "1" : "0";

            using var process = Process.Start(psi)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(20_000))
            {
                process.Kill(entireProcessTree: true);
                throw new TimeoutException("Generated installer exceeded the 20 second test bound.");
            }

            return new InstallerResult(
                process.ExitCode,
                stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult(),
                File.Exists(_curlLogPath)
                    ? File.ReadAllLines(_curlLogPath).Where(line => line.Length > 0).ToArray()
                    : []);
        }

        public void WriteOldManagedBinary()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ManagedBinaryPath)!);
            File.WriteAllText(ManagedBinaryPath, "old-managed-speedtest\n");
        }

        public void CorruptPrimaryArchive()
        {
            File.WriteAllText(_primaryArchivePath, "not a gzip archive\n");
        }

        public void AssertOldBinaryPreserved() =>
            File.ReadAllText(ManagedBinaryPath).Should().Be("old-managed-speedtest\n");

        public void AssertNoStagingRemains()
        {
            var binDirectory = Path.GetDirectoryName(ManagedBinaryPath)!;
            if (Directory.Exists(binDirectory))
                Directory.GetDirectories(binDirectory, ".speedtest-install.*").Should().BeEmpty();
            File.Exists(ManagedBinaryPath + ".new").Should().BeFalse();
        }

        private void WriteInstaller()
        {
            var config = new SqmConfiguration
            {
                ConnectionType = ConnectionType.CellularHome,
                ConnectionName = "Installer Test",
                Interface = "eth6.100",
                NominalDownloadSpeed = 200,
                NominalUploadSpeed = 30,
                ShapeUpload = true,
                PingHost = "1.1.1.1",
            };
            config.ApplyProfileSettings(1000);
            var profile = config.GetProfile();
            var boot = new ScriptGenerator(config)
                .GenerateBootScript(profile.GetHourlyBaseline(1.0), profile.GetHourlyUploadBaseline(0.0))
                .Replace("/data/network-optimizer/bin/speedtest", ManagedBinaryPath, StringComparison.Ordinal)
                .Replace("/var/log/sqm-installer-test.log", _logPath, StringComparison.Ordinal)
                .Replace(ScriptGenerator.ManagedSpeedtestAarch64ArchiveSha256, PrimarySha256, StringComparison.Ordinal)
                .Replace(ScriptGenerator.ManagedSpeedtestAarch64DebSha256, PackageSha256, StringComparison.Ordinal)
                .Replace(ScriptGenerator.ManagedSpeedtestAarch64BinarySha256, BinarySha256, StringComparison.Ordinal)
                .Replace(ScriptGenerator.ManagedSpeedtestArmhfArchiveSha256, PrimarySha256, StringComparison.Ordinal)
                .Replace(ScriptGenerator.ManagedSpeedtestArmhfDebSha256, PackageSha256, StringComparison.Ordinal)
                .Replace(ScriptGenerator.ManagedSpeedtestArmhfBinarySha256, BinarySha256, StringComparison.Ordinal)
                .Replace(ScriptGenerator.ManagedSpeedtestX86_64ArchiveSha256, PrimarySha256, StringComparison.Ordinal)
                .Replace(ScriptGenerator.ManagedSpeedtestX86_64DebSha256, PackageSha256, StringComparison.Ordinal)
                .Replace(ScriptGenerator.ManagedSpeedtestX86_64BinarySha256, BinarySha256, StringComparison.Ordinal)
                .Replace("\r\n", "\n", StringComparison.Ordinal);

            var sectionTwo = boot.IndexOf("# Section 2: Create Directories", StringComparison.Ordinal);
            sectionTwo.Should().BeGreaterThan(0, "the generated script must have a section 2 boundary");
            var isolatedInstaller = boot[..sectionTwo] + $"\necho '{DeploymentSentinel}'\n";
            File.WriteAllText(_scriptPath, isolatedInstaller);
        }

        private void WriteStubs()
        {
            WriteStub("uname", "#!/bin/sh\nprintf '%s\\n' \"$STUB_ARCH\"\n");
            WriteStub("which", "#!/bin/sh\nexit 0\n");
            WriteStub("apt-get", "#!/bin/sh\necho apt-get-must-not-run >&2\nexit 91\n");
            WriteStub("curl", """
                #!/bin/sh
                set -eu
                url=
                output=
                write_out=0
                while [ "$#" -gt 0 ]; do
                    case "$1" in
                        --write-out) write_out=1; shift; [ "$#" -gt 0 ] && shift ;;
                        -o) shift; output=$1; shift ;;
                        --*) shift ;;
                        *) url=$1; shift ;;
                    esac
                done
                printf '%s\n' "$url" >> "$CURL_LOG"
                case "$url" in
                    *packagecloud.io*) status=$FALLBACK_STATUS; fixture=$PACKAGE_ARCHIVE ;;
                    *) status=$PRIMARY_STATUS; fixture=$PRIMARY_ARCHIVE ;;
                esac
                if [ "$status" = 200 ]; then
                    cp "$fixture" "$output"
                    if [ "$write_out" = 1 ]; then printf '%s' "$status"; fi
                    exit 0
                fi
                if [ "$write_out" = 1 ]; then printf '%s' "$status"; fi
                exit 22
                """);
            WriteStub("dpkg-deb", """
                #!/bin/sh
                set -eu
                : > "$DPKG_MARKER"
                [ "$DPKG_FAIL" = 0 ] || exit 1
                [ "$1" = --extract ]
                mkdir -p "$3"
                tar -xzf "$2" -C "$3"
                """);
        }

        private void WriteStub(string name, string contents)
        {
            var path = Path.Combine(_stubDirectory, name);
            File.WriteAllText(path, contents.Replace("\r\n", "\n", StringComparison.Ordinal));
            MakeExecutable(path);
        }

        private static string EnsureSha256sum(string stubDirectory)
        {
            var existing = FindOnPath("sha256sum");
            if (existing != null) return existing;

            var shasum = FindOnPath("shasum");
            shasum.Should().NotBeNull("a real SHA-256 implementation is needed for the generated checksum checks");
            var shim = Path.Combine(stubDirectory, "sha256sum");
            File.WriteAllText(shim, $"#!/bin/sh\nexec '{shasum}' -a 256 \"$@\"\n");
            MakeExecutable(shim);
            return shim;
        }

        private static string? FindOnPath(string command)
        {
            foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            {
                var candidate = Path.Combine(directory, command);
                if (File.Exists(candidate)) return candidate;
            }
            return null;
        }

        private static void CreateTarArchive(string output, string directory, string entry)
        {
            var psi = new ProcessStartInfo(FindOnPath("tar") ?? "tar")
            {
                WorkingDirectory = directory,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-czf");
            psi.ArgumentList.Add(output);
            psi.ArgumentList.Add("-C");
            psi.ArgumentList.Add(directory);
            psi.ArgumentList.Add(entry);
            using var process = Process.Start(psi)!;
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit(10_000).Should().BeTrue("local fixture archive creation is bounded");
            process.ExitCode.Should().Be(0, stderr);
        }

        private static void MakeExecutable(string path)
        {
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        private static string BashPath() =>
            FindOnPath("bash") ?? throw new InvalidOperationException("bash is required for managed installer behavior tests.");

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private sealed record InstallerResult(int ExitCode, string CombinedOutput, IReadOnlyList<string> CurlUrls);
}
