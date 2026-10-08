# Docker bridge 基础版：实施与部署

## 状态与范围

这是第一阶段代码，基于 `fengexian-c/IpQualityMonitor` 的提交
`7ff18b1dc81320faea5ae5b6863146afdc8264f4`（2.8.1）。

**不是已发布的稳定镜像，也没有完成 Linux 实机、ARM64 或 24—72 小时验收。**
本次环境没有 .NET SDK / Docker，且无法直接拉取仓库到构建环境，因此没有执行完整 .NET 编译或整套 mtr 编译。
已经执行的检查和没有执行的检查见 `verification/server-preview/STATUS.md`。
GitHub 连接创建开发分支返回 403，本次远端仓库没有任何修改。交付方式为新增文件与 Git 补丁。

本次只增加项目和部署文件，不改动现有 Core、Windows App 或 Windows 构建脚本。
服务器引用原有 Core，复用其 TCP、路由策略、时间轴、历史写入和路由分析。

## 已写入本次代码的功能

- 独立 Application / Linux / Web 项目，应用级后台实例，不随浏览器创建采集任务。
- 单管理员登录、密码文件初始化、密码派生散列、Cookie、CSRF、登录/API 限速。
- 目标添加、开始/暂停、移除配置、统一设置；配置版本冲突检测。
- 启停意图持久化，停止容器不把所有目标永久保存为暂停。
- 20 个目标上限；TCP 使用现有 Core；ICMP 与逐跳探测使用私有 mtr 管道适配器。
- 带源码哈希检查的 mtr 补丁：毫秒超时、取消确认、原始套接字能力检查、已处理 ICMP 错误的 type/code、单调计时。
- 有界进程请求与查询并发；同时间段的时间轴查询共享结果。
- 非阻塞容器选路缓存；路由前后确认上下文；不能确认时使用 unknown，不伪造比较范围。
- 保留 `history.db` 与 `route-analysis.db`；稳定采集点标识、单实例目录锁。
- 基础静态网页：全部目标、24h/7d 折线和小时格、最近原始路由、事件、统一设置。
- Docker bridge、显式 `ports:`、非 root、持久目录、只读根文件系统、local 日志轮转。
- 独立源码检查、.NET 控制台检查、Docker 冒烟脚本和 GitHub Actions 工作流。

## 明确没有完成的部分

此阶段网页是用于验证后台的轻量 HTML/CSS/JavaScript 界面，**不是计划中的最终 Vue 前端**。
尚未接入完整 2.8.1 路径分支比较、历史参考展示、在线定位/NextTrace、Windows 归档导入、
在线一致性备份、完整诊断包、目标编辑页面或独立保留策略。

路由分析服务已经作为后台启动并保存首次判断，但当前网页只显示最近 20 份原始路由；
事件限最近 100 条。分析结果的多目标 Web 查询缓存留待完整比较页面接入时实施。
持续样本的 Detail 保留已处理错误的 type/code；旧 Core 的 HopProbe 没有相应字段，
本次未改其路由 JSON 格式，因此路由中仍保存兼容状态类别，而非完整原始 type/code。

IPv6 公网探测和 Docker IPv6 出口需实测；链路本地 IPv6 明确返回本地不支持错误，
不假装成功或超时。mtr 补丁和适配器尚未完成真实网络错误、丢包和取消拓扑测试。

## 把补丁应用到仓库

在原仓库工作区干净、基线提交存在的前提下，在仓库根目录执行：

```sh
git switch -c feature/docker-bridge-preview 7ff18b1dc81320faea5ae5b6863146afdc8264f4
git apply --check /path/to/ipqualitymonitor-docker-bridge.patch
git apply /path/to/ipqualitymonitor-docker-bridge.patch
git status --short
```

`/path/to/...` 替换为实际下载路径；Windows PowerShell 可以使用带引号的 Windows 路径。
补丁只新增文件，不会删除或覆盖现有 Windows 文件。分支名已存在时请更换名称，不要强制重置。
应用不会自动提交、推送或发布镜像。审阅并通过测试后再提交。

## Linux / NAS 首次构建与运行

前提：原生 Linux Docker Engine 与 Compose 插件。预览不承诺 rootless Docker，
也不把 Docker Desktop 当成与 NAS 等价的网络验收环境。

