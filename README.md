# Cropsmith

Interactive console tool (.NET 8 + Spectre.Console) that crops a video to the width × height you choose.
Picture and audio (and subtitles) are kept; only the frame changes.

## Run

    dotnet run --project src/Cropsmith            # or: Cropsmith.exe [optional video path]

Or publish a single exe: `dotnet publish src/Cropsmith -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o dist`

## Modes

- **Smart** – samples ~120 frames, finds the picture against a white or black background, trims the empty space,
  then scales to your width × height (padding only if the aspect ratio differs by more than 1%).
- **Exact** – cuts a precise width × height window (centered, top-left, or custom X/Y). No scaling.

## Dependencies

ffmpeg/ffprobe are located automatically (own folder → PATH → WinGet). If missing, Cropsmith offers to download a
static build into `%LOCALAPPDATA%\Cropsmith\bin`. Windows only.

Output is saved beside the source as `<name>_cropped.mp4` (never overwrites). Audio is copied untouched when
MP4-compatible, otherwise re-encoded to AAC. Video is H.264 CRF 14.

## Tests

    dotnet test
