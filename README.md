# SCPSL-ModPatch

A program that lets you disable central authentication and external game anti-cheat to modify the game freely. Note that servers with enabled `online_mode` in the config will not work.

## Supported game versions

| Game version | Release | Supported | Release period |
|:-------------|:--------|:---------:|:---------------|
| `15.x.x` | Megapatch III | ❌ | 2027 |
| `14.x.x` | Heavy Duty | ✅ | December 2024–present |
| `13.x.x` | Refracted Reality | ✅ | May 2023–October 2024 |
| `12.x.x` | Mimicry | ✅ | December 2022–January 2023 |
| `11.x.x` | Parabellum | ✅ | October 2021–June 2022 |
| `10.x.x` | Scopophobia | ✅ | July 2020–March 2021 |
| `9.x.x` | Megapatch II | ✅ | November 2019–December 2019 |
| `8.x.x` | Megapatch I | ✅ | November 2018–April 2019 |
| `0.x.x–7.x.x` | Open Beta | ✅ | December 2017–October 2018 |

## How to use it:

All you need is to download the program, open it, type the root game path in the settings, press "Load game" and then press "Patch".

### Command line:

You can also use the program from command line without opening the GUI. Here is an example for PowerShell:

```powershell
$process = Start-Process -FilePath '.\SCPSL ModPatch.exe' -ArgumentList @(
    'patch',
    '--game-dir "C:\Games\SCPSL"',
    '--clean-launcher "C:\Launchers\SCPSL.exe"',
    '--result ".\patch-result.json"'
) -Wait -PassThru -WindowStyle Hidden
$process.ExitCode
```

Use `--help` to see the available parameters and exit codes. The result folder must already exist. For IL2CPP games, use a local clean launcher for your game's Unity version and unpack `GameAssembly.dll` beforehand if it is protected. Omit `--clean-launcher` for Mono games.

For supported IL2CPP games, `--bundle-version-mode correct` also synchronizes the displayed Unity `PlayerSettings.bundleVersion` in `SCPSL_Data/globalgamemanagers` with the detected game version. The default `preserve` mode leaves that file unchanged. Correction is prepared and validated before any game-file write; ambiguous fields or unsafe serialized-size changes fail preparation. The JSON result reports the previous/new version, correction status, and hashes of changed files. An already-correct file is reported without rewriting it. This option is not supported for Mono games. ModLaunch requests correction while retaining its own backups, activation, and restoration.

Both downloads include the same `SCPSL ModPatch.exe`: open it without arguments for the GUI, or pass command line arguments for the CLI. Their bundled clean launchers are in `clean_launchers/<Unity version>/SCPSL.exe`.

The JSON result reports `bundleVersionMode` and, when correction is requested, a `bundleVersion` object with the file path, `before`, `after`, and `status` (`applied` or `already-correct`). A changed settings file also appears in `modifiedFiles` with its original and resulting hashes; an already-correct file does not. Release inventories advertise this support through the `bundle-version-correction` capability. Launchers can retain ownership of backups, activation, and restoration while ModPatch prepares the corrected game files.

## What archive should I download?

There are a couple archives in the assets section.

- `SCPSL.ModPatch.Standalone.x64.zip` - is the recommended Windows x64 program, bundled with .NET 10 Desktop Runtime. No separate .NET installation is required.
- `SCPSL.ModPatch.dotnet6.0.zip` - is the Windows x64 compatibility program, built as a single executable that depends on .NET 6 Desktop Runtime (x64).

Both downloads support the GUI and CLI. New releases no longer include x86 builds or a separate CLI archive. The .NET 6 compatibility download requires .NET 6 Desktop Runtime even if a newer runtime is installed.

The standalone uses .NET's single-file assembly compression to reduce the executable size while retaining all runtime libraries and language resources. On Linux, run it through Wine or Proton; no Windows .NET installation is needed in that prefix. The compatibility download requires Windows .NET 6 Desktop Runtime in its Wine/Proton prefix.

### Building release packages

Install the .NET 10 SDK where your IDE can discover it, then open `SCPSL ModPatch.sln` and use the x64 configuration. In Rider, select the .NET 10 SDK's MSBuild under **Settings > Build, Execution, Deployment > Toolset and Build** if an older Visual Studio MSBuild is selected. Restore and build both targets with:

```powershell
dotnet restore './SCPSL ModPatch.sln'
dotnet build './SCPSL ModPatch.sln' -c Release
```

Use a .NET 10 SDK and PowerShell 7 to build both downloads:

```powershell
pwsh ./scripts/package.ps1
```

Use `-DotNet 'C:\path\to\dotnet.exe'` to select an SDK installation, or `-Package Standalone` / `-Package Compatibility` to publish one download. The archives, SHA-256 files and matching source snapshot are written to `artifacts/release`. Both programs bundle their application dependencies into one executable. Each download also includes the canonical `patchinfo.json`, clean launchers and `bundle.json` with its target framework, source revision, working tree status and file hashes. Patch templates remain in the repository and source snapshot. Packaging uses isolated build folders so it does not replace the IDE's restore assets. `scripts/package-cli.ps1` forwards to the same packaging script for compatibility.

## Creating your own mod-patches:

In order to create and use your own mod-patches you need to use `patchinfo.json` files.
What is it? It is special json that contains all patch data that ModPatch needs.<br/>
The "clean" version of the `patchinfo.json`, named `patchinfo_template.json`, is located [here](Shared_Files/patchinfo_template/patchinfo_template.json).
The template for byte patterns, named `patchinfo_pattern_template.json`, is located [here](Shared_Files/patchinfo_template/patchinfo_pattern_template.json).
The managed IL template, named `patchinfo_mono_template.json`, is located [here](Shared_Files/patchinfo_template/patchinfo_mono_template.json).