以下命令都在**应用补丁后的仓库根目录**执行，不是在独立增量文件包中执行：

```sh
# 创建只属于此应用的目录和初始随机密码。已有密码不会被覆盖。
sudo sh deploy/init.sh

# 编辑监听地址等非敏感配置。
sudo nano deploy/.env
```

默认 `IPQUALITY_BIND_ADDRESS=127.0.0.1`，仅宿主机本地/可达的反代访问。
局域网直接访问时改为 NAS 的局域网 IPv4，例如 `192.168.1.20`；
也可以明确设置 `0.0.0.0`，但这会发布到全部宿主机 IPv4 接口，须自行限制公网访问。
默认宿主机端口为 8088，容器内部端口固定为 8080。

```sh
# 先查看最终配置。
sudo docker compose --env-file deploy/.env -f deploy/compose.yaml config

# 首次建议先运行检查阶段，失败时不要继续当作可用版本部署。
sudo docker build --target tests -f deploy/Dockerfile .

# 构建 runtime（包含固定版本 mtr 的下载、校验与编译）。
sudo docker compose --env-file deploy/.env -f deploy/compose.yaml build
sudo docker compose --env-file deploy/.env -f deploy/compose.yaml up -d
sudo docker compose --env-file deploy/.env -f deploy/compose.yaml logs --tail=100
```

在设置了局域网监听后，用浏览器访问 `http://NAS局域网IP:8088`。
本地查看初始密码：

```sh
sudo cat deploy/secrets/admin_password.txt
```

**不要把密码、`deploy/data`、`deploy/secrets`、`deploy/.env` 提交到 Git。**
使用 HTTPS 反代时设置 `IPQUALITY_SECURE_COOKIES=true`，同时把反代实际连接来源 IP
填写到 `IPQUALITY_TRUSTED_PROXIES`（多个 IP 以逗号分隔），并让反代覆盖设置 `X-Forwarded-Proto: https`。
只有明确可信的来源才会改变请求协议，不能信任任意客户端传来的转发头。
没有正确配置可信反代时，HTTPS-only 的防伪 Cookie 配置会拒绝普通 HTTP 请求；HTTP 调试时应为 false。
HTTP 会明文传输登录数据，仅用于可信内网调试；不要把此预览版直接暴露给公网。

采用固定 UID/GID 10001；绑定的数据目录需由该 UID 写入。
初始化脚本不递归修改整个 NAS 共享目录，仅准备 `deploy/data`。
如果把数据挂载改到其他目录，先只对该应用目录设置相应所有权。
不要通过 `chmod 777`、root Web、`privileged` 或额外网络能力解决权限问题。

## 网络：只使用 bridge + ports

实际 Compose 的核心配置是：

```yaml
ports:
  - "${IPQUALITY_BIND_ADDRESS:-127.0.0.1}:${IPQUALITY_HTTP_PORT:-8088}:8080"
networks:
  - monitor
# ...
networks:
  monitor:
    driver: bridge
```

没有 `network_mode: host`，也没有隐式 host 回退。
Web 在容器内绑定 `0.0.0.0:8080`，因此端口映射能够到达它。
单纯把原方案的容器内监听 `127.0.0.1` 保留下来会让端口映射无法按预期工作，本实现已改变该监听。

测量路径是“容器网络 → Docker bridge → 宿主机出口 → 目标”。
`127.0.0.1` 指容器自身，不是 NAS 宿主机。
容器系统选路只能观察容器命名空间，不能保证直接识别宿主机的 VPN/策略路由变化。
页面和实例元数据均明确标记 bridge；不删除原始 TTL，也不把容器网关冒充运营商节点。

`ports:` 用于访问网页，不用于映射 ICMP 探测。不要额外映射所谓“ICMP 端口”。

### 可选 IPv6 bridge

```sh
sudo docker compose --env-file deploy/.env \
  -f deploy/compose.yaml -f deploy/compose.ipv6.yaml config
sudo docker compose --env-file deploy/.env \
  -f deploy/compose.yaml -f deploy/compose.ipv6.yaml up -d --build
```

网络启用 IPv6 不等于公网 IPv6 已可达；Docker Engine、宿主机、NAT/路由和防火墙需共同支持。
预览不在代码中添加宿主机 IPv6 sysctl 或路由，不自动改动 NAS 网络配置。
公网 IPv6 的实际出口可通过容器中的 `ip -6 route`、受控目标与抓包验证。

