# SCPSL-ModPatch
A program that lets you disable external game anti-cheat and modify the game freely. Note that servers with enabled `online_mode` in the config will not work.

## How to use it:
All you need is to download the program, open it, type the root game path in the settings, load IL2CPP and then press the "Patch" button.

### Command line:
You can also use the program from command line without opening the GUI. Here is an example for PowerShell:

```powershell
$process = Start-Process -FilePath '.\SCPSL ModPatch.exe' -ArgumentList @(
    'patch',
    '--game-dir "C:\Games\SCPSL"',
    '--clean-launcher ".\clean_launchers\6000.0.43f1\SCPSL.exe"',
    '--result ".\patch-result.json"'
) -Wait -PassThru -WindowStyle Hidden
$process.ExitCode
```

- `--game-dir PATH` is the root game path. This parameter is required.
- `--result PATH` is the path where the JSON result will be saved. This parameter is required, and its folder must already exist.
- `--patch-info PATH` is the path to your patch file. Default is `patchinfo.json` in the program folder for both versions.
- `--profile LABEL` is the name of the game version from the combo box. Use it if the version cannot be selected automatically.
- `--clean-launcher PATH` is the path to an existing "clean" launcher for your game's Unity version. Default is `cleanLauncherPath` from the patch file, relative to that file's folder.
- `--metadata-mode write|preserve` defines whether to update `global-metadata.dat`. Default is `write`; `preserve` leaves the original metadata file unchanged.

Use `--help` to see the available parameters. The JSON result contains the detected game version, patch results and modified files. The exit code is `0` on success, `1` if patching fails, `2` if the parameters are invalid and `3` if the result cannot be saved.

Use a local clean launcher and unpack `GameAssembly.dll` beforehand if it is protected. Command line mode uses the supplied parameters instead of the GUI settings.

If you downloaded the CLI archive, use `SCPSL.ModPatch.Cli.exe` instead. Its bundled clean launchers have the name `SCPSL.bin`; pass the appropriate file with `--clean-launcher`.

## What archive should I download?
There are a couple archives in the assets section.

- `SCPSL.ModPatch.dotnet6.0.zip` - is a program that depends on .NET 6 Desktop Runtime.
- `SCPSL.ModPatch.Standalone.x86.zip` - is a standalone x86 program that doesn't need anything to run itself.
- `SCPSL.ModPatch.Standalone.x64.zip` - is a standalone x86_64 program that doesn't need anything to run itself.
- `SCPSL.ModPatch.Standalone.zip` - the same as `SCPSL.ModPatch.Standalone.x86.zip`, was often used for versions before 2.0.0.
- `SCPSL-ModPatch.Cli-win-x64.zip` - is a command line version for Windows x64 that depends on .NET 6 Runtime (x64).

## Creating your own mod-patches:
In order to create and use your own mod-patches you need to use `patchinfo.json` files.
What is it? It is special json that contains all patch data that ModPatch needs.<br/>
The "clean" version of the `patchinfo.json`, named `patchinfo_template.json`, is located [here](Shared_Files/patchinfo_template/patchinfo_template.json) or in the root folder of the ModPatch program.
The template for byte patterns, named `patchinfo_pattern_template.json`, is located [here](Shared_Files/patchinfo_template/patchinfo_pattern_template.json).

Both versions use the same `patchinfo.json`. You can mix patches that use `patchOffset` and `patchPattern` in one file, and use the optional `version` or `gameAssemblySha256` fields to target a particular game build. Existing patch files need no changes when used with the updated program. Older program versions do not understand the new fields.

The included IL2CPP profiles start at `9.0.0`. Versions `9.0.0–9.1.3` and `10.0.0–10.0.6` use the legacy release reader and their matching Unity launchers. Select their profile in the GUI, or pass its exact label with `--profile` in the CLI when the Unity version cannot be selected automatically. Protected DLLs must be unpacked first; earlier Mono builds are not supported. See the [expanded historical checks](docs/expanded-history-results.md).

Here is an example of patching versions from 10.1.0 to 11.1.4:
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
- `version` is one game version, such as `10.1.1`, to use instead of `versionFrom` and `versionTo`. It is optional field.
- `gameAssemblySha256` is a 64-character SHA-256 hash of `GameAssembly.dll` to use instead of a game version. It is optional field.
- `cleanLauncherPath` is the path where "clean" version of launcher (SCPSL.exe) is located. Default value is `empty string`.
- `cleanLauncherUrl` is the URL where "clean" version of launcher is located, it is used if user doesn't have the "clean" launcher at `cleanLauncherPath`. It is optional field. Default value is `null`.
- `metadataVersion` is the new metadata version of the `global-metadata.dat`. Default value is `0`.
- `methods` contains patch methods and gameVersion method.
  - `patchMethods` is an array of your patches.
    - `name` is a name of method, you can get proper name from `script.json` in `ScriptMethod` array after using dump feature in [Il2CppDumper](https://github.com/Perfare/Il2CppDumper). Default value is `empty string`.
    - `patchOffset` is a relative offset from the method which name you typed above. Default value is `0`.
    - `patchPattern` is a byte pattern to use instead of `patchOffset`. Use hexadecimal bytes and `??` for any byte. It is optional field.
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

Use only one of `versionFrom`/`versionTo`, `version` or `gameAssemblySha256` in each patch set. If several entries match, the DLL hash has priority, followed by the exact version and then the version range. Only one patch set is used.

### PatchData keywords:
The `patchData` has some keywords you can use in order to simplify your work:

- `[NOP]` is a binary equivalent of assembler `nop` instruction.
- `[RET]` is a binary equivalent of assembler `ret` instruction.
- `[FASM:{asm code here}!]` is a special keyword that can assemble instruction, that is placed instead of `{asm code here}`, to its binary equivalent. [EXPERIMENTAL] May not work as expected. For the CLI version, supply `fasm_binaries/FASM.exe` in the program folder.

Also you can use ` `(space) and `-` separators but it is not recommended.

### Pattern matching:
You can use a byte pattern instead of a fixed `patchOffset`. Here is an example:

```json
{
  "name": "SimpleMenu.<StartLoad>d__8$$MoveNext",
  "patchPattern": "48 8B 80 B8 00 00 00 48 8B 48 08 8B 01",
  "patchData": "B8 01 00 00 00",
  "patchSize": 13
}
```

Do not use `patchOffset` together with `patchPattern`. The pattern must match exactly once inside the specified method. If it is missing or matches more than once, patching stops.

Pattern matching supports Windows x64 game files. Existing patches that use `patchOffset` still work.

## Discord server
This project mainly was made for this discord server — https://discord.gg/HVpmehSGBY

## Used libraries/programs:
1) [Il2CppDumper](https://github.com/Perfare/Il2CppDumper) for getting IL2CPP info.
2) [unlicense](https://github.com/ergrelet/unlicense) for unpacking Themida.
3) [Fasm.NET](https://github.com/JamesMenetrey/Fasm.NET) in the FASM binaries.
