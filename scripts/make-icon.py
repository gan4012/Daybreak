#!/usr/bin/env python3
"""
生成 Daybreak 应用图标(.ico)。

设计沿用官网的「地平线」语言:一轮太阳刚从地平线上升起。
不用 Minecraft 的任何图形元素(草方块、苦力怕脸等)，避免商标问题。

做法:在 1024 的超采样画布上绘制，再 LANCZOS 降采样到各档尺寸，
保证 16x16 的任务栏尺寸下依然干净。

用法:
    python scripts/make-icon.py --out Daybreak.Launcher/daybreak.ico

依赖: pip install Pillow
"""
import argparse
import math
import pathlib
import sys

try:
    from PIL import Image, ImageDraw, ImageFilter
except ImportError:
    sys.exit("缺少 Pillow。先执行: pip install Pillow")

S = 1024                      # 超采样画布尺寸
HORIZON_Y = int(S * 0.630)    # 地平线位置
SUN_R = int(S * 0.278)        # 太阳半径
CORNER = int(S * 0.185)       # 圆角半径

# 品牌色(与启动器 Brand.xaml / 官网一致)
NIGHT = (11, 14, 26)
NIGHT_TOP = (20, 25, 44)
EMBER = (255, 138, 61)
ROSE = (244, 81, 127)
DAYBREAK = (255, 232, 196)
GOLD = (255, 198, 77)


def vgrad(size, top, bottom):
    """竖直渐变。"""
    w, h = size
    img = Image.new("RGB", (1, h))
    px = img.load()
    for y in range(h):
        t = y / max(1, h - 1)
        px[0, y] = (
            round(top[0] + (bottom[0] - top[0]) * t),
            round(top[1] + (bottom[1] - top[1]) * t),
            round(top[2] + (bottom[2] - top[2]) * t),
        )
    return img.resize((w, h), Image.BILINEAR)


def rounded_mask(size, radius):
    m = Image.new("L", size, 0)
    d = ImageDraw.Draw(m)
    d.rounded_rectangle([0, 0, size[0] - 1, size[1] - 1], radius=radius, fill=255)
    return m


