# -*- coding: utf-8 -*-
"""按分镜把素材拼成粗剪。

做两版：
    rough-16x9.mp4   B站横屏
    rough-9x16.mp4   抖音竖屏

配音用 tts.ps1 生成的那条，字幕单独给 SRT（不烧进画面，方便在剪映里改样式）。

每句旁白对应一个片段，片段短了就放慢/循环补足，长了就裁掉 —— 保证画面和配音严丝合缝。
"""
import os
import re
import subprocess
import sys

ROOT = r"D:\ZCode-Data\Daybreak\docs\video"
CLIPS = os.path.join(ROOT, "clips")
VOICE_DIR = os.path.join(ROOT, "配音输出-快")     # +20% 语速，抖音用
OUT_DIR = os.path.join(ROOT, "粗剪")

FFMPEG = None
for base in [
    r"C:\Users\32109\AppData\Local\Microsoft\WinGet\Packages",
]:
    for dirpath, _, files in os.walk(base):
        if "ffmpeg.exe" in files:
            FFMPEG = os.path.join(dirpath, "ffmpeg.exe")
            break
    if FFMPEG:
        break

if not FFMPEG:
    sys.exit("找不到 ffmpeg")

# 分镜：每句旁白 → 用哪些片段（多个片段平分这句的时长）
SHOTS = [
    ["E5-房间号"],                       # 1 不用公网IP，6位数字就能联机
    ["T1-文字卡1"],                      # 2 以前要么花钱租服务器
    ["T2-文字卡2"],                      # 3 要么折腾端口映射
    ["T3-文字卡3"],                      # 4 要么只能同一个WiFi
    ["E1-好友联机页"],                   # 5 点一下创建房间
    ["E3-提权提示"],                     # 6 授权管理员权限
    ["E5-房间号"],                       # 7 自动生成六位房间号
    ["E6-房间信息"],                     # 8 朋友输入房间号加入
    ["E6-房间信息"],                     # 9 点启动游戏并进服
    ["E5-房间号"],                       # 10 直接连进去
    ["D3-服务端类型"],                   # 11 四种服务端一键开服
    ["G2-搜索Mod", "C1-版本管理", "I1-设置页"],   # 12 Mod市场/版本隔离/诊断/主题
    ["A1-首页"],                         # 13 破晓启动器，免费下载
]


def parse_srt(path):
    """返回 [(start, end, text), ...]"""
    with open(path, encoding="utf-8-sig") as f:
        raw = f.read()
    out = []
    for block in re.split(r"\r?\n\r?\n", raw.strip()):
        lines = [l for l in block.splitlines() if l.strip()]
        if len(lines) < 3:
            continue
        m = re.match(r"(\d+):(\d+):(\d+),(\d+)\s*-->\s*(\d+):(\d+):(\d+),(\d+)", lines[1])
        if not m:
            continue
        g = [int(x) for x in m.groups()]
        start = g[0] * 3600 + g[1] * 60 + g[2] + g[3] / 1000
        end = g[4] * 3600 + g[5] * 60 + g[6] + g[7] / 1000
        out.append((start, end, " ".join(lines[2:])))
    return out


def probe_size(path):
    """返回素材的 (宽, 高)。"""
    probe = FFMPEG.replace("ffmpeg.exe", "ffprobe.exe")
    out = subprocess.run(
        [probe, "-v", "error", "-select_streams", "v:0",
         "-show_entries", "stream=width,height", "-of", "csv=p=0", path],
        capture_output=True).stdout.decode().strip()
    w, h = out.split(",")[:2]
    return int(w), int(h)


def run(args):
    r = subprocess.run(args, capture_output=True)
    if r.returncode != 0:
        print(r.stderr.decode("utf-8", "ignore")[-800:])
        raise SystemExit("ffmpeg 失败")


