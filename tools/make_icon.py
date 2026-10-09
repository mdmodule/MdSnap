"""
MdSnap 图标生成器
输入:  MdSnap_icon_3.png  (白底 RGB 无透明通道)
输出:  MdSnap.ico (多尺寸) + MdSnap_preview.png (棋盘底预览，用于人工检查)

处理流程:
  1. 四角 flood-fill 抠掉外层白底  -> 透明
     (只填与边角连通的白色，保留图标内部白色的 "MD" 与选区框)
  2. 按 alpha 自动裁边
  3. 补成正方形(透明填充)
  4. 生成多尺寸 ICO: 16/20/24/32/40/48/64/128/256
"""
from PIL import Image, ImageDraw
import os

SRC = r"C:\Users\Administrator\Desktop\MdSnap_icon_concepts\MdSnap_icon_3.png"
OUT_DIR = r"D:\workspace\C#\MdSnap\MdSnap\Assets"
ICO = os.path.join(OUT_DIR, "MdSnap.ico")
PREVIEW = os.path.join(OUT_DIR, "MdSnap_preview.png")

os.makedirs(OUT_DIR, exist_ok=True)

im = Image.open(SRC).convert("RGBA")
w, h = im.size

# --- 1. 四角 flood-fill 抠白底 ---
MAGIC = (255, 0, 255, 255)   # 魔术色，稍后替换为透明
for corner in [(0, 0), (w - 1, 0), (0, h - 1), (w - 1, h - 1)]:
    ImageDraw.floodfill(im, corner, MAGIC, thresh=60)

px = im.load()
for y in range(h):
    for x in range(w):
        if px[x, y] == MAGIC:
            px[x, y] = (0, 0, 0, 0)

# --- 2. 自动裁边 ---
bbox = im.getbbox()
im = im.crop(bbox)
cw, ch = im.size

# --- 3. 补正方形 ---
side = max(cw, ch)
sq = Image.new("RGBA", (side, side), (0, 0, 0, 0))
sq.paste(im, ((side - cw) // 2, (side - ch) // 2), im)

# --- 4. 预览图(棋盘底, 256) ---
prev = Image.new("RGBA", (256, 256), (0, 0, 0, 0))
for yy in range(0, 256, 16):
    for xx in range(0, 256, 16):
        c = (200, 200, 200, 255) if ((xx // 16 + yy // 16) % 2 == 0) else (150, 150, 150, 255)
        for py in range(yy, min(yy + 16, 256)):
            for pxx in range(xx, min(xx + 16, 256)):
                prev.putpixel((pxx, py), c)
prev.alpha_composite(sq.resize((256, 256), Image.LANCZOS))
prev.save(PREVIEW)

# --- 5. 多尺寸 ICO ---
sizes = [(16, 16), (20, 20), (24, 24), (32, 32), (40, 40), (48, 48), (64, 64), (128, 128), (256, 256)]
sq.save(ICO, format="ICO", sizes=sizes)

print("裁剪后尺寸:", (cw, ch), "-> 补方:", (side, side))
print("ICO 已生成:", ICO, os.path.getsize(ICO), "bytes")
print("预览已生成:", PREVIEW)
