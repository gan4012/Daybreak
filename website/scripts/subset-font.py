#!/usr/bin/env python3
"""
把得意黑(Smiley Sans)裁成只含本站用到的字符，输出 woff2。

为什么需要:中文字体动辄 5-10MB，直接上网会拖垮首屏。
本站实际用到的汉字大概两三百个，裁完之后只有几十 KB。

用法:
    python subset-font.py <源字体.ttf> [--html index.html] [--out assets/font/smiley-sans-subset.woff2]

依赖: pip install "fonttools[woff]"
"""
import argparse
import pathlib
import re
import sys

try:
    from fontTools import subset
except ImportError:
    sys.exit('缺少 fonttools。先执行: pip install "fonttools[woff]"')


# 基础字符集:数字、英文字母、常用标点，保证数字/英文不会缺字
BASE_CHARS = (
    "0123456789"
    "abcdefghijklmnopqrstuvwxyz"
    "ABCDEFGHIJKLMNOPQRSTUVWXYZ"
    " .:,;!?()[]{}<>/\\|-_=+*&%#@~'\"`^$"
    "，。、；：？！（）【】《》「」『』…—～·“”‘’"
    "　"
)

# 这些字符构不成"可见文字"，但保留它们不会有害
EXTRA = "0123456789"


def collect_chars(html_path: pathlib.Path) -> set[str]:
    """收集页面里出现的所有字符。
    会把 <script>/<style> 里的内容也包含进来 —— 少收几个字比缺字好看得多。"""
    text = html_path.read_text(encoding="utf-8")
    return set(text)


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("source", help="源字体文件(.ttf/.otf)")
    ap.add_argument("--html", default="index.html", help="用来收集字符的页面")
    ap.add_argument("--extra-text", default="", help="额外要包含的文字")
    ap.add_argument("--out", default="assets/font/smiley-sans-subset.woff2")
    args = ap.parse_args()

    src = pathlib.Path(args.source)
    if not src.exists():
        sys.exit(f"找不到源字体: {src}")

    html = pathlib.Path(args.html)
    if not html.exists():
        sys.exit(f"找不到页面: {html}")

    chars = collect_chars(html) | set(BASE_CHARS) | set(args.extra_text)

    # 去掉控制字符
    chars = {c for c in chars if c.isprintable() or c == "　"}

    out = pathlib.Path(args.out)
    out.parent.mkdir(parents=True, exist_ok=True)

    options = subset.Options()
    options.flavor = "woff2"
    options.desubroutinize = True
    # 保留一些 OpenType 表，否则某些渲染器上会掉字形
    options.layout_features = ["*"]
    options.name_IDs = ["*"]
    options.notdef_outline = True

    font = subset.load_font(str(src), options)
    subsetter = subset.Subsetter(options=options)
    subsetter.populate(text="".join(sorted(chars)))
    subsetter.subset(font)
    subset.save_font(font, str(out), options)

    src_kb = src.stat().st_size / 1024
    out_kb = out.stat().st_size / 1024
    print(f"字符数: {len(chars)}")
    print(f"源字体: {src.name}  {src_kb:.0f} KB")
    print(f"子集:   {out}  {out_kb:.1f} KB  ({(1 - out_kb / src_kb) * 100:.1f}% 压缩)")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