Both versions use the same `patchinfo.json`, with support for fixed offsets, byte patterns and managed IL patches.

Here is an example patch file:

```json
{
  "versionRanges": [
    {
      "versionRange": "10.1.0 – 11.1.4",
      "versionFrom": "10.1.0",
      "versionTo": "11.1.4",
      "cleanLauncherPath": "./clean_launchers/2019.4.16f1/SCPSL.exe",
      "cleanLauncherUrl": "https://raw.githubusercontent.com/hopperlopip/SCPSL-ModPatch/refs/heads/master/Shared_Files/clean_launchers/2019.4.16f1/SCPSL.exe",
      "metadataVersion": 24,
      "methods": {
        "patchMethods": [
          {
            "name": "CentralAuthManager$$Authentication",
            "patchOffset": 0,
            "patchData": "[RET]",
            "patchSize": 2,
            "nowarning": false,
            "patchifnotexists": null
          }
        ],
        "gameVersionMethod": {
          "name": "GameCore.Version$$.cctor",
          "autoFindOffsets": false,
          "majorOffset": 52,
          "minorOffset": 70,
          "revisionOffset": 88,
          "typeOffset": 106
        }
      }
    }
  ]
}
```

- `versionRange` is a "name" of your version range. It will be used in combo box. It is optional field. Default value is `empty string`.
- `versionFrom` is a first game version where your patch works. It is optional field if `versionRange` is not empty, but it is recommended to fill out this field. Default value is `empty string`.
- `versionTo` is a last game version where your patch works. An empty value or `Current` has no upper limit. Default value is `empty string`.
- `version` is one game version to use instead of `versionFrom` and `versionTo`. It is optional field.
- `gameAssemblySha256` is a 64-character SHA-256 hash of `GameAssembly.dll` to use instead of a game version. It is optional field.
- `cleanLauncherPath` is the path where "clean" version of launcher (SCPSL.exe) is located. Default value is `empty string`.
- `cleanLauncherUrl` is the URL where "clean" version of launcher is located, it is used if user doesn't have the "clean" launcher at `cleanLauncherPath`. It is optional field. Default value is `null`.
- `metadataVersion` is the new metadata version of the `global-metadata.dat`. Default value is `0`.
- `methods` contains patch methods and gameVersion method.
  - `patchMethods` is an array of your patches.
    - `name` is a name of method, you can get proper name from `script.json` in `ScriptMethod` array after using dump feature in [Il2CppDumper](https://github.com/Perfare/Il2CppDumper). Default value is `empty string`.
    - `patchOffset` is a relative offset from the method which name you typed above. Default value is `0`.
    - `patchPattern` is a byte pattern to use instead of `patchOffset`. Use hexadecimal bytes and `??` for any byte. It must match exactly once inside the specified method. It is optional field.
    - `patternOffset` is a relative offset from the start of the matched pattern. It is used only with `patchPattern`. Default value is `0`.
    - `patchData` is a hexadecimal representation of the replacing data. It has its own [keywords](#patchdata-keywords) in order to simplify the work. Default value is `empty string`.
    - `patchSize` is a size of patch area which will be replaced by the `patchData`, if the size is bigger than `patchData` size, the next bytes will be replaced by `NOP` assember instruction. It is optional field. If this field is `null` then the patchSize will be taken from lenght of `patchData`. Default value is `null`.
    - `nowarning` is a bool field that allows a missing method to be skipped without a warning. Default value is `false`.
    - `patchifnotexists` is a string field of a method name, if the field isn't `null` and not `empty string` the patch method will be patched only if the specified method in this field doesn't exist. Default value is `null`.
  - `gameVersionMethod` is an object for information of the Game Version. It is optional field. Default value is `null`.
    - `name` is a name of the GameVersion method. Default value is `"GameCore.Version$$.cctor"`.
    - `autoFindOffsets` is a bool field that enabled auto search for GameVersion. If this field is `true` then all offset fields are ignored. Default value is `false`.
    - `majorOffset` is an offset for major (the first one) number of the game version (<ins>14</ins>.0.0).
    - `minorOffset` is an offset for minor (the second one) number of the game version (14.<ins>0</ins>.0).
    - `revisionOffset` is an offset for revision (the third one) number of the game version (14.0.<ins>0</ins>).
    - `typeOffset` is an offset for the version type such as `Release`, `Public Beta`, `Private Beta` etc.

### PatchData keywords:

The `patchData` has some keywords you can use in order to simplify your work:

- `[NOP]` is a binary equivalent of assembler `nop` instruction.
- `[RET]` is a binary equivalent of assembler `ret` instruction.
- `[FASM:{asm code here}!]` is a special keyword that can assemble instruction, that is placed instead of `{asm code here}`, to its binary equivalent. [EXPERIMENTAL] May not work as expected. Supply `fasm_binaries/FASM.exe` in the program folder if you use this feature.

## Discord server

This project mainly was made for this discord server — https://discord.gg/HVpmehSGBY

## Used libraries/programs:

1) [Il2CppDumper](https://github.com/Perfare/Il2CppDumper) for getting IL2CPP info.
2) [unlicense](https://github.com/ergrelet/unlicense) for unpacking Themida.
3) [Fasm.NET](https://github.com/JamesMenetrey/Fasm.NET) in the FASM binaries.
4) [Mono.Cecil](https://github.com/jbevain/cecil) for managed IL patching.
5) [AssetsTools.NET](https://github.com/nesrak1/AssetsTools.NET) for reading Unity assets.
