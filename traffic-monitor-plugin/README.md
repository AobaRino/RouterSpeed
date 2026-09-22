# RouterSpeed TrafficMonitor 插件

把路由器上 dae 分流后的本机直连 / 代理实时网速接入 [TrafficMonitor](https://github.com/zhongyang219/TrafficMonitor)。
TrafficMonitor 负责任务栏嵌入、悬浮窗、皮肤、开机自启和设置界面；本插件只做一件事：每秒从
路由器的只读接口 `/cgi-bin/router-speed` 取一次计数器，算出速率，交给 TrafficMonitor 显示。

路由器端不需要任何改动，仍然使用仓库里的采集器和控制程序，部署方式见根目录 README。

## 显示项目

| 项目 ID | 显示 | 说明 |
|---|---|---|
| `routerspeed_direct_down` / `routerspeed_direct_up` | `直↓ 1.20 MB/s` / `直↑ 48.0 KB/s` | 文本项，按 TrafficMonitor 的字体、颜色和排版显示 |
| `routerspeed_proxy_down` / `routerspeed_proxy_up` | `代↓ 300 KB/s` / `代↑ 20.0 KB/s` | 同上 |
| `routerspeed_direct_row` / `routerspeed_proxy_row` | `D ▼1.2 M ▲48.0 K` / `P ▼300 K ▲20.0 K` | 自绘紧凑行，两项各占任务栏一行，正好组成一列 |

两组任选其一即可。在 TrafficMonitor 的“任务栏窗口设置 → 显示项目”里勾选，顺序在“项目顺序”里调整。
任务栏是两两配对排列的，若内置项目数量为奇数，紧凑行会和最后一个内置项目配对；把内置项目
调成偶数或把紧凑行排在最前面就能让 `D` / `P` 上下对齐。

鼠标悬停提示显示连接状态；有未分类流量时额外显示其速率。断线或凭证失效时数值显示为 `—`。

## 安装

1. 编译得到 `build\RouterSpeedPlugin.dll`（见下文），或使用已构建的文件。
2. 复制到 TrafficMonitor 程序目录下的 `plugins\`（没有就新建），重新启动 TrafficMonitor。
3. 右键 TrafficMonitor → “插件管理” → 选中 RouterSpeed → “选项”，粘贴 LuCI“Windows 工具连接”
   导出的 JSON（或手动填写接口地址和 Token），确定。
4. 在“任务栏窗口设置 → 显示项目”里勾选想要的项目。

配置保存在 TrafficMonitor 配置目录的 `plugins\RouterSpeed.ini`（便携模式即程序目录）；Token 用当前
Windows 用户的 DPAPI 加密（`ProtectedToken`）。也可以手写一行明文 `Token=`，插件第一次加载时会
转成加密形式并删掉明文。

只读接口同时校验 Token 和请求来源 IPv4，必须从路由器采集设置里指定的那台电脑访问；经 WireGuard
等隧道访问会得到 403（提示“路由器拒绝了本机地址”）。

## 构建

需要 Visual Studio 的“使用 C++ 的桌面开发”工作负载（MSVC + Windows SDK）。在本目录执行：

```bat
build.cmd
```

产物为 `build\RouterSpeedPlugin.dll`（x64，静态无第三方依赖，仅链接 WinHTTP、Crypt32、User32、GDI32）。

`include\PluginInterface.h` 来自 TrafficMonitor 仓库（GPLv3），是插件接口的定义，接口版本 8；
插件只使用接口版本 ≤ 7 的能力，兼容已发布的 TrafficMonitor 1.86。

## 实现说明

- HTTP 轮询在独立线程进行，`DataRequired()` 只读取最近一次结果，不会阻塞 TrafficMonitor 的
  主线程（任务栏窗口是 Explorer 的子窗口，主线程阻塞会连带任务栏卡顿）。
- 速率按计数器差值除以路由器时间戳差值计算；`instanceId` 变化或计数器回退时重新建立基线。
- 请求禁用系统代理和重定向，超时 2 秒，正文最多读取 16 KB。
- 紧凑行用 GDI 自绘（`IPluginItem::DrawItem`），三角箭头随字体高度缩放，颜色跟随 TrafficMonitor
  的标签 / 数值颜色设置。
