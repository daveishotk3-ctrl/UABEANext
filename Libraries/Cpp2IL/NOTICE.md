# Vendored Cpp2IL libraries

`LibCpp2IL` and `WasmDisassembler` are copied from https://github.com/SamboyCoding/Cpp2IL
(commit `b5ad444`, 2026-09-12) and are used under the MIT license in `LICENSE`.

They are vendored instead of using the `Samboy063.LibCpp2IL` NuGet package because the newest published
package (`2022.1.0-pre-release.21`) only reads IL2CPP metadata up to version 31, while Unity 6 builds
use newer versions. This copy reads metadata versions 23 to 108.

Changes from upstream:

- `TargetFrameworks` reduced to `net8.0;netstandard2.0` (the frameworks this repo builds for).
- `GeneratePackageOnBuild` turned off.

To update, copy the two folders over from upstream again and reapply the changes above.
