"""
从思源黑体可变字体生成 MdSnap 的内嵌字体（子集化 + 按 OFL 要求改名）。

素材：SourceHanSansSC-VF.otf（思源黑体 简体中文 可变字体）
授权：SIL Open Font License 1.1 —— 允许商用、允许嵌入、允许修改
      版权声明为 "© 2014-2021 Adobe, with Reserved Font Name 'Source'"

⚠️ OFL 合规要点（本脚本据此实现）：
   1. 子集化属于 OFL 定义的"修改版"，**不得继续使用保留字体名 'Source'**
      → 这里改名为 "MdSnap Sans SC"
   2. 原始版权声明必须保留（nameID 0 保留原始行 + 追加修改说明）
   3. 许可证描述（nameID 13）与 OFL 链接（nameID 14）必须保留
   4. 许可证全文随软件分发（见 Assets/Fonts/OFL.txt）

用法：python tools/make_font.py
输出：MdSnap/Assets/Fonts/MdSnapSansSC-Regular.otf / -SemiBold.otf
"""
import glob
import os
import sys

from fontTools.ttLib import TTFont
from fontTools.varLib import instancer
from fontTools import subset

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SRC = os.path.join(ROOT, "tools", "fontsrc", "package", "SourceHanSansSC-VF.otf")
OUTDIR = os.path.join(ROOT, "MdSnap", "Assets", "Fonts")

FAMILY = "MdSnap Sans SC"
PREFIX = "MdSnapSansSC"

ORIG_COPYRIGHT = "© 2014-2021 Adobe (http://www.adobe.com/), with Reserved Font Name 'Source'."
NEW_COPYRIGHT = (
    "Copyright (c) 2014-2021 Adobe (http://www.adobe.com/), with Reserved Font Name 'Source'. "
    "This is a modified version (glyph subset + renamed family, 'MdSnap Sans SC') prepared for MdSnap. "
    "Licensed under the SIL Open Font License, Version 1.1."
)
OFL_DESC = (
    "This Font Software is licensed under the SIL Open Font License, Version 1.1. "
    "This Font Software is distributed on an \"AS IS\" BASIS, WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, "
    "either express or implied. See the SIL Open Font License for the specific language, permissions and "
    "limitations governing your use of this Font Software."
)
OFL_URL = "https://scripts.sil.org/OFL"


def base_chars() -> set:
    """ASCII / 拉丁补充 / 常用符号 / 中文标点 / 全角字符。"""
    chars = set()
    ranges = [
        (0x0020, 0x007E),   # ASCII 可打印
        (0x00A0, 0x00FF),   # 拉丁补充
        (0x2000, 0x206F),   # 通用标点
        (0x2190, 0x21FF),   # 箭头
        (0x2460, 0x24FF),   # 带圈数字
        (0x25A0, 0x25FF),   # 几何图形（菜单/图标用）
        (0x2600, 0x26FF),   # 杂项符号
        (0x3000, 0x303F),   # 中文标点
        (0xFF00, 0xFFEF),   # 全角字符
    ]
    for lo, hi in ranges:
        chars.update(chr(c) for c in range(lo, hi + 1))
    return chars


def gb2312_chars() -> set:
    """GB2312 全集（6763 常用汉字 + 符号）——覆盖日常中文绰绰有余。"""
    chars = set()
    for hi in range(0xA1, 0xF8):
        for lo in range(0xA1, 0xFF):
            try:
                chars.add(bytes([hi, lo]).decode("gb2312"))
            except Exception:
                pass
    return chars


def project_chars() -> set:
    """把项目源码/界面里出现过的所有非 ASCII 字符都收进来，保证 UI 不出豆腐块。"""
    chars = set()
    for pat in ("*.cs", "*.xaml"):
        for path in glob.glob(os.path.join(ROOT, "MdSnap", "**", pat), recursive=True):
            if f"{os.sep}obj{os.sep}" in path or f"{os.sep}bin{os.sep}" in path:
                continue
            try:
                text = open(path, encoding="utf-8").read()
            except Exception:
                continue
            chars.update(ch for ch in text if ord(ch) > 0x7F)
    return chars


def rename(font: TTFont, style: str) -> None:
    """按 OFL 保留字体名条款改名。"""
    name = font["name"]
    full = f"{FAMILY} {style}"
    ps = f"{PREFIX}-{style}"
    for pid, eid, lid in ((3, 1, 0x409), (1, 0, 0)):
        name.setName(NEW_COPYRIGHT, 0, pid, eid, lid)
        name.setName(FAMILY, 1, pid, eid, lid)
        name.setName(style, 2, pid, eid, lid)
        name.setName(f"{FAMILY}:{style}:2026", 3, pid, eid, lid)
        name.setName(full, 4, pid, eid, lid)
        name.setName(ps, 6, pid, eid, lid)
        name.setName(OFL_DESC, 13, pid, eid, lid)
        name.setName(OFL_URL, 14, pid, eid, lid)
        name.setName(FAMILY, 16, pid, eid, lid)
        name.setName(style, 17, pid, eid, lid)


def build(weight: int, style: str, chars: set) -> str:
    print(f"\n--- 生成 {style}（wght={weight}）")
    font = TTFont(SRC)

    print("    1) 从可变字体取出该字重 …")
    instancer.instantiateVariableFont(font, {"wght": weight}, inplace=True, updateFontNames=False)

    print(f"    2) 子集化（目标 {len(chars)} 个字符）…")
    opts = subset.Options()
    opts.layout_features = ["*"]
    opts.name_IDs = ["*"]
    opts.name_legacy = True
    opts.name_languages = ["*"]
    opts.notdef_outline = True
    opts.recalc_bounds = True
    opts.drop_tables += ["DSIG"]
    sub = subset.Subsetter(options=opts)
    sub.populate(unicodes=[ord(c) for c in chars])
    sub.subset(font)

    print("    3) 改名（OFL 保留字体名合规）…")
    rename(font, style)
    font["OS/2"].usWeightClass = weight

    os.makedirs(OUTDIR, exist_ok=True)
    out = os.path.join(OUTDIR, f"{PREFIX}-{style}.otf")
    font.save(out)
    size = os.path.getsize(out) / 1048576
    print(f"    ✅ {os.path.basename(out)}  {size:.2f} MB")
    return out


def main() -> int:
    if not os.path.exists(SRC):
        print(f"缺少源字体：{SRC}")
        print("请先执行 tools/fetch_font.ps1 或按 docs 里的命令下载 @fontpkg/source-han-sans-sc-vf")
        return 1

    chars = base_chars() | gb2312_chars() | project_chars()
    chars = {c for c in chars if c.strip()}
    print(f"字符集合计 {len(chars)} 个（基础符号 + GB2312 + 项目出现过的字符）")

    build(400, "Regular", chars)
    build(600, "SemiBold", chars)
    return 0


if __name__ == "__main__":
    sys.exit(main())
