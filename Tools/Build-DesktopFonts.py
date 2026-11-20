"""Generate the desktop font assets from the approved local font files."""
import argparse
import shutil
import subprocess
from pathlib import Path

from fontTools.ttLib import TTFont


def rename(path, family):
    font = TTFont(path)
    for name_id in (1, 3, 4, 6, 16):
        font["name"].removeNames(nameID=name_id)
        value = family.replace(" ", "") if name_id == 6 else family
        font["name"].setName(value, name_id, 3, 1, 0x409)
    font.save(path)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--wenkai", type=Path, required=True)
    parser.add_argument("--serif", type=Path, required=True)
    parser.add_argument("--vtt-sample", type=Path, required=True)
    parser.add_argument("--autohint", type=Path, required=True)
    args = parser.parse_args()
    output = Path(__file__).resolve().parent.parent / "AniMeido.Desktop/Assets/Fonts"
    output.mkdir(parents=True, exist_ok=True)
    wenkai = output / "DesktopWenKai.ttf"
    subprocess.run([
        str(args.autohint), "--increase-x-height=0", "--hinting-range-min=8",
        "--hinting-range-max=50", "--hinting-limit=200",
        str(args.wenkai), str(wenkai),
    ], check=True)
    rename(wenkai, "Desktop WenKai")
    serif = output / "DesktopSerif.ttf"
    shutil.copyfile(args.serif, serif)
    rename(serif, "Desktop Serif")
    reading = output / "DesktopWenKaiReading.ttf"
    shutil.copyfile(args.vtt_sample, reading)
    rename(reading, "Desktop WenKai Reading")
    source = TTFont(args.wenkai)
    complete = TTFont(wenkai)
    assert source.getBestCmap().keys() <= complete.getBestCmap().keys()
    assert len(complete.getBestCmap()) > 40000
    for path in (wenkai, serif, reading):
        font = TTFont(path)
        print(f"{path.name}: {len(font.getBestCmap())} characters, {path.stat().st_size} bytes")


if __name__ == "__main__":
    main()
