"""
下载思源黑体 SC 可变字体到 tools/fontsrc/。

为什么单独一个脚本：原始字体包解包后约 50MB，不适合进 git 仓库。
仓库里只保留**生成好的子集字体**（MdSnap/Assets/Fonts/*.otf，各约 4MB），
需要从头重建字体时执行：

    python tools/fetch_font.py     # 本脚本：下载源字体
    python tools/make_font.py      # 子集化 + 按 OFL 要求改名

依赖：pip install fonttools brotli  （make_font.py 需要）

字体授权：SIL Open Font License 1.1，允许商用、修改与随软件分发。
原始字体版权声明含保留字体名 'Source'，因此生成的字形子集已按要求改名。
详见 README 的「字体与许可」一节。
"""
import io
import json
import os
import sys
import tarfile
import urllib.request

PKG = "@fontpkg/source-han-sans-sc-vf"
# 用国内镜像：GitHub 直连在中国大陆不稳定，npmmirror 可达
MIRROR = "https://registry.npmmirror.com"
DEST = os.path.join(os.path.dirname(os.path.abspath(__file__)), "fontsrc")
EXPECTED = os.path.join(DEST, "package", "SourceHanSansSC-VF.otf")


def fetch(url: str) -> bytes:
    req = urllib.request.Request(url, headers={"User-Agent": "MdSnap-build/1.0"})
    with urllib.request.urlopen(req, timeout=900) as resp:
        return resp.read()


def main() -> int:
    os.makedirs(DEST, exist_ok=True)

    print(f"[1/3] 查询版本：{MIRROR}/{PKG}/latest")
    try:
        meta = json.loads(fetch(f"{MIRROR}/{PKG}/latest"))
    except Exception as exc:
        print(f"❌ 查询失败：{exc}")
        print("   如果你在墙外，也可以直接从别处获取 SourceHanSansSC-VF.otf，")
        print(f"   放到 {EXPECTED} 即可。")
        return 1

    url = meta["dist"]["tarball"]
    print(f"[2/3] 下载：{url}")
    raw = fetch(url)
    print(f"      完成，{len(raw) / 1048576:.1f} MB")

    print("[3/3] 解包…")
    with tarfile.open(fileobj=io.BytesIO(raw), mode="r:gz") as tf:
        tf.extractall(DEST)

    if not os.path.exists(EXPECTED):
        print(f"❌ 解包后没找到 {os.path.basename(EXPECTED)}")
        return 1

    print(f"✅ 就绪：{EXPECTED}  ({os.path.getsize(EXPECTED) / 1048576:.1f} MB)")
    print("   下一步执行：python tools/make_font.py")
    return 0


if __name__ == "__main__":
    sys.exit(main())