def make_segment(clip, duration, out, canvas_w, canvas_h, anchor, vertical=False):
    """把一个片段处理成固定时长、画布尺寸统一的中间文件。

    anchor: 0.0=顶部对齐, 0.5=垂直居中, 1.0=底部对齐
    素材不够长会自动循环，保证输出长度精确等于 duration。
    """
    src = os.path.join(CLIPS, clip + ".mp4")
    if not os.path.exists(src):
        raise SystemExit(f"缺素材: {src}")

    # 前景：等比缩放到能放进画布，再按 anchor 摆放
    fg = (
        f"scale={canvas_w}:{canvas_h}:force_original_aspect_ratio=decrease,"
        f"pad={canvas_w}:{canvas_h}:(ow-iw)/2:(oh-ih)*{anchor}:color=0x0E1220"
    )

    base = [
        FFMPEG, "-y", "-v", "error",
        "-stream_loop", "-1",
        "-i", src,
        "-t", f"{duration:.3f}",
        "-r", "30",
        "-c:v", "libx264", "-preset", "veryfast", "-crf", "20",
        "-pix_fmt", "yuv420p",
        "-an",
    ]

    if vertical:
        # 竖屏：把同一画面放大裁切、模糊当背景，前景原比例叠上去。
        # 横屏录屏放竖屏只留黑边会很空，这是通行处理。
        #
        # 三个必须注意的点:
        #  1. split 分两路必须走 -filter_complex，-vf 处理不了多输出
        #  2. 前景 pad 必须填「透明色」并先转 rgba。
        #     之前填不透明色把前景撑满整画布，overlay 上去就把背景整个盖掉了，
        #     表现是"模糊背景没生效"，其实是前景不透明。
        #  3. 录屏里左侧 240px 是导航栏，竖屏下很占地方，先裁掉再缩放，
        #     主画面能大出约 20%。文字卡是 1920 宽的，不能裁。
        src_w, _src_h = probe_size(src)
        pre = "crop=iw-240:ih:240:0," if src_w <= 1280 else ""

        bg = (
            f"{pre}scale={canvas_w}:{canvas_h}:force_original_aspect_ratio=increase,"
            f"crop={canvas_w}:{canvas_h},gblur=sigma=38,"
            f"eq=brightness=-0.18:saturation=1.05"
        )
        fg = (
            f"{pre}scale={canvas_w}:{canvas_h}:force_original_aspect_ratio=decrease,"
            f"format=rgba,"
            f"pad={canvas_w}:{canvas_h}:(ow-iw)/2:(oh-ih)*{anchor}:color=0x00000000"
        )
        fc = (
            f"[0:v]split=2[a][b];"
            f"[a]{bg}[bg];"
            f"[b]{fg}[fg];"
            f"[bg][fg]overlay=0:0:format=auto,format=yuv420p[v]"
        )
        run(base + ["-filter_complex", fc, "-map", "[v]", out])
    else:
        fg = (
            f"scale={canvas_w}:{canvas_h}:force_original_aspect_ratio=decrease,"
            f"pad={canvas_w}:{canvas_h}:(ow-iw)/2:(oh-ih)*{anchor}:color=0x0E1220"
        )
        run(base + ["-vf", fg, out])


def build(voice, srt, out_path, w, h, label):
    segs = parse_srt(srt)
    if len(segs) != len(SHOTS):
        raise SystemExit(f"字幕 {len(segs)} 条，但分镜表 {len(SHOTS)} 条，对不上")

    tmp = os.path.join(OUT_DIR, "_seg")
    os.makedirs(tmp, exist_ok=True)

    # 画面尺寸:横屏铺满；竖屏把录屏放在画布中部偏上，下方留字幕位
    # 横屏居中；竖屏偏上，给底部字幕留位置
    anchor = 0.5 if w / h > 1.2 else 0.12

    parts = []
    print(f"==> {label}")

    # 每段时长要算到「下一段的开始」，而不是只算旁白本身。
    # 否则句间的停顿被丢掉，累加起来比配音短，最后一句会被吃掉。
    probe = FFMPEG.replace("ffmpeg.exe", "ffprobe.exe")
    total_voice = float(subprocess.run(
        [probe, "-v", "error", "-show_entries", "format=duration",
         "-of", "csv=p=0", voice],
        capture_output=True).stdout.decode().strip())

    bounds = []
    for i, (start, _e, _t) in enumerate(segs):
        nxt = segs[i + 1][0] if i + 1 < len(segs) else total_voice
        bounds.append(nxt - start)

    for i, ((start, end, text), clips) in enumerate(zip(segs, SHOTS)):
        total = bounds[i]
        each = total / len(clips)
        for j, c in enumerate(clips):
            p = os.path.join(tmp, f"{i:02d}_{j}.mp4")
            make_segment(c, each, p, w, h, anchor, vertical=(w / h < 1.2))
            parts.append(p)
        head = text[:26] + ("…" if len(text) > 26 else "")
        print(f"    {i+1:2d}. {total:5.2f}s  {head}")

    # 拼接画面
    listfile = os.path.join(tmp, "_list.txt")
    with open(listfile, "w", encoding="utf-8") as f:
        for p in parts:
            f.write(f"file '{p.replace(chr(92), '/')}'\n")

    silent = os.path.join(tmp, "_video.mp4")
    run([FFMPEG, "-y", "-v", "error", "-f", "concat", "-safe", "0",
         "-i", listfile, "-c", "copy", silent])

    # 合上配音
    run([FFMPEG, "-y", "-v", "error",
         "-i", silent, "-i", voice,
         "-map", "0:v:0", "-map", "1:a:0",
         "-c:v", "copy", "-c:a", "aac", "-b:a", "192k",
         "-shortest", "-movflags", "+faststart",
         out_path])

    dur = subprocess.run(
        [FFMPEG.replace("ffmpeg.exe", "ffprobe.exe"), "-v", "error",
         "-show_entries", "format=duration", "-of", "csv=p=0", out_path],
        capture_output=True).stdout.decode().strip()
    size = os.path.getsize(out_path) / 1024 / 1024
    print(f"    → {os.path.basename(out_path)}  {float(dur):.1f}s  {size:.1f} MB")


if __name__ == "__main__":
    os.makedirs(OUT_DIR, exist_ok=True)
    voice = os.path.join(VOICE_DIR, "配音.mp3")
    srt = os.path.join(VOICE_DIR, "字幕.srt")
    if not os.path.exists(voice):
        raise SystemExit(f"缺配音: {voice}\n先跑 tools/tts.ps1")

    build(voice, srt, os.path.join(OUT_DIR, "rough-16x9.mp4"), 1920, 1080, "横屏 B站版")
    build(voice, srt, os.path.join(OUT_DIR, "rough-9x16.mp4"), 1080, 1920, "竖屏 抖音版")
    print(f"\n粗剪完成 → {OUT_DIR}")