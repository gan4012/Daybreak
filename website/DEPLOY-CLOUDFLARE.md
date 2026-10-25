# 部署到 Cloudflare Pages（免备案）

## 为什么这么做

腾讯云备案要求轻量应用服务器 **累计购买时长 ≥ 3 个月**（见[官方文档](https://cloud.tencent.com/document/product/243/18908)），
不足三个月无法备案。而备案只针对「用**域名**在**境内**提供网站服务」。

所以拆开处理：

| 用途 | 放在哪 | 需要备案吗 |
|---|---|---|
| 官网（域名 + HTTPS） | Cloudflare Pages（境外） | ❌ 不需要 |
| 联机中继（TCP/UDP 11010） | 腾讯云服务器（IP 直连） | ❌ 不需要 |

**中继节点本来就不需要备案** —— 它不是网站服务，不走 80/443。

## 前置条件

- 一个 Cloudflare 账号（免费）
- 一个域名（Cloudflare 免费套餐即可，不要求把 NS 迁过去，CNAME 接入也行）
- 本仓库的 GitHub 访问权限（用 Git 集成的话）

---

## 方式一：Git 集成（推荐，以后 push 自动部署）

1. 登录 [Cloudflare Dashboard](https://dash.cloudflare.com/) → 左侧 **Workers & Pages** → **Create** → **Pages** → **Connect to Git**
2. 授权 GitHub，选择 `gan4012/Daybreak` 仓库
3. 构建设置留空，**只改一项**：

   | 配置项 | 值 |
   |---|---|
   | Framework preset | `None` |
   | Build command | （留空） |
   | **Build output directory** | **`website`** |
   | Root directory | `/` |

4. 点 **Save and Deploy**，等 30 秒左右

之后每次 `git push` 到 `main`，Cloudflare 会自动重新部署。

> 部署只会上传 `website/` 目录，并且按 `.assetsignore` 排除 `scripts/`、`deploy/`、
> `_preview/` 和 `*.md`。开发脚本不会跑到线上。

## 方式二：直接上传（不想连 Git 时用）

```powershell
# 安装 wrangler
npm install -g wrangler

# 登录(会打开浏览器授权)
wrangler login

# 部署(注意目录是 website)
wrangler pages deploy website --project-name=daybreak
```

首次执行会让你创建项目。之后再跑同一条命令就是更新。

---

## 绑定域名

部署成功后会先拿到一个 `https://daybreak-xxx.pages.dev` 地址，能直接用。
绑自己的域名：

1. Cloudflare Pages 项目 → **Custom domains** → **Set up a custom domain**
2. 输入你的域名，比如 `daybreak.example.com`
3. 如果域名就在 Cloudflare 托管：自动加记录，直接生效
4. 如果域名在别处：按提示去域名商加一条 **CNAME** 指向 `daybreak-xxx.pages.dev`

HTTPS 证书由 Cloudflare **自动签发并续期**，不用管。

---

## 验证清单

部署完请逐项确认：

- [ ] 首页能打开，字体是得意黑（标题那种紧凑斜体）
- [ ] 三张截图都能显示
- [ ] 「网盘下载」按钮可以点，跳到蓝奏云，提取码 `62jd`
- [ ] 手机上打开排版正常（无横向滚动）
- [ ] 浏览器地址栏是 HTTPS 锁标

---

## 和国内 IP 站的关系

`http://<服务器IP>/` 上的同一份站点**继续保留**，作为备用入口：

- 万一 Cloudflare 在国内访问不畅，还能用 IP
- 两处内容一致，都来自 `website/` 目录

需要注意：**用 IP 直接提供网站服务在国内属于灰色地带**，运营商理论上可以要求整改。
所以定位应该是「临时备用」，主站还是走 Cloudflare + 域名。

## 国内访问不理想时怎么办

Cloudflare 免费套餐在国内的连通性时好时坏。如果反馈明显慢，有三条路：

1. **保持现状** —— 静态页面首屏只有约 84 KB，多数情况下可接受
2. **加国内加速** —— 需要备案，回到方案 B
3. **换托管** —— 但 Vercel / Netlify / GitHub Pages 在国内通常更慢，不建议

## 以后要做的事

- 拿到稳定域名后，可以把 `index.html` 里指向 Gitee/GitHub 的链接改成更亲切的站内页
- 加一个 `sitemap.xml`（目前内容少，暂时没必要）
