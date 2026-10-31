# Patch-info authoring reference

This document will guide you through the fields and structure of `patchinfo.json` files. It is not a tutorial on native reverse engineering or IL2CPP internals. See the [README](../README.md) for general usage instructions.

## Files and loading

The canonical definitions are [Shared_Files/patchinfo.json](../Shared_Files/patchinfo.json). Authoring examples are available for [native offsets](../Shared_Files/patchinfo_template/patchinfo_template.json) and [native byte patterns and selectors](../Shared_Files/patchinfo_template/patchinfo_pattern_template.json). Templates contain illustrative method names/settings, not a patch guaranteed to apply to a game.

Both downloads use the same schema:

```json
{
  "versionRanges": [
    {
      "versionRange": "My build range",
      "versionFrom": "10.1.0",
      "versionTo": "11.1.4",
      "cleanLauncherPath": "./clean_launchers/2019.4.16f1/SCPSL.exe",
      "metadataVersion": 24,
      "methods": {
        "patchMethods": [],
        "gameVersionMethod": {
          "name": "GameCore.Version$$.cctor",
          "autoFindOffsets": true
        }
      }
    }
  ]
}
```

An empty `patchMethods` array describes no native edits; the IL2CPP workflow still handles metadata and the launcher. The example above illustrates structure, not useful authentication patches.

CLI `--patch-info PATH` chooses a definitions file and defaults to the copy beside the executable. The GUI uses `patchinfo.json` in its working directory unless **Use a custom patchinfo.json file** is enabled. Restart the GUI after changing its custom-file setting so the new definitions are loaded. CLI deserialization rejects unknown JSON members; GUI loading does not use that strict unknown-member setting. Use the documented field names and types rather than relying on reader differences.

The CLI uses Json.NET for manifest loading, report-path discovery and result serialization. Discovery accepts the production parser's comments, trailing commas and case-insensitive member names; execution still rejects unknown schema members. Avoid duplicate properties: discovery conservatively protects every case variant of a recognized input path. If an existing manifest cannot be parsed or inspected, the CLI fails closed without replacing the result file.

## Selecting one patch set

Each `versionRanges` entry is a **complete profile**, including parser/version-reader settings.

Automatic final selection checks these tiers in order:

1. Original `GameAssembly.dll` SHA-256: `gameAssemblySha256`.
2. Exact numeric `version`.
3. Inclusive numeric `versionFrom`–`versionTo` range.
>File Hash > Game Version > Range

A match in a higher tier takes precedence over lower tiers. **Two matches in the same selected tier are an error**; array order does not break ties. The `versionRange` display label does not establish precedence. A hash-only match can select a profile even when no release version can be detected.

| Field | Meaning and constraints |
| --- | --- |
| `versionRange` | Optional display label, default `""`. Used in the GUI and by CLI `--profile`. Give labels unique names. |
| `gameAssemblySha256` | Original `GameAssembly.dll` hash. Exactly 64 hexadecimal characters, compared case-insensitively. |
| `version` | Exact selector with exactly three nonnegative numeric components, such as `13.6.9`. |
| `versionFrom` | Inclusive numeric lower bound, in three-component form. Default `""`. For compatibility, an omitted lower bound with a nonempty `versionRange` means `0.0.0`; prefer explicit bounds. |
| `versionTo` | Inclusive numeric upper bound. Empty or `"Current"` (case-insensitive) means unbounded. A bounded end cannot precede the start. |

Use **one selector kind per profile**: hash, exact version, or range. Hash profiles cannot also supply `version`, `versionFrom` or `versionTo`; exact-version profiles cannot supply range bounds. The display label can coexist with any selector kind. Unbounded `Current` is not evidence that future builds work.

A profile's display string is its nonempty `versionRange`, otherwise its hash prefixed with `SHA-256: `, otherwise its exact `version`, otherwise its range bounds (or `Undefined version range`). CLI `--profile` requires exactly one entry with that exact display string. It is an **initial parser choice, not a forced final patch target**.

