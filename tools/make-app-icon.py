"""assets/icon.png から、exe とトレイに使う app.ico を作る。

使い方: python tools/make-app-icon.py (Pillow が必要)
"""
from pathlib import Path

from PIL import Image

ROOT = Path(__file__).resolve().parent.parent
SOURCE = ROOT / "assets" / "icon.png"
OUTPUT = ROOT / "dotnet" / "AgentLimitChecker.App" / "app.ico"
# 20、24、40 は表示倍率 125%、150%、250% のトレイと小さいアイコンの大きさ。
SIZES = [16, 20, 24, 32, 40, 48, 64, 256]


def main() -> None:
    master = Image.open(SOURCE).convert("RGBA")
    # 各サイズを元の大きさから直接縮小する。Pillow に任せると最大の 1 枚から段階的に縮めて細い線がぼやける。
    frames = [master.resize((size, size), Image.LANCZOS) for size in SIZES]
    frames[-1].save(OUTPUT, format="ICO", sizes=[(size, size) for size in SIZES], append_images=frames[:-1])


if __name__ == "__main__":
    main()
