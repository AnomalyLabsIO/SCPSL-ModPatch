using System.IO.Compression;
using System.Security.Cryptography;

namespace SCPSL_ModPatch
{
    internal sealed record UnlicenseRelease(string DownloadUrl, string ArchiveSha256, long ArchiveLength,
        string ExecutableSha256, long ExecutableLength);

    internal sealed class UnlicenseDownloader
    {
        internal static readonly UnlicenseRelease Release = new(
            "https://github.com/ergrelet/unlicense/releases/download/0.4.0/unlicense-py3.11-x64.zip",
            "09D2B526D7A9F76DC11546B3AF85E67CD187108F060AF6286D7A533831949D16", 49_048_093,
            "33C27D4DEAAF54F832849D71CE65CE568EB2CA2BB1F24C21F9CF9F0DDE7AF955", 49_496_867);

        private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromMinutes(10) };
        private readonly string _installationDirectory;
        private readonly HttpClient _client;
        private readonly UnlicenseRelease _release;

        public UnlicenseDownloader() : this(AppContext.BaseDirectory, Client, Release) { }

        internal UnlicenseDownloader(string installationDirectory, HttpClient client, UnlicenseRelease release)
        {
            _installationDirectory = Path.GetFullPath(installationDirectory);
            _client = client;
            _release = release;
        }

        internal string ExecutablePath => Path.Combine(_installationDirectory, "unlicense.exe");

        internal string? FindExistingExecutable(string configuredPath)
        {
            // Explicitly supplied tools remain usable, including older bundled copies.
            if (File.Exists(configuredPath))
                return Path.GetFullPath(configuredPath);
            return IsInstalledExecutableValid() ? ExecutablePath : null;
        }

        private bool IsInstalledExecutableValid()
        {
            if (!File.Exists(ExecutablePath))
                return false;
            using FileStream file = File.OpenRead(ExecutablePath);
            using SHA256 hash = SHA256.Create();
            return file.Length == _release.ExecutableLength &&
                Convert.ToHexString(hash.ComputeHash(file)) == _release.ExecutableSha256;
        }

        internal async Task<string> DownloadAsync(IProgress<int>? progress = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsInstalledExecutableValid())
                return ExecutablePath;

            Directory.CreateDirectory(_installationDirectory);
            string stagingDirectory = Path.Combine(_installationDirectory, $".download-{Guid.NewGuid():N}");
            Directory.CreateDirectory(stagingDirectory);
            try
            {
                string archivePath = Path.Combine(stagingDirectory, "unlicense.zip");
                using (HttpResponseMessage response = await _client.GetAsync(_release.DownloadUrl,
                    HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
                {
                    response.EnsureSuccessStatusCode();
                    if (response.Content.Headers.ContentLength is long length && length != _release.ArchiveLength)
                        throw new InvalidDataException("The Unlicense download does not match the pinned archive size.");
                    using Stream source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                    await CopyAndVerifyAsync(source, archivePath, _release.ArchiveLength, _release.ArchiveSha256,
                        progress, 0, 90, cancellationToken).ConfigureAwait(false);
                }

                string executablePath = Path.Combine(stagingDirectory, "unlicense.exe");
                using (ZipArchive archive = ZipFile.OpenRead(archivePath))
                {
                    // The pinned upstream ZIP contains only this file. Never extract arbitrary paths.
                    if (archive.Entries.Count != 1 || archive.Entries[0].FullName != "unlicense.exe" ||
                        archive.Entries[0].Length != _release.ExecutableLength)
                        throw new InvalidDataException("The Unlicense archive has an unexpected layout or executable size.");
                    using Stream source = archive.Entries[0].Open();
                    await CopyAndVerifyAsync(source, executablePath, _release.ExecutableLength, _release.ExecutableSha256,
                        progress, 90, 99, cancellationToken).ConfigureAwait(false);
                }

                cancellationToken.ThrowIfCancellationRequested();
                File.Move(executablePath, ExecutablePath, overwrite: true);
                progress?.Report(100);
                return ExecutablePath;
            }
            finally
            {
                try { Directory.Delete(stagingDirectory, recursive: true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        private static async Task CopyAndVerifyAsync(Stream source, string destination, long expectedLength,
            string expectedHash, IProgress<int>? progress, int progressFrom, int progressTo, CancellationToken cancellationToken)
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                81920, useAsync: true);
            byte[] buffer = new byte[81920];
            long total = 0;
            int read;
            while ((read = await source.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
            {
                total += read;
                if (total > expectedLength)
                    throw new InvalidDataException("Unlicense data exceeds its pinned size.");
                hash.AppendData(buffer, 0, read);
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                progress?.Report(progressFrom + (int)(total * (progressTo - progressFrom) / expectedLength));
            }
            if (total != expectedLength || Convert.ToHexString(hash.GetHashAndReset()) != expectedHash)
                throw new InvalidDataException("Unlicense data failed verification against its pinned SHA-256 hash and size.");
        }
    }
}
