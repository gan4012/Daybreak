# 视频工具链

两个脚本，把「录屏」和「配音+字幕」这两件机械活自动化。

## 依赖（已装好）

| 工具 | 版本 | 装在哪 |
|---|---|---|
| ffmpeg / ffprobe | 9.0.2 | winget 装的，已加入用户 PATH |
| edge-tts | 7.3.1 | Python 3.12 (`C:\Users\32109\AppData\Local\Programs\Python\Python312`) |

重装命令：

```powershell
winget install --id Gyan.FFmpeg -e --accept-package-agreements --accept-source-agreements
pip install edge-tts
```

> **注意**：ffmpeg 是通过 winget 装的，**已经写进用户 PATH，但已经开着的终端读不到**。
> 新开一个终端就好。脚本里已经做了兜底：PATH 里找不到会去 winget 的安装目录里翻。

---

## tts.ps1 — 配音 + 字幕一键生成

### 为什么这么做

剪映的「文本朗读」是一句一句手动加的，13 句就要点 13 次，而且字幕还要另外识别。
这个脚本直接从文案生成**一整条配音 + 一份严丝合缝的字幕**，拖进剪映就能用。

**关键**：字幕时间轴是按每句配音的**实际时长**累加算出来的，不是估的，
所以导入剪映后不会出现"字幕比声音快半秒"这种问题。

### 用法

```powershell
# 基本用法
powershell -File docs/video/tools/tts.ps1 -ScriptFile docs/video/script-抖音.txt

# 换音色和语速
powershell -File docs/video/tools/tts.ps1 `
    -ScriptFile docs/video/script-抖音.txt `
    -Voice zh-CN-YunjianNeural `
    -Rate "+20%"

# 加大句间停顿（默认 0.45 秒）
powershell -File docs/video/tools/tts.ps1 -ScriptFile xxx.txt -GapSeconds 0.8
```

### 脚本文件格式

纯文本，**一行一句**。空行和 `#` 开头的行会被忽略（可以拿 `#` 写注释）。

```text
# 镜头1 钩子
不用公网IP，输入六位数字，就能和朋友一起玩我的世界。
# 镜头2 痛点
以前想和朋友一起玩，要么花钱租服务器。
```

### 输出

```
配音输出/
├── 配音.mp3    整条配音，48kHz 立体声，句间带停顿
└── 字幕.srt    带 BOM，剪映能正确识别中文
```

### 可选音色

| ID | 名字 | 风格 |
|---|---|---|
| `zh-CN-YunxiNeural` | 云希 | 活泼、阳光（**默认，适合产品演示**） |
| `zh-CN-YunjianNeural` | 云健 | 激情、有力（适合快节奏） |
| `zh-CN-YunyangNeural` | 云扬 | 稳重、新闻腔（适合正经讲解） |
| `zh-CN-XiaoxiaoNeural` | 晓晓 | 温暖女声 |
| `zh-CN-XiaoyiNeural` | 晓伊 | 活泼女声 |
| `zh-CN-YunxiaNeural` | 云夏 | 可爱少年音 |

试听样本在 `docs/video/_samples/`。

### 语速参考

| Rate | 效果 | 适合 |
|---|---|---|
| `+0%` | 正常 | B站长视频 |
| `+10%` | 稍快 | 通用（**默认**） |
| `+20%` | 明显快 | 抖音，同样内容能省 5-8 秒 |
| `+30%` | 很赶 | 不建议，听着累 |

---

## record.ps1 — 录屏

### 为什么不用 OBS / Win+G

**因为窗口遮挡问题。**

用 gdigrab 按屏幕区域采集，如果别的窗口压在启动器上面，**录出来的画面里会混进别人的内容**。
这不是理论风险 —— 之前截图时就误拍到过浏览器里的私人页面。

这个脚本会在录制前把目标窗口**强制置顶**，录完恢复，从根上避免这个问题。
录完还会提示你先播一遍确认画面干净。

### 用法

```powershell
# 录启动器窗口 30 秒，前面留 5 秒准备
powershell -File docs/video/tools/record.ps1 -Window "Daybreak" -Duration 30 -Countdown 5 -Out A1.mp4

# 不限时，按回车停止
powershell -File docs/video/tools/record.ps1 -Window "Daybreak" -Out A1.mp4

# 录整个主屏
powershell -File docs/video/tools/record.ps1 -FullScreen -Duration 20 -Out X.mp4
```

### 参数

| 参数 | 默认 | 说明 |
|---|---|---|
| `-Window` | 必填 | 窗口标题关键词，匹配到就用 |
| `-FullScreen` | | 录整个主屏（代替 `-Window`） |
| `-Duration` | 0 | 录制秒数；0 = 按回车手动停 |
| `-Countdown` | 3 | 开始前倒计时秒数 |
| `-Fps` | 30 | 帧率。软件操作 30 够用 |
| `-Crf` | 18 | 画质。数字越小越清晰、文件越大（18 已经很好） |
| `-Out` | record.mp4 | 输出路径 |

### 建议的录制参数

按分镜脚本的需求：

```powershell
# 前端 UI 操作，需要看清字
-Crf 18 -Fps 30
```

文件大小参考：1280×820 录 6 秒 ≈ 0.5 MB，录 1 分钟 ≈ 5 MB。**不用担心占空间。**

---

## 完整工作流

```powershell
# ① 把文案写进 script-抖音.txt（一行一句）

# ② 生成配音和字幕
powershell -File docs/video/tools/tts.ps1 -ScriptFile docs/video/script-抖音.txt -Rate "+20%"

# ③ 打开启动器，按录制清单逐个录
powershell -File docs/video/tools/record.ps1 -Window "Daybreak" -Duration 8 -Countdown 5 -Out clips\A1.mp4
powershell -File docs/video/tools/record.ps1 -Window "Daybreak" -Duration 10 -Countdown 5 -Out clips\E5.mp4
# …按 01-录制清单.md 走

# ④ 剪映里：导入 配音.mp3 拖到时间轴最左端 → 导入 字幕.srt → 按分镜摆画面
```

---

## 常见问题

**Q: 运行脚本报「找不到 ffmpeg」**
新开一个终端。winget 已经把路径写进用户 PATH，但已开着的终端读不到。
（脚本有兜底，一般不会真的失败。）

**Q: 字幕在剪映里是乱码**
脚本已经写了 BOM，不应该出现。如果真乱码，检查是不是用记事本另存过 ——
记事本会丢 BOM。重新跑一遍脚本即可。

**Q: 配音和字幕对不上**
脚本按实际时长算的，理论上严丝合缝。如果剪映里显示错位，
检查是不是把音频拖到了非 0 秒的位置 —— 字幕是从 0 秒开始的。

**Q: 录出来的画面里有别的窗口**
说明置顶没生效（可能窗口标题没匹配上）。脚本开头会打印匹配到的窗口标题和区域，
看一眼对不对。也可以先手动把启动器点到最前再录。