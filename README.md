# SCPSL-ModPatch

A program that lets you disable central authentication and external game anti-cheat to modify the game freely. 
This is a requirement for legitimate local modding as per the [SCP:SL EULA](https://store.steampowered.com/eula/700330_eula_0).
Note that servers with `online_mode` enabled in the config will not permit modified clients.

## Supported game versions

| Game version | Release |
|:-------------|:--------|
| `14.x.x` | Heavy Duty |
| `13.x.x` | Refracted Reality |
| `12.x.x` | Mimicry |
| `11.x.x` | Parabellum |
| `10.x.x` | Scopophobia |
| `9.x.x` | Megapatch II |

Older versions of the game are built with Mono and can be modified without additional patching.

## How to use it:

1. Download a program archive from [Releases](https://github.com/AnomalyLabsIO/SCPSL-ModPatch/releases) and extract its entire contents.
2. Close the game and back up your game installation.
3. Open `SCPSL ModPatch.exe`, select **Open settings**, choose the root game folder (containing `SCPSL.exe`), and select **Apply settings**.
4. Select **Load game**, then **Patch game**, and follow any prompts.

For protected game DLLs, the program will ask to download and run [Unlicense](https://github.com/ergrelet/unlicense).
### Command line:

You can also patch a game without opening the graphical interface. Run this from the extracted program folder:

```bat
"SCPSL ModPatch.exe" patch --game-dir "C:\Program Files (x86)\Steam\steamapps\common\SCP Secret Laboratory" --result "patch-result.json"
```

The command writes a JSON result to the specified path. Its parent folder must already exist. Use `--help` to see command-line help.

| Option | Description |
| --- | --- |
| `--game-dir PATH` | Required. The root game folder. |
| `--result PATH` | Required for patching. Where to save the JSON result. |
| `--patch-info PATH` | Use custom patch definitions. Defaults to `patchinfo.json` beside the program. |
| `--profile LABEL` | Select a game profile by its exact name. Usually detected automatically. |
| `--clean-launcher PATH` | Use a matching clean launcher instead of the bundled one. |
| `--metadata-mode write\|preserve` | Update (`write`, default) or preserve (`preserve`) IL2CPP metadata. |
| `--bundle-version-mode preserve\|correct` | Preserve (`preserve`, default) or correct (`correct`) the game's displayed version. Available from version 10 onward. |

## Which archive should I download?

- `SCPSL.ModPatch.Standalone.x64.zip` — Windows x64 program with the .NET runtime included. Larger, but no separate .NET installation is needed.
- `SCPSL.ModPatch.dotnet6.0.zip` — Windows x64 program requiring **.NET 6 Desktop Runtime (x64)**. Smaller, as long as the correct .NET runtime is installed.

Both downloads include the graphical interface, command-line mode, patch definitions, and clean Unity launchers. Extract the entire archive before running the program.

> Supported game versions requires a 64-bit OS, so the program is now 64-bit only. x86/32-bit Windows versions have been removed. You may build a 32-bit version from source if you need it.

## Linux Support

Use the **standalone x64** download with Wine or Proton; it includes the Windows .NET runtime. Extract the archive and run this from its folder:
- Wine:
```bash
wine "./SCPSL ModPatch.exe"
```
- Proton, install [Protontricks](https://github.com/Matoking/protontricks#usage), then:
```bash
protontricks-launch "./SCPSL ModPatch.exe
```
The .NET 6 download requires **Windows .NET 6 Desktop Runtime (x64)** installed in the same Wine/Proton prefix.


## Creating your own mod-patches:

Custom patches are defined in `patchinfo.json`. See the [patch-info authoring guide](docs/patchinfo.md) for instructions and templates.

## Discord server

Join the community on [Discord](https://discord.gg/HVpmehSGBY).

## Used libraries/programs:

1. [Il2CppDumper](https://github.com/Perfare/Il2CppDumper) for IL2CPP information.
2. [unlicense](https://github.com/ergrelet/unlicense) for protected-DLL unpacking.
3. [Fasm.NET](https://github.com/JamesMenetrey/Fasm.NET) for native assembly support.