### IL2CPP bootstrap and version detection

Before the final game version is known, IL2CPP needs initial metadata parser settings. An original-DLL hash match has priority; an explicit initial profile can supply settings for a custom build. Otherwise the session matches `UnityPlayer.dll`'s product version to the Unity version component in `cleanLauncherPath`. Both suffixed releases such as `2019.3.0b9` and numeric Windows resources such as `2019.3.0.14561972` are recognized. A numeric resource narrows the parser candidates without inventing a release-channel suffix.

Parser settings and game-version readers are resolved separately. Profiles sharing a metadata schema can use different native offsets or automatic offset discovery. The session verifies each distinct reader against the native image and accepts only a version that selects a target with that reader's configuration, preserving hash/exact/range precedence. The final selected target supplies the launcher path. Arbitrary bytes at configured offsets are not sufficient for automatic detection; modern readers must identify instruction-aligned version stores within the named method. Multiple self-consistent native identities are rejected rather than resolved by array order.

When Unity identification is unavailable or matching profiles use different metadata schemas, the original metadata header can identify an unambiguous parser family. Missing or conflicting parser evidence is an error; `--profile` remains an advanced override for custom definitions, not a prerequisite for the bundled builds.

`methods.gameVersionMethod` is optional (`null` by default). A profile selected only by version/range still needs a detectable version; omitting a reader does not make a range automatically match.

| Reader field | Meaning |
| --- | --- |
| `name` | Native ScriptMethod name; default `GameCore.Version$$.cctor`. |
| `autoFindOffsets` | Default `false`. When `true`, searches the supported native version-field layout rather than using the supplied offsets. |
| `majorOffset`, `minorOffset`, `revisionOffset`, `typeOffset` | Byte offsets relative to the version method for the supported version-byte layout. Default `0`; use values established for the actual build, not template zeros. |

When the default version method is absent, the native reader can use the supported legacy code/metadata release-label analysis. This is not arbitrary string scanning, and detected legacy releases without a type field are read-only in the GUI.

## Paths, metadata and launcher settings

| Field | Behavior |
| --- | --- |
| `cleanLauncherPath` | Local clean Unity launcher, default `""`. Required for a complete IL2CPP patch workflow unless overridden with CLI `--clean-launcher`. Use the game's Unity version. |
| `cleanLauncherUrl` | Optional GUI download fallback when the local launcher is missing. Default `null`. CLI never downloads from it. |
| `metadataVersion` | Integer written into the parser's metadata header at byte offset 4. Default `0`, which is a schema default, not a usable value for arbitrary games. |

**Path bases differ:** CLI resolves manifest launcher paths relative to the manifest's directory. GUI launcher paths follow its working directory. Explicit CLI paths are relative to the caller's working directory. For one manifest to work in both modes, keep its launcher tree next to it and start the GUI from that directory, or use suitable absolute paths. CLI requires a separate, nonempty clean-launcher input, distinct from the game destinations and manifest. It does not verify that your override matches the game's Unity version.

Native game-file destinations must be distinct from one another and from the manifest and clean-launcher input. Game-file alias checks run before native loading; all relevant inputs and destinations are checked again before saving. CLI checks resolve symbolic links/junctions and compare Windows file identities, including hardlinks. These checks do not make multi-file native saves transactional.

GUI and CLI IL2CPP loading normalize only a temporary parser copy; loading or a failed load leaves runtime metadata unchanged. GUI saves the selected header after an explicit patch or version edit saves its assembly, checking metadata for external changes before either write. CLI `--metadata-mode write` (default) saves the selected header during the save stage, while `preserve` never writes runtime metadata. Patching does not create original-file backups. Preserve originals before patching. GUI unpacking requires fresh, structurally valid x64 DLL output from a successful Unlicense process and retains the replaced DLL in a unique `GameAssembly.dll.before-unpack-*.bak` file.

## Native method patches

