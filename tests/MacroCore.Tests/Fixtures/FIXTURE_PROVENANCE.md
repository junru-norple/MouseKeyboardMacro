# Fixture provenance

The public repository stores no `.macro` files. `SyntheticMacroFixtureFactory` creates deterministic fixtures only at test runtime under the repository-local `TestSandbox`.

- Fixed UTC timestamp: `2000-01-01T00:00:00Z`
- Display: `DISPLAY_SYNTHETIC`, 1280 x 720
- Target: `SyntheticTarget.exe`
- Coordinates and event timing: fixed synthetic constants
- Random input, local paths, user hashes, window titles, device identities, and user recordings: none

The generated files are deleted with `TestSandbox` and are never copied into Repository or ReleaseAssets.
