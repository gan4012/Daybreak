#!/bin/bash
# 更新 Daybreak 官网的 nginx 配置
set -e

CONF=/etc/nginx/sites-available/daybreak

sudo tee "$CONF" > /dev/null <<'NGINX'
server {
    listen 80 default_server;
    listen [::]:80 default_server;
    server_name _;

    root /var/www/daybreak;
    index index.html;

    charset utf-8;
    server_tokens off;

    # 安全响应头
    add_header X-Content-Type-Options "nosniff" always;
    add_header X-Frame-Options "SAMEORIGIN" always;
    add_header Referrer-Policy "strict-origin-when-cross-origin" always;

    # HTML 不缓存，改完立刻生效
    location = /index.html {
        add_header Cache-Control "no-cache, must-revalidate" always;
        add_header X-Content-Type-Options "nosniff" always;
        add_header X-Frame-Options "SAMEORIGIN" always;
        add_header Referrer-Policy "strict-origin-when-cross-origin" always;
    }

    # 静态资源分级缓存
    location /assets/font/ {
        add_header Cache-Control "public, max-age=31536000, immutable" always;
    }
    location /screenshots/ {
        add_header Cache-Control "public, max-age=604800" always;
    }
    location /assets/ {
        add_header Cache-Control "public, max-age=3600" always;
    }

    gzip on;
    gzip_vary on;
    gzip_min_length 1024;
    gzip_types text/plain text/css application/javascript application/json image/svg+xml font/woff2;

    # 关键:这个站是纯静态的，没有前端路由。
    # 之前写成 try_files $uri $uri/ /index.html，导致任何不存在的文件都返回首页 + 200，
    # 是典型的"软 404"——搜索引擎会当成重复内容，也让人以为文件还在。
    location / {
        try_files $uri $uri/ =404;
    }

    error_page 404 /404.html;
    location = /404.html {
        internal;
    }
}
NGINX

sudo ln -sf "$CONF" /etc/nginx/sites-enabled/daybreak
sudo nginx -t
sudo systemctl reload nginx

echo "--- 自测 ---"
curl -sS -o /dev/null -w "首页:           HTTP %{http_code}\n" http://127.0.0.1/
curl -sS -o /dev/null -w "robots.txt:     HTTP %{http_code}\n" http://127.0.0.1/robots.txt
curl -sS -o /dev/null -w "不存在的页面:    HTTP %{http_code}  (应该是 404)\n" http://127.0.0.1/no-such-page
curl -sS -o /dev/null -w "css:            HTTP %{http_code}\n" http://127.0.0.1/assets/style.css
echo "--- 安全头 ---"
curl -sSI http://127.0.0.1/ | grep -iE "x-content-type|x-frame|referrer" || echo "(未生效)"
echo "--- 404 页面内容 ---"
curl -sS http://127.0.0.1/no-such-page | grep -o "<title>.*</title>"