## 数据与凭据

- 使用新的 `deploy/data`。未带本服务实例标记的现有数据库会被拒绝，避免初始化/清理 Windows 历史。
- `settings.server.json` 独立于 Windows `settings.json`；设置格式损坏时停止并保留原件，不自动重置。
- `instance.json` 保留稳定采集点 ID；恢复同一采集点时完整保留它。
- 复制到另一台主机用于新的采集点时，应使用新的数据目录；预览没有跨采集点合并功能。
- `route-analysis.db` 包含首次判断，不只是可丢弃缓存。
- 初始密码只用于生成 `admin-auth.json` 中的派生散列；之后修改初始密码文件不会自动重设账户。
- Data Protection 密钥保存在 `keys/`；同盘密钥与密文不防拥有 NAS 管理权限的人读取。
- 保留期限沿用 Core 的 31 天；没有启用新的缩短历史清理策略。

停机备份示例（备份目录不放入 data）：

```sh
sudo docker compose --env-file deploy/.env -f deploy/compose.yaml stop
sudo mkdir -p /YOUR/BACKUP/DIR
sudo tar -C deploy -czf /YOUR/BACKUP/DIR/ipqualitymonitor-data.tar.gz data
sudo docker compose --env-file deploy/.env -f deploy/compose.yaml start
```

备份应保护访问权限。不要运行时只复制 `history.db`，也不要漏掉分析库、WAL 状态和保护密钥。
恢复前停机并保留当前目录副本，再恢复整份数据，不混合不同备份的两个数据库。

需重设管理员密码时，先停机和完整备份，再仅移走 `data/admin-auth.json`，
并更新密码文件后启动；不要删除 `keys/`。新散列的安全标记会使旧登录 Cookie 失效。
当前没有网页修改密码入口。

## 权限与组件契约

Web 使用非 root UID 10001；仅 mtr 可执行文件在最终镜像中设置 `cap_net_raw=ep`。
容器能力边界仅增加 NET_RAW，不需要 NET_ADMIN、宿主机 PID 或 Docker socket。
依赖文件能力提升，因此不能同时添加 `no-new-privileges:true`；实际 NAS 文件能力必须通过最终镜像测试。

适配器先检查 `iqm-contract-v1` 和对应地址族的 raw 能力；不能使用普通系统 mtr 冒充。
原始套接字不可用时返回本地错误，不采用会合并错误类型的 datagram 回退。
固定 mtr 源码修订见 `tools/mtr/UPSTREAM.json`；不允许构建时自动跟随 master。
补丁针对五个核心源文件先核对 Git blob SHA，再修改；未知源版本直接失败。

镜像包含对应 patched mtr 源码与补丁，位于：
`/usr/share/doc/ipqualitymonitor/mtr-source.tar.gz`。
此子组件遵循原 mtr GPL-2.0 声明；不在本次改动中改写原仓库的许可声明。

## 检查与发布

```sh
python3 -m unittest discover -s tests/server -p 'test_*.py' -v
node --check src/IpQualityMonitor.Web/wwwroot/app.js

dotnet run --project tests/IpQualityMonitor.Server.Tests/IpQualityMonitor.Server.Tests.csproj -c Release
dotnet run --project tests/TcpLatencyMonitor.Tests/TcpLatencyMonitor.Tests.csproj -c Release -- --routes
dotnet run --project tests/TcpLatencyMonitor.Tests/TcpLatencyMonitor.Tests.csproj -c Release -- --route-history
```

`.NET` 新检查包括状态映射、配置锁/损坏保护、模拟子进程并发、取消、超时和崩溃分类。
这些模拟不能代替实际 raw ICMP、TTL、不可达及 Docker bridge 测试。

`.github/workflows/server-preview.yml` 包含检查阶段、运行镜像构建与 bridge HTTP 冒烟测试。
只定义工作流，不表示本次已经执行；没有自动推送 GHCR 或发布 Release。
ARM64 要在相应架构进行构建与真实网络测试后再标为支持。

下一道门槛是：完整编译通过 → 最终镜像权限与 loopback 冒烟 → 受控 IPv4/IPv6 拓扑 → 稳定性与恢复测试。
通过之后，再接入最终 Vue 界面、完整路由历史、定位和 Windows 归档。
