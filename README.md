# Cropsmith

Interactive console tool (.NET 8 + Spectre.Console) that crops a video to the width × height you choose.
Picture and audio (and subtitles) are kept; only the frame changes.

## Run

    dotnet run --project src/Cropsmith            # or: Cropsmith.exe [optional video path]

Or publish a single exe: `dotnet publish src/Cropsmith -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o dist`

## Modes

- **Smart** – samples ~120 frames, finds the picture against a white or black background, trims the empty space,
  then scales to your width × height (padding only if the aspect ratio differs by more than 1%).
- **Exact** – you give the region to keep (width × height, centered, top-left, or custom X/Y), then the output size.
  The region is scaled to fit and padded with black, like Smart. Output size defaults to the region size, which is a plain crop.

If the video has subtitles you're asked whether to turn them off by default. They stay in the file as a selectable
track; only the "on by default" flag is cleared.

## Dependencies

ffmpeg/ffprobe are located automatically (own folder → PATH → WinGet). If missing, Cropsmith offers to download a
static build into `%LOCALAPPDATA%\Cropsmith\bin`. Windows only.

Output is saved beside the source as `<name>_cropped.mp4` (never overwrites). Audio is copied untouched when
MP4-compatible, otherwise re-encoded to AAC. Video is H.264 CRF 14.

## Tests

    dotnet test

## Keys

| Key | Does |
| --- | --- |
| Enter | confirm / select |
| Esc | go back one step (on the first screen: asks to quit); cancels a running crop or download |
| Ctrl+C, Ctrl+Q | quit from anywhere (cancels a running crop) |
| Q | quit, on selection lists |
| ↑ ↓ Home End PgUp PgDn | move in lists |
| ← → Home End Del Backspace, Ctrl+U | edit text/number fields; Ctrl+U clears |

A cancelled crop deletes its partial output file.
