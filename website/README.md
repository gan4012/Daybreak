# Daybreak 官网

静态站点，纯 HTML/CSS/JS，没有框架也没有构建步骤。nginx 直接托管。

## 目录

```
website/
├── index.html              页面结构
├── assets/
│   ├── style.css           设计 token + 布局 + 动效
│   ├── app.js              应用下载配置、导航滚动态
│   ├── config.js           ★ 下载链接在这里改
│   ├── logo.svg            favicon
│   └── font/
│       └── smiley-sans-subset.woff2   得意黑子集(63KB)
├── screenshots/            从真实程序截的图
├── scripts/
│   ├── subset-font.py      字体子集化
│   ├── make-review-pages.py 生成单区块审阅页
│   ├── measure.mjs         CDP 测量布局宽度
│   └── shoot.mjs           CDP 精确截图
└── deploy/deploy.ps1       部署脚本(server.txt 不进 git)
```

## 常见改动

### 改下载链接（最常做）

只改 `assets/config.js`：

```js
window.DAYBREAK = {
    version: "v0.1.2",
    size: "96.6 MB",
    links: {
        netdisk: "",          // 填上网盘直链，按钮自动变成可点的主按钮
        netdiskCode: "",      // 提取码，会显示在按钮右侧
        netdiskNote: ""       // 说明，比如「蓝奏云，无需提取码」
    }
};
```

`netdisk` 留空时，页面上显示「即将上线」且不可点。填了就直接生效，**改完重新部署即可**。

### 改文案

直接改 `index.html`。如果加了页面原来没有的汉字，**必须重跑字体子集**（否则新字会回退到系统字体，看着突兀）：

```powershell
# 1. 下载得意黑(OFL): https://github.com/atelier-anchor/smiley-sans/releases
# 2. 重跑子集
python scripts/subset-font.py <SmileySans-Oblique.ttf> --html index.html --out assets/font/smiley-sans-subset.woff2
```

### 更新截图

先按仓库根 `AGENTS.md` 里的流程启动程序，然后用 `capture-safe.ps1` 截图：

```powershell
powershell -File ..\capture-safe.ps1 -OutFile app.png
```

> **注意**：不要用旧的 `capture.ps1`。它只按窗口矩形抓屏，如果别的窗口压在启动器上面，
> 会把别人的内容一起拍进来（实机上曾经截到用户的私人网页）。`capture-safe.ps1` 会先
> 强制置顶并校验前台窗口，不满足就直接拒绝截图。

导航到具体页面的控件用 `uia-nav.ps1 -name "联机"`（按显示文本找，再向上找可选择的祖先）。

### 部署

```powershell
# 首次：把公钥加到服务器
#   ~/.ssh/daybreak_deploy.pub 内容追加到服务器的 ~/.ssh/authorized_keys
# 然后写 server.txt（不进 git）
"ubuntu@你的服务器" | Out-File website\deploy\server.txt -Encoding ASCII -NoNewline

powershell -File website/deploy/deploy.ps1
```

服务器地址也可以走环境变量 `DAYBREAK_SERVER`，避免在本地留文件。

## 为什么是现在这个设计

不是随手做的，是先调研了三个前端设计资源再定的方案：

- [Anthropic `frontend-design`](https://github.com/anthropics/skills/tree/main/skills/frontend-design)
  —— 设计方法论，以及"AI 生成页面"的五类典型味道
- [Emil Kowalski 的动效技能](https://github.com/emilkowalski/skills)
  —— 什么时候该动、用什么缓动、多长时长
- [Impeccable](https://github.com/pbakaus/impeccable)
  —— 62 条针对 AI 生成前端的确定性检测规则

**主题「地平线」**：破晓不是夜也不是昼，是天刚亮那一瞬。所以全站有一条贯穿的天地交界线，
hero 的启动器截图立在地平线上，像太阳升起。

**刻意避开的套路**（都是上面那些资源点名过的）：

| 套路 | 本站做法 |
|---|---|
| 圆角方块图标 + 标题 | 没有图标块，功能用编辑式两栏排版 |
| 一模一样圆角卡片墙 | 细线分隔的非对称布局 |
| `A · B · C` 中点连接 | 不用中点 |
| 每个区块 fade-in + 每张卡 hover 位移 | 只有一次入场编排 |
| `ease-in` 缓动 | 自定义 `cubic-bezier(0.23, 1, 0.32, 1)` |
| 全大写追踪字距小标签 | 不用 |
| 渐变只给标题半句 | 整句一个处理 |

**唯一的"亮点"是地平线上的初光**，其余都压暗（Chanel 那句"出门前摘掉一件配饰"）。

## 工具说明

`measure.mjs` 和 `shoot.mjs` 走 CDP（Chrome DevTools Protocol），不是 `chrome --screenshot`。

原因：**Windows 版 Chrome 的窗口有最小宽度**（约 500px），`--window-size=390` 会被夹紧，
页面按更大宽度排版后再裁到 390，看起来像"文字被切掉"，其实是截图方式错了。
CDP 的 `Emulation.setDeviceMetricsOverride` 才能真正把布局视口设成 390。

```powershell
node scripts/measure.mjs http://127.0.0.1:8899/ 390 844   # 检查是否横向溢出
node scripts/shoot.mjs  http://127.0.0.1:8899/ 390 844 out.png --mobile
```

## 以后加 HTTPS

现在只有 IP，所以是 HTTP。有域名之后：

```bash
sudo apt-get install -y certbot python3-certbot-nginx
sudo certbot --nginx -d 你的域名
```

certbot 会自动改 nginx 配置并配置续期。届时把 `index.html` 里的链接、以及启动器
`config.js` 之外的更新源地址一并改成 https。
