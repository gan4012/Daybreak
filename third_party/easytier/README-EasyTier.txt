EasyTier 组件说明
================

本目录下的 easytier-core.exe 等文件来自开源项目 EasyTier:
  上游: https://github.com/EasyTier/EasyTier
  官网: https://easytier.cn/
  版本: v2.6.4
  用途: Daybreak 联机功能(虚拟组网)。以**独立子进程**方式调用,不链接、不修改其源码。

许可证(重要)
-------------
EasyTier 早期版本使用 Apache License 2.0,当前主线已改为 LGPL-3.0。
本项目按**更严格的 LGPL-3.0** 守法,完整文本见同目录:
  LICENSE-EasyTier-LGPL-3.0.txt

合规要点:
1. 分发的是**未经修改**的官方二进制,子进程调用(aggregation),不构成衍生作品;
2. 用户可自由用任意兼容版本替换本目录下的 easytier-core.exe;
3. 已随包提供 LGPL-3.0 完整文本。

如需自行获取源码:
  https://github.com/EasyTier/EasyTier

随附文件用途
-------------
easytier-core.exe   主程序(组网)
wintun.dll          创建虚拟网卡(TUN)必需
WinDivert64.sys     数据包捕获驱动
Packet.dll          WinDivert 配套库