def build(small: bool = False) -> Image.Image:
    """设计要点(小尺寸优先):
    16px 的任务栏图标只有 2~3 个形状能被看清，所以只保留三个元素 ——
    深色圆角底、一轮贴着地平线的太阳、一条亮地平线。

    small=True 时给 32px 以下单独出一版:太阳更大、地平线更粗、光晕更弱。
    直接缩小会让细节糊成一团，所以小尺寸需要另一套参数(图标设计里的 hinting)。
    """
    sun_r = int(S * (0.315 if small else 0.278))
    horizon_y = int(S * (0.615 if small else 0.630))
    corner = int(S * (0.20 if small else 0.185))
    line_thickness = max(8, int(S * (0.062 if small else 0.038)))
    glow_alpha = 34 if small else 58
    glow_radius = 1.18 if small else 1.34
    glow_blur = 0.020 if small else 0.030
    margin = int(S * (0.045 if small else 0.075))
    shade_alpha = 150 if small else 120

    canvas = Image.new('RGBA', (S, S), (0, 0, 0, 0))

    bg = vgrad((S, S), NIGHT_TOP, NIGHT).convert('RGBA')
    canvas.paste(bg, (0, 0), rounded_mask((S, S), corner))

    sun_cx = S // 2
    sun_cy = horizon_y - sun_r

    glow = Image.new('RGBA', (S, S), (0, 0, 0, 0))
    gr = int(sun_r * glow_radius)
    ImageDraw.Draw(glow).ellipse(
        [sun_cx - gr, sun_cy - gr, sun_cx + gr, sun_cy + gr],
        fill=(*EMBER, glow_alpha),
    )
    glow = glow.filter(ImageFilter.GaussianBlur(S * glow_blur))
    canvas = Image.alpha_composite(canvas, glow)

    d2 = sun_r * 2
    sun = vgrad((d2, d2), DAYBREAK, EMBER).convert('RGBA')
    mask = Image.new('L', (d2, d2), 0)
    ImageDraw.Draw(mask).ellipse([0, 0, d2 - 1, d2 - 1], fill=255)
    canvas.paste(sun, (sun_cx - sun_r, sun_cy - sun_r), mask)

    line = Image.new('RGBA', (S, S), (0, 0, 0, 0))
    ld = ImageDraw.Draw(line)
    seg = 260
    x0, x1 = margin, S - margin
    for i in range(seg):
        a = x0 + (x1 - x0) * i / seg
        b = x0 + (x1 - x0) * (i + 1) / seg
        t = i / (seg - 1)
        fade = 1 - abs(t - 0.5) * (1.0 if small else 1.35)
        alpha = int(255 * max(0.35 if small else 0.12, min(1.0, fade)))
        ld.rectangle(
            [a, horizon_y - line_thickness // 2, b + 1, horizon_y + line_thickness // 2],
            fill=(*GOLD, alpha),
        )
    canvas = Image.alpha_composite(canvas, line)

    shade = Image.new('RGBA', (S, S), (0, 0, 0, 0))
    ImageDraw.Draw(shade).rectangle(
        [0, horizon_y + line_thickness // 2, S, S], fill=(*NIGHT, shade_alpha))
    canvas = Image.alpha_composite(canvas, shade)

    refl = Image.new('RGBA', (S, S), (0, 0, 0, 0))
    rw = int(sun_r * 0.62)
    ry = horizon_y + line_thickness // 2
    ImageDraw.Draw(refl).ellipse(
        [sun_cx - rw, ry, sun_cx + rw, ry + int(sun_r * 0.42)],
        fill=(*EMBER, 110),
    )
    refl = refl.filter(ImageFilter.GaussianBlur(S * 0.018))
    canvas = Image.alpha_composite(canvas, refl)

    out = Image.new('RGBA', (S, S), (0, 0, 0, 0))
    out.paste(canvas, (0, 0), rounded_mask((S, S), corner))
    return out


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--out", default="Daybreak.Launcher/daybreak.ico")
    ap.add_argument("--png", default="", help="同时导出一张 256 的 PNG(可选)")
    args = ap.parse_args()

    big = build(small=False)
    small = build(small=True)

    def art_for(size: int) -> Image.Image:
        """<=32px 用加强版，其余用标准版。"""
        return small if size <= 32 else big

    out = pathlib.Path(args.out)
    out.parent.mkdir(parents=True, exist_ok=True)

    # Windows 图标常用尺寸。16/20/24 是标题栏与任务栏，32/48 是资源管理器，256 是大图标视图
    sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256]

    frames = []
    for s in sizes:
        frames.append(art_for(s).resize((s, s), Image.LANCZOS))

    # Pillow 的 ICO 写入器只接受一个主图 + sizes，内部重采样质量一般，
    # 所以这里手动构造：把 256 当主图，其余尺寸作为独立帧塞进 ICO。
    big.resize((256, 256), Image.LANCZOS).save(
        out,
        format="ICO",
        sizes=[(s, s) for s in sizes],
        append_images=frames[:-1],
    )

    print(f"已生成 {out}  ({out.stat().st_size / 1024:.1f} KB)")

    if args.png:
        png = pathlib.Path(args.png)
        png.parent.mkdir(parents=True, exist_ok=True)
        big.resize((256, 256), Image.LANCZOS).save(png)
        print(f"已生成 {png}")

    # 顺便导出各尺寸便于目视检查
    preview_dir = out.parent / "_icon-preview"
    preview_dir.mkdir(exist_ok=True)
    for s in sizes:
        art_for(s).resize((s, s), Image.LANCZOS).save(preview_dir / f"icon-{s}.png")
    print(f"预览帧已导出到 {preview_dir}")

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
