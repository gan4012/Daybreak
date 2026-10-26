#!/bin/bash
# 香港服务器上配置 dayberak.top 的 nginx 站点
set -e

CONF=/etc/nginx/sites-available/daybreak

sudo tee "$CONF" > /dev/null <<'NGINX'
server {
    listen 80;
    listen [::]:80;
    server_name dayberak.top www.dayberak.top;

    root /var/www/daybreak;
    index index.html;

    charset utf-8;
    server_tokens off;

    # Let's Encrypt 的 HTTP-01 校验走这里
    location /.well-known/acme-challenge/ {
        root /var/www/daybreak;
        allow all;
    }

    add_header X-Content-Type-Options "nosniff" always;
    add_header X-Frame-Options "SAMEORIGIN" always;
    add_header Referrer-Policy "strict-origin-when-cross-origin" always;

    location = /index.html {
        add_header Cache-Control "no-cache, must-revalidate" always;
        add_header X-Content-Type-Options "nosniff" always;
        add_header X-Frame-Options "SAMEORIGIN" always;
        add_header Referrer-Policy "strict-origin-when-cross-origin" always;
    }

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

    location / {
        try_files $uri $uri/ =404;
    }

    error_page 404 /404.html;
    location = /404.html {
        internal;
    }
}

# 用 IP 直接访问时也给同一份站点(方便验证，也避免露出默认页)
server {
    listen 80 default_server;
    listen [::]:80 default_server;
    server_name _;

    root /var/www/daybreak;
    index index.html;
    charset utf-8;
    server_tokens off;

    location / {
        try_files $uri $uri/ =404;
    }
    error_page 404 /404.html;
    location = /404.html { internal; }
}
NGINX

sudo ln -sf "$CONF" /etc/nginx/sites-enabled/daybreak
# 去掉默认站点，否则它会抢 default_server
sudo rm -f /etc/nginx/sites-enabled/default

sudo nginx -t
sudo systemctl reload nginx

echo "--- 自测 ---"
curl -sS -o /dev/null -w "首页(IP):        HTTP %{http_code}\n" http://127.0.0.1/
curl -sS -o /dev/null -w "css:            HTTP %{http_code}\n" http://127.0.0.1/assets/style.css
curl -sS -o /dev/null -w "不存在的页面:     HTTP %{http_code}\n" http://127.0.0.1/nope
curl -sS -o /dev/null -w "域名访问:        HTTP %{http_code}\n" -H "Host: dayberak.top" http://127.0.0.1/
echo "--- 首页标题 ---"
curl -sS --max-time 8 http://127.0.0.1/ | grep -o "<title>.*</title>"