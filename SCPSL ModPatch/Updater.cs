using SCPSL_ModPatch.PatchUtils;
using Newtonsoft.Json;

namespace SCPSL_ModPatch
{
    internal class Updater
    {
        const string PATCHINFO_LINK = "https://raw.githubusercontent.com/AnomalyLabsIO/SCPSL-ModPatch/refs/heads/master/Shared_Files/patchinfo.json";
        static readonly HttpClient _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

        public static Task<PatchInfo> UpdatePatchInfoAsync(string patchInfoPath)
        {
            return UpdatePatchInfoAsync(patchInfoPath, _httpClient);
        }

        internal static async Task<PatchInfo> UpdatePatchInfoAsync(string patchInfoPath, HttpClient httpClient)
        {
            string json = await httpClient.GetStringAsync(PATCHINFO_LINK).ConfigureAwait(false);
            PatchInfo? patchInfo = JsonConvert.DeserializeObject<PatchInfo>(json);
            if (patchInfo == null || patchInfo.versionRanges == null || patchInfo.versionRanges.Length == 0)
                throw new InvalidDataException("Downloaded patch info must contain at least one version range.");
            PatchTargetResolver.Validate(patchInfo);

            string fullPath = Path.GetFullPath(patchInfoPath);
            string temporaryPath = Path.Combine(Path.GetDirectoryName(fullPath)!, $".patchinfo-{Guid.NewGuid():N}.tmp");
            try
            {
                await File.WriteAllTextAsync(temporaryPath, json).ConfigureAwait(false);
                File.Move(temporaryPath, fullPath, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }
            return patchInfo;
        }
    }
}
