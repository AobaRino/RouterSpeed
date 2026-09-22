# RouterSpeed

从 ImmortalWrt 路由器采集流量，在 Windows 上分别显示**本机直连 / 代理的实时上传和下载速度**。路由器端提供 LuCI“网速采集器”菜单，Windows 端提供悬浮网速条和任务栏附近的紧凑显示。

## 功能

- **Windows 网速条**：悬浮模式支持拖动、置顶和隐藏；任务栏模式在主屏水平任务栏的托盘左侧显示两行紧凑数据（`D` 直连、`P` 代理，▼ 下载、▲ 上传），背景透明、文字直接落在深色任务栏上。悬浮模式采用 TrafficMonitor 式的面板布局；在程序目录或 `%LOCALAPPDATA%\RouterSpeed` 放一张 `skin.png` 即可作为面板皮肤，面板尺寸随图片，文字颜色按皮肤明暗自动选择。右键可查看完整速率、状态和连接设置，不显示鼠标悬停提示。
- **显示与快捷键**：右键“显示模式”切换两种模式，保留悬浮位置和置顶偏好。默认 `Ctrl + Alt + N` 显示 / 隐藏，可修改或停用；双击托盘图标也可切换显示。
- **登录自启**：右键“开机自启”使用当前用户的 Windows 登录任务，以普通交互用户权限在登录约 10 秒后启动，无需保存 Windows 密码。移动程序目录后需重新开启自启。
- **LuCI 菜单**：在“服务 → 网速采集器”查看直连、代理、未分类的实时上下行、采样时间和累计采集丢包；设置要统计的本机 IPv4、LAN 采集接口及采集启停。
- **连接配置**：LuCI 可生成、复制、下载或重置只读密钥；Windows 支持粘贴或导入连接配置，保存后立即生效。

任务栏显示采用独立窗口，不为 Windows 保留布局空间，也不注入或重启 Explorer。当前支持主屏水平任务栏；全屏或任务栏自动隐藏时暂时隐藏，空间不足或布局长期不可用时回退到悬浮位置。短暂布局变化会等待确认，避免立即切换尺寸；手动隐藏后不会自动重新显示。开始菜单、搜索或托盘弹层打开时，Windows 会把任务栏提升到桌面窗口层之上，此时网速条被任务栏遮挡属正常现象；任务栏回到桌面层后立即恢复显示，期间不会退回悬浮位置。

## 运行条件与兼容范围

- Windows：需要 **.NET 10 Desktop Runtime**；从源码构建需要 **.NET 10 SDK**。发布目录应放在当前用户可写的位置，以便保存连接配置。
- 路由器：ImmortalWrt / OpenWrt 环境，具备 LuCI、rpcd、uhttpd、UCI 和 procd，并已运行兼容的 dae / daed。
- 路由器采集实现目前仅支持并验证过 **Linux amd64（x86-64）**。不能将该二进制直接用于 ARM、MIPS 等设备。
- 两个 Go 组件使用标准库，构建要求 **Go 1.22 或更新版本**。
- 采集器依赖 dae 的 BPF 分流映射和最终路由日志，不是适用于所有代理插件的通用统计接口。默认映射路径为 `/sys/fs/bpf/daed/routing_tuples_map`，要求 key 为 40 字节、value 为 36 字节；默认日志路径为 `/var/log/daed/daed.log`。不同固件或 dae 版本需先核对兼容性，详见 [采集器说明](collector/README.md)。

## 构建

在仓库根目录执行 Windows 构建：

```powershell
dotnet publish RouterSpeed.csproj -c Release -o publish --self-contained false
```

运行 `publish/RouterSpeed.exe`，并保留同目录生成的其他运行文件。该构建方式依赖已安装的 .NET 10 Desktop Runtime。

在 PowerShell 中交叉编译两个路由器二进制，输出到部署目录：

```powershell
$env:GOOS = 'linux'
$env:GOARCH = 'amd64'
$env:CGO_ENABLED = '0'
go -C collector build -trimpath -ldflags='-s -w' -o ../router-files/usr/libexec/router-speed-collector .
go -C router-control build -trimpath -ldflags='-s -w' -o ../router-files/usr/libexec/router-speed-control .
```

## 测试

Go 测试请在未设置交叉编译 `GOOS` / `GOARCH` 的终端中执行，确保测试程序面向本机系统：

```powershell
dotnet run --project tests/RouterSpeed.Tests.csproj -c Release
go -C collector test ./...
go -C router-control test ./...
```

可选的任务栏布局、窗口层级和界面回归在 Windows 交互桌面上运行；界面检查使用独立测试窗口：

```powershell
dotnet run --project tests/taskbar-layout-check/LayoutCheck.csproj -c Release
dotnet run --project tests/window-order-check/WindowOrderCheck.csproj -c Release -- --observer-integration
dotnet run --project tests/dock-ui-check/DockUiCheck.csproj -c Release
```

## 首次部署

