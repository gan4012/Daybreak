/* ============================================================
   Daybreak 官网脚本
   故意保持很小:只做三件事 —— 应用下载配置、导航滚动态、年份。
   没有框架、没有构建步骤。
   ============================================================ */
(function () {
    'use strict';

    var cfg = window.DAYBREAK || {};
    var links = cfg.links || {};

    /* ---------- 1. 应用版本号与体积 ---------- */
    function setText(id, text) {
        var el = document.getElementById(id);
        if (el && text) el.textContent = text;
    }
    setText('dlVersion', cfg.version);
    setText('dlSize', cfg.size);

    /* ---------- 2. 网盘按钮 ---------- */
    /* 没配置链接时按钮是禁用的,并显示「即将上线」;
       配置了就变成可点的主按钮,并把提取码/说明写进标签。 */
    var netdisk = document.getElementById('dlNetdisk');
    var netdiskTag = document.getElementById('dlNetdiskTag');

    if (netdisk && typeof links.netdisk === 'string' && links.netdisk.trim()) {
        var url = links.netdisk.trim();

        netdisk.setAttribute('href', url);
        netdisk.removeAttribute('aria-disabled');
        netdisk.setAttribute('target', '_blank');
        netdisk.setAttribute('rel', 'noopener');

        var tag = '可下载';
        if (links.netdiskCode && String(links.netdiskCode).trim()) {
            tag = '提取码 ' + String(links.netdiskCode).trim();
        } else if (links.netdiskNote && String(links.netdiskNote).trim()) {
            tag = String(links.netdiskNote).trim();
        }
        if (netdiskTag) netdiskTag.textContent = tag;

        var desc = netdisk.querySelector('.dl-desc');
        if (desc) {
            desc.textContent = (links.netdiskNote && links.netdiskNote.trim())
                ? String(links.netdiskNote).trim()
                : '速度稳定，国内直连，推荐';
        }
    }

    /* ---------- 3. 导航滚动态 ---------- */
    /* 只在滚过 hero 顶部之后加一层实底,让文字在内容区也读得清 */
    var nav = document.getElementById('nav');
    if (nav) {
        var ticking = false;
        var onScroll = function () {
            if (ticking) return;
            ticking = true;
            window.requestAnimationFrame(function () {
                if (window.scrollY > 40) {
                    nav.classList.add('scrolled');
                } else {
                    nav.classList.remove('scrolled');
                }
                ticking = false;
            });
        };
        window.addEventListener('scroll', onScroll, { passive: true });
        onScroll();
    }
})();
