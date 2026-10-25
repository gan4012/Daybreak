#!/usr/bin/env python3
"""
生成单区块审阅页:从 index.html 里抽出某个 section，单独成页。

为什么需要:无头 Chrome 在带 scroll-behavior:smooth 的页面里不会自动滚到锚点，
整页截图又会被 100svh 的 hero 撑满。所以为了审阅每个区块的排版，
直接给它做一个只含该区块的临时页面。产物在 _preview/，不进部署。

用法: python make-review-pages.py
"""
import pathlib
import re
import shutil

ROOT = pathlib.Path(__file__).resolve().parent.parent
PREVIEW = ROOT / "_preview"
INDEX = ROOT / "index.html"


def main() -> int:
    html = INDEX.read_text(encoding="utf-8")
    PREVIEW.mkdir(exist_ok=True)

    def fix_paths(s: str) -> str:
        """审阅页放在 _preview/ 子目录，相对路径要回退一级。"""
        return (
            s.replace('href="assets/', 'href="../assets/')
             .replace('src="assets/', 'src="../assets/')
             .replace('src="screenshots/', 'src="../screenshots/')
             .replace('url("font/', 'url("../assets/font/')
        )

    head_match = re.search(r"<head>(.*?)</head>", html, re.S)
    head = head_match.group(1) if head_match else ""
    head = fix_paths(head)

    # 抽出所有 section
    sections = re.findall(
        r'(<section class="section[^"]*"[^>]*id="([^"]+)"[^>]*>.*?</section>)',
        html, re.S,
    )
    if not sections:
        print("没找到 section")
        return 1

    for block, sid in sections:
        page = f"""<!DOCTYPE html>
<html lang="zh-CN"><head>{head}
<style>
  .hero, .nav {{ display: none !important; }}
  .section {{ padding-top: 48px; }}
</style>
</head><body>{fix_paths(block)}
<script src="../assets/config.js"></script>
<script src="../assets/app.js"></script>
</body></html>"""
        out = PREVIEW / f"review-{sid}.html"
        out.write_text(page, encoding="utf-8")
        print(f"生成: {out.name}")

    # 顺便做一份整页审阅(hero 隐藏)
    mobile = re.sub(r"<section class=\"hero\">.*?</section>", "", html, flags=re.S)
    mobile = fix_paths(mobile).replace(
        "</head>",
        "<style>.nav{display:none}.hero{display:none}</style></head>"
    )
    (PREVIEW / "review-all.html").write_text(mobile, encoding="utf-8")
    print("生成: review-all.html")

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