以下是手动部署流程；仓库中的 `router-files/` 是与路由器根目录对应的文件模板。部署前备份即将覆盖的同名文件。

源码和配置模板中的预填地址仅用于示例，首次使用时必须调整为自己的路由器地址、客户端 IPv4 和采集接口；下文的 `192.168.1.x` 不代表程序原始默认值。

1. **确认采集条件。** 核对路由器架构、dae 映射格式、日志路径，以及真正承载这台 Windows 电脑流量的 LAN 接口。建议为该电脑配置固定 DHCP 地址。
2. **修改服务配置。** 按自己的网络修改 `router-files/etc/config/router_speed` 中的 `client` 和 `interface`。例如电脑为 `192.168.1.10`、采集接口为 `eth0`；接口名不能直接照抄，应以路由器实际配置为准。`enabled` 控制采集服务是否启用。
3. **复制部署文件。** 构建两个 Go 二进制后，将 `router-files/` 下的文件按相对路径安装到路由器。两个二进制、`/usr/libexec/rpcd/router-speed`、`/www/cgi-bin/router-speed` 和 `/etc/init.d/router-speed` 需有执行权限（`0755`）。
4. **初始化并启动。** 在路由器运行 `/usr/libexec/router-speed-control --init`，随机生成只读密钥；已有有效密钥会被保留。启用并启动 `/etc/init.d/router-speed` 服务，然后重载 rpcd，使新增 RPC 和 ACL 生效。刷新 LuCI 后应出现“服务 → 网速采集器”。
5. **连接 Windows。** 在 LuCI 中确认采集接口和本机 IPv4，检查实时数据，再通过“Windows 工具连接”导出配置。启动 Windows 程序，在“连接设置”粘贴或导入该配置。

例如路由器为 `192.168.1.1` 时，只读 API 地址为 `http://192.168.1.1/cgi-bin/router-speed`。所有示例地址均需替换为自己的地址。

采集服务仅管理 RouterSpeed 自身，不需要修改网络、防火墙或 dae 分流规则，也不需要重启 dae。控制程序、RPC 和 API 的详细行为见 [路由器控制组件说明](router-control/README.md)。

## 凭据与本地数据

Windows 通过路由器现有 Web 服务上的 `/cgi-bin/router-speed` 读取汇总统计，不保存路由器 root 密码。只读 API 同时校验随机 token 和配置的客户端 IPv4 来源；该 token 不能修改采集设置，也不能用于登录 LuCI。

LuCI 中显示或重置密钥需要管理会话。重置后旧密钥立即失效，需重新导入 Windows。导出的连接 JSON 含明文 token，应妥善保管，导入后可删除。

- 路由器密钥：`/etc/router-speed/token`，权限 `0600`，所在目录权限 `0700`。
- 路由器采样：`/tmp/router-speed/status.json`，权限 `0600`，存于 RAM；LuCI 和 Windows 共享同一个采集进程的数据。
- Windows 连接配置：程序目录的 `settings.json`，密钥使用当前用户的 DPAPI 加密，不应直接复制给其他 Windows 用户。
- Windows 显示偏好：`%LocalAppData%\RouterSpeed\ui.json`。
- Windows 启动日志：`%LocalAppData%\RouterSpeed\startup.log`，仅记录启动阶段、进程号和错误类型。`RouterSpeed.exe --set-startup on` / `off` 可用于设置登录自启。

API 使用现有 uhttpd 配置，不额外开监听端口。若使用 HTTP，传输没有加密，请仅用于可信局域网；HTTPS 取决于路由器已有的 Web 服务配置。Windows 请求禁用系统代理和自动重定向。

不要将实际 token、导出配置、`settings.json`、本地日志、备份或构建输出提交到公开仓库。

## 统计范围与限制

- 只统计所选电脑的 **IPv4 公网 TCP / UDP**，不含 IPv6、局域网访问和其他协议。
- `KB` / `MB` 按 1024 进位；紧凑显示中的 `K` / `M` / `G` 分别表示 KB/s、MB/s、GB/s。
- 字节数包含 IPv4 头和重传，可能受到网卡卸载、采集丢包和接口选择影响，与应用下载文件大小不同。
- dae 的 domain 模式可能在用户态再次分流。内核确认的 DIRECT 视为直连，其他流量结合最终路由日志分类；无法确认的流量计入“未分类”，不会默认为直连。
- 等待日志可能引入短暂延迟。未分类或连接异常会显示状态提示；断线显示横杠并自动重试。
- 采集器重启、客户端 IP 或接口变化后会重新建立速率基线。该工具用于实时网速观察，不适用于计费审计。

参考：[dae 分流实现](https://github.com/daeuniverse/dae/blob/7e67e31e241a6d2cc5f2b5ff228b4fb5faf6d24a/control/kern/tproxy.c)、[Windows 登录触发任务](https://learn.microsoft.com/en-us/windows/win32/taskschd/starting-an-executable-when-a-user-logs-on)。