Entries in `methods.patchMethods` use Il2CppDumper `script.json` names from `ScriptMethod`, for example `CentralAuthManager$$Authentication`.

| Field | Meaning |
| --- | --- |
| `name` | Native method name. Default `""`; supply the actual dumped name. |
| `patchOffset` | Nonnegative byte offset from the method start. Omitted/`null` means zero for an offset patch. Mutually exclusive with `patchPattern`, even if explicitly set to `0`. |
| `patchPattern` | Optional unique, method-scoped byte pattern; see below. |
| `patternOffset` | Signed byte displacement from the pattern match, default `0`. A nonzero value requires `patchPattern`. |
| `patchData` | Nonempty replacement hex data, with optional keywords. Default `""`. |
| `patchSize` | Bytes to overwrite, default `null` (replacement-data length). Must be positive and at least the replacement length; the remainder is filled with `90` (NOP). |
| `nowarning` | Default `false`. Allows a missing method to be skipped; does not make malformed patches or failed pattern matches optional. |
| `patchifnotexists` | Optional native method name. Skip this fallback patch when that method exists. Default `null`; empty has no condition. |

All native writes are planned before the assembly buffer is modified. Empty data, invalid sizes, out-of-file writes and overlapping writes fail preparation. Offset patches are checked against the binary bounds, **not the target method's bounds**; the author must establish a safe method extent. Pattern patches additionally enforce their inferred method bounds. Saving rejects a DLL that changed on disk since loading/the last save. Multi-file IL2CPP saving is not transactional; prepare success does not guarantee every destination was saved.

### Byte patterns

A pattern accepts hex byte pairs, optionally separated by whitespace or hyphens, and full-byte wildcards `??` or a standalone `?` token. Examples: `48 8B ?? ??`, `48-8B-??-??`, `488B????`. Partial-nibble wildcards such as `A?` are invalid. Patterns must be nonempty and contain at least one fixed byte.

```json
{
  "name": "YourType$$YourMethod",
  "patchPattern": "48 8B ?? ??",
  "patternOffset": 0,
  "patchData": "[RET]",
  "patchSize": 2
}
```

The method name must resolve to one native address. The pattern must match **exactly once within that method's inferred native range**. No match or multiple matches fail; there is no global-binary fallback. The entire write after `patternOffset` must remain in that method. A negative `patternOffset` is allowed only if these bounds remain satisfied.

### PatchData keywords

Native `patchData` accepts hexadecimal byte pairs; literal spaces and hyphens are removed before hex decoding. Keywords are case-sensitive:

- `[NOP]` expands to `90`.
- `[RET]` expands to `C3`.
- `[FASM:{assembly code}!]` assembles the enclosed native instructions with optional FASM support. This remains experimental. Supply `fasm_binaries/FASM.exe` in the program directory; missing FASM or assembly failures reject preparation. Ordinary supplied patches do not require authoring assembly code.

These are architecture-specific native machine bytes. Check instruction boundaries, calling conventions, return values and the intended architecture yourself.

### Missing methods and warnings

Required native patches use the same failure policy in GUI and CLI:

| Situation | Behavior |
| --- | --- |
| `patchifnotexists` names an existing method | Skip as `skipped-fallback`. |
| Target method missing with `nowarning: true` | Skip as `skipped-optional`. |
| Required method missing in any patch | Fail preparation before applying any planned writes. |
| Present method but bad/ambiguous pattern, data, size or overlap | Fail regardless of `nowarning`. |

Do not interpret a missing-method warning as successful application of that method's patch. CLI `patches` entries record the applicable outcomes.

## Results and qualification

During CLI preparation, applied edits are reported as `prepared` until the assembly save succeeds; skipped outcomes retain their reason. Reports can show changes after a failed save, so do not use a single patch status as a transaction result. The CLI's process exit code and overall result indicate whether the operation succeeded.

Before using a custom profile, retain the original DLL hash, game/version evidence, parser settings, unique pattern matches and saved-copy comparison. Verify untouched data as well as intended edits.
