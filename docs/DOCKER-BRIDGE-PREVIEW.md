# Docker bridge 基础版：实施与部署

## 状态与范围

此分支 `docker/bridge-preview-20261008` 提供独立的 Linux/NAS Docker 服务，基于原仓库
`7ff18b1dc81320faea5ae5b6863146afdc8264f4`（Windows 2.8.1），使用 ASP.NET Core、浏览器界面与持久化 SQLite。
分支已经提交到 GitHub；不需要手动应用补丁。`main` 和原 Windows 项目保持不变。

采用 **bridge 网络与显式 `ports:`**，没有 host 网络回退。默认只监听宿主机回环地址，
首次运行需通过环境变量或密码文件提供管理员密码，未提供有效凭据时服务拒绝启动。

**这是可自行构建验证的 Docker 开发分支，不是已发布的稳定镜像。**
实际构建、测试和未验证边界见 [验证状态](../verification/server-preview/STATUS.md)。
CI 对 `docker/**` 分支在原生 amd64 runner 上构建及冒烟测试，不上传镜像、不部署。
不能仅凭一次 CI 通过推断所有 NAS、IPv6 公网及长期运行已经验收。

服务器引用原有 Core，复用 TCP、路由探测策略、时间轴、历史写入和路由分析。
既有 Windows 数据目录与配置不能直接作为服务器数据目录挂载。

## 已写入本次代码的功能

- 独立 Application / Linux / Web 项目，应用级后台实例，不随浏览器创建采集任务。
- 单管理员用户名/密码登录、环境变量或密码文件初始化、密码派生散列、Cookie、CSRF、登录/API 限速。
- 目标添加、开始/暂停、移除配置、统一设置；配置版本冲突检测。
- 启停意图持久化，停止容器不把所有目标永久保存为暂停。
- 20 个目标上限；TCP 使用现有 Core；ICMP 与逐跳探测使用私有 mtr 管道适配器。
- 带源码哈希检查的 mtr 补丁：毫秒超时、取消确认、原始套接字能力检查、已处理 ICMP 错误的 type/code、单调计时。
- 有界进程请求与查询并发；同时间段的时间轴查询共享结果。
- 非阻塞容器选路缓存；路由前后确认上下文；不能确认时使用 unknown，不伪造比较范围。
- 保留 `history.db` 与 `route-analysis.db`；稳定采集点标识、单实例目录锁。
- 基础静态网页：全部目标、24h/7d 折线和小时格、最近原始路由、事件、统一设置。
- Docker bridge、显式端口映射、非 root、持久目录；Compose 模板额外启用只读根文件系统与 local 日志轮转。
- 独立源码检查、.NET 控制台检查、Docker 冒烟脚本和 GitHub Actions 工作流。
- 请求字段/长度/模式校验，损坏或不完整数据拒绝覆盖；停止服务时先排空读取再释放 SQLite 与目录锁。
- 健康检查反映后台就绪及写入故障；登录退出后不会用旧请求恢复页面，重复表单提交受到保护。

## 明确没有完成的部分

此阶段网页是用于验证后台的轻量 HTML/CSS/JavaScript 界面，**不是计划中的最终 Vue 前端**。
尚未接入完整 2.8.1 路径分支比较、历史参考展示、Windows 归档导入、
在线一致性备份、完整诊断包、目标编辑页面或独立保留策略。

路由分析服务已经作为后台启动并保存首次判断，但当前网页只显示最近 20 份原始路由；
事件限最近 100 条。分析结果的多目标 Web 查询缓存留待完整比较页面接入时实施。
持续样本的 Detail 保留已处理错误的 type/code；旧 Core 的 HopProbe 没有相应字段，
本次未改其路由 JSON 格式，因此路由中仍保存兼容状态类别，而非完整原始 type/code。

IPv6 公网探测和 Docker IPv6 出口需实测；链路本地 IPv6 明确返回本地不支持错误，
不假装成功或超时。mtr 补丁和适配器尚未完成真实网络错误、丢包和取消拓扑测试。

## 获取 Docker 分支

```sh
git clone --branch docker/bridge-preview-20261008 --single-branch https://github.com/fengexian-c/IpQualityMonitor.git
cd IpQualityMonitor
```

已有仓库可先保存本地修改，再 fetch 并切换到该远端分支；不要强制重置或覆盖其他分支。
分支名中的 `/` 是普通 Git 命名方式，不影响 Docker 构建。

## Linux / NAS 首次构建与运行

支持目标仅为 **linux/amd64（x86-64）**；ARM64 不在此分支的兼容承诺与测试范围内。
前提：amd64 原生 Linux Docker Engine；仅可选的 Compose 方式需要 Compose 插件。
预览不承诺 rootless Docker，也不把 Docker Desktop 当成与 NAS 等价的网络验收环境。

以下命令都在**上述 Docker 分支的仓库根目录**执行。镜像需本地构建，
`ipqualitymonitor:server-preview` 只是本地标签，不表示已有公开发布镜像：

```sh
# 首先构建检查阶段；失败时不要继续当作可用版本部署。
docker build --target tests -f deploy/Dockerfile .
# 构建 runtime（包含固定版本 mtr 的下载、校验与编译）。
docker build --target runtime -t ipqualitymonitor:server-preview -f deploy/Dockerfile .
```

### 推荐：docker run + named volume + 环境变量

在有 Docker 运行权限的 **Bash** 中隐藏输入密码，不要将真实密码写进命令、脚本、聊天或 Git：

```bash
export IPQUALITY_ADMIN_USERNAME=nas-admin  # 可选；完全不设置时默认 admin
IFS= read -r -s -p '管理员密码（16–256 字符）: ' IPQUALITY_ADMIN_PASSWORD
printf '\n'
export IPQUALITY_ADMIN_PASSWORD

docker run -d --name ipqualitymonitor \
  -p 127.0.0.1:8088:8080 \
  -v ipqualitymonitor-data:/data \
  -e IPQUALITY_ADMIN_USERNAME -e IPQUALITY_ADMIN_PASSWORD \
  ipqualitymonitor:server-preview
unset IPQUALITY_ADMIN_PASSWORD

docker logs --tail=100 ipqualitymonitor
docker inspect --format '{{.State.Health.Status}}' ipqualitymonitor
```

等待健康状态为 `healthy` 后访问 `http://127.0.0.1:8088`，使用输入的用户名/密码登录。
第一次挂载空 named volume 时，Docker 使用镜像中 `/data` 的目录所有权，Web 按镜像固定的
UID/GID 10001 运行；不需要运行 `deploy/init.sh`，也没有 root 入口脚本或递归修改宿主机目录所有权。
始终使用同一个 `ipqualitymonitor-data` 卷保留账户与数据。不要添加 `volume-nocopy`。

默认仅宿主机本地/可达的反代访问。局域网直接访问时，把 `-p` 中的 `127.0.0.1`
替换为 NAS 的局域网 IPv4，例如 `192.168.1.20`，再访问 `http://NAS局域网IP:8088`。
`0.0.0.0:8088:8080` 会发布到全部宿主机 IPv4 接口，须自行限制公网访问。
宿主机端口可修改，容器内部端口固定为 8080。

若需要 `sudo`，它可能清除导出的变量，导致 `-e IPQUALITY_ADMIN_PASSWORD` 没有传入值。
在管理员允许的情况下，将上面的 `docker run` 改为
`sudo --preserve-env=IPQUALITY_ADMIN_USERNAME,IPQUALITY_ADMIN_PASSWORD docker run`，其余参数相同；
或者在有 Docker 权限的管理员 Bash 中重新输入和导出密码。不要把真实密码改写成命令行字面量。

**环境变量方式接受 Docker 管理员可读取明文的风险：** 密码会保存在容器配置中，
`docker inspect`、Docker 管理界面以及有相应权限的人都可能读取它。不要粘贴完整的 inspect 输出。
上面的 `unset` 只清除当前 shell 的值；`docker restart` 仍使用原容器配置。

首次初始化成功并确认可登录后，停止并删除**容器**，用同一卷重建且不再传入任何初始化变量，
才能从新容器配置中去掉该明文密码。以下示例沿用回环监听；若改过监听、端口等设置，重建时保持相同设置：

```sh
docker stop --time 45 ipqualitymonitor
docker rm ipqualitymonitor
docker run -d --name ipqualitymonitor \
  -p 127.0.0.1:8088:8080 \
  -v ipqualitymonitor-data:/data \
  ipqualitymonitor:server-preview
```

这不会修改持久化用户名/密码，也不会清理其他地方已留下的密码副本。不要删除数据卷。

### 可选：为 docker run 添加运行限制

上面的简便命令使用 Docker 默认 bridge 与默认能力集合（包含 NET_RAW），镜像本身固定非 root 用户。
没有额外配置自动重启、只读根文件系统、资源限制或日志轮转。需要这些设置时，可在 `docker run`
的镜像名之前按需添加下列参数，首次启动或之后重建时都保持一致：

```text
--init --restart unless-stopped --stop-timeout 45
--cap-drop ALL --cap-add NET_RAW --read-only
--tmpfs /tmp:rw,nosuid,nodev,size=64m
--memory 512m --pids-limit 128
--log-driver local --log-opt max-size=10m --log-opt max-file=3
```

只有在先移除默认能力（`--cap-drop ALL`）时，才需要显式加回 `--cap-add NET_RAW`。
只读根文件系统下仍需上述可写 `/tmp` 和原来的 `/data` 卷；不能添加 `no-new-privileges`，
否则 mtr 的文件能力提升无法工作。下方 Compose 已提供这些运行限制。

### 可选：保留密码文件的 Compose 部署

文件方式仍受支持；以下 Compose 使用应用专用的 `deploy/data` 绑定目录，与上面的 named volume
是**两个不同的数据位置**，不能把切换命令当成自动迁移。已有 named volume 时继续沿用原挂载，
迁移需停机完整备份并恢复数据。

```sh
# 只准备此应用的绑定目录和随机密码；已有密码不会被覆盖。
sudo sh deploy/init.sh
# 编辑监听地址等配置；IPQUALITY_ADMIN_USERNAME 可选，未设置默认 admin。
sudo nano deploy/.env

sudo docker compose --env-file deploy/.env -f deploy/compose.yaml config
sudo docker compose --env-file deploy/.env -f deploy/compose.yaml build
sudo docker compose --env-file deploy/.env -f deploy/compose.yaml up -d
sudo docker compose --env-file deploy/.env -f deploy/compose.yaml logs --tail=100
# 仅在本地查看初始密码。
sudo cat deploy/secrets/admin_password.txt
```

`deploy/compose.yaml` 已设置 `IPQUALITY_ADMIN_PASSWORD_FILE`。
**不要向该文件方式的 Compose 添加 `IPQUALITY_ADMIN_PASSWORD`，即使值为空也不行。**
密码来源以配置是否存在判断，不能同时存在；Compose 整体改用环境变量方式时，必须移除密码文件变量及相关 secret 挂载。
可选用户名通过 `deploy/.env` 中的 `IPQUALITY_ADMIN_USERNAME` 设置；显式空值会在首次初始化时拒绝启动。

默认 `IPQUALITY_BIND_ADDRESS=127.0.0.1`，局域网访问时改为 NAS 的局域网 IPv4。
初始化脚本只为固定 UID/GID 10001 准备应用的 `deploy/data`；改用其他绑定目录时，
只设置该应用目录的相应所有权，不要递归 chown 整个 NAS 共享目录。
不要通过 `chmod 777`、root Web、`privileged` 或额外网络能力解决权限问题。

**不要把密码、`deploy/data`、`deploy/secrets`、`deploy/.env` 提交到 Git。**
使用 HTTPS 反代时设置 `IPQUALITY_SECURE_COOKIES=true`，同时把反代实际连接来源 IP
填写到 `IPQUALITY_TRUSTED_PROXIES`（多个 IP 以逗号分隔），并让反代覆盖设置 `X-Forwarded-Proto: https`。
`docker run` 部署通过相应 `-e` 参数设置，Compose 部署通过 `deploy/.env` 设置。
只有明确可信的来源才会改变请求协议，不能信任任意客户端传来的转发头。
没有正确配置可信反代时，HTTPS-only 的防伪 Cookie 配置会拒绝普通 HTTP 请求；HTTP 调试时应为 false。
HTTP 会明文传输登录数据，仅用于可信内网调试；不要把此预览版直接暴露给公网。

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

- 使用新的应用数据卷/目录。未带本服务实例标记的现有数据库会被拒绝，避免初始化/清理 Windows 历史。
- `settings.server.json` 独立于 Windows `settings.json`；设置格式损坏时停止并保留原件，不自动重置。
- `instance.json` 保留稳定采集点 ID；恢复同一采集点时完整保留它。
- 复制到另一台主机用于新的采集点时，应使用新的数据目录；预览没有跨采集点合并功能。
- `route-analysis.db` 包含首次判断，不只是可丢弃缓存。
- Data Protection 密钥保存在 `keys/`；同盘密钥与密文不防拥有 NAS 管理权限的人读取。
- 保留期限沿用 Core 的 31 天；没有启用新的缩短历史清理策略。

### 初始化与现有账户兼容性

只有不存在 `admin-auth.json` 时才读取初始化凭据：

- `IPQUALITY_ADMIN_USERNAME` 可选，**未配置**时默认 `admin`；只接受 ASCII
  `[A-Za-z0-9_.-]{1,64}`，区分大小写，不自动去除空格。显式空值、空格或非 ASCII 用户名无效。
- `IPQUALITY_ADMIN_PASSWORD` 按原值使用，长度必须为 **16–256 字符**；不去除开头/末尾的空格或换行。
- 或使用 `IPQUALITY_ADMIN_PASSWORD_FILE` 指定 UTF-8 密码文件；文件方式只去除末尾的 CR/LF 换行，
  不去除空格，去除换行后同样要求 16–256 字符。
- 两种密码来源**按是否存在互斥**；同时设置就拒绝首次初始化，即使其中一个或两者为空。
  两者都未配置、密码为空/长度不符、文件不可读或用户名无效，也不会创建可登录的默认账户。

初始化仅把密码派生散列保存到 `admin-auth.json`，不会保存明文密码。新的账户记录使用 schema 2 并保存用户名。
一旦已有**有效**认证文件，所有初始化变量均被忽略，包括空值、冲突来源、错误用户名或不可读的密码文件路径。
修改环境变量或密码文件、重启或重建容器都不能为已有账户改名/重置密码；现有认证文件损坏时拒绝启动，
不会用初始化值静默覆盖。这是应用的读取规则；若 Compose 仍声明 secret，Compose 本身仍要求其源文件存在。
要移除文件方式的引导配置，应一起移除相应环境变量、服务 secret 挂载和顶层 secret 定义后重建容器。

旧 schema 1 认证文件继续使用用户名 `admin` 和原密码登录；读取时文件内容**逐字节保持不变**，不会自动迁移重写。
新 schema 2 认证文件不能回退给仅支持 schema 1 的旧程序使用；此类旧二进制回退不受支持。
升级前完整停机备份；需要回退时恢复与旧程序配套的整份备份，不手工改 schema 字段或混用不同时间的数据文件。

### 停机备份

named volume 示例（主机需要 `tar`，备份写到当前目录下的新私有目录）：

```sh
umask 077
backup_dir="$(pwd)/ipqualitymonitor-backup-$(date +%Y%m%d-%H%M%S)"
docker stop --time 45 ipqualitymonitor &&
mkdir "$backup_dir" &&
docker run --rm --network none --read-only --cap-drop ALL \
  -v ipqualitymonitor-data:/data:ro \
  --entrypoint tar ipqualitymonitor:server-preview \
  -C /data -czf - . > "$backup_dir/data.tar.gz" &&
tar -tzf "$backup_dir/data.tar.gz" > /dev/null
```

确认全部命令成功后，若仅备份可运行 `docker start ipqualitymonitor`；若接着恢复账户，保持停机。

Compose 绑定目录的停机备份：

```sh
sudo docker compose --env-file deploy/.env -f deploy/compose.yaml stop &&
sudo install -d -m 0700 /YOUR/BACKUP/DIR &&
sudo sh -c 'umask 077; tar -C deploy -czf /YOUR/BACKUP/DIR/ipqualitymonitor-data.tar.gz data' &&
sudo tar -tzf /YOUR/BACKUP/DIR/ipqualitymonitor-data.tar.gz > /dev/null
```

使用新的备份路径，避免覆盖已有备份。确认全部命令成功后，若仅备份可运行
`sudo docker compose --env-file deploy/.env -f deploy/compose.yaml start`；若接着恢复账户，保持停机。

备份应保护访问权限。不要运行时只复制 `history.db`，也不要漏掉分析库、WAL/SHM、`instance.json` 和 `keys/`。
恢复前停机并保留当前卷/目录的完整副本，再恢复整份数据，不混合不同备份的两个数据库。
不要使用 `docker volume rm`、`docker compose down -v` 或删除数据目录来重置登录。

### 离线恢复管理员用户名或密码

当前没有网页修改密码入口。需恢复账户时，按顺序操作：

1. 停止服务，并按上节**完整备份同一个数据卷/目录**。确认备份成功、可读取后才继续；全程保持服务停止。
2. 只移走 `admin-auth.json`；不要删除/改动数据库、`instance.json`、`keys/` 或整个 named volume。
   named volume 可用以下一次性非 root 工具容器，将旧认证文件保留为另一文件名：

   ```sh
   docker run --rm --network none --read-only --cap-drop ALL \
     -v ipqualitymonitor-data:/data \
     --entrypoint /bin/sh ipqualitymonitor:server-preview \
     -c 'test -f /data/admin-auth.json && test ! -e /data/admin-auth.json.before-reset && mv /data/admin-auth.json /data/admin-auth.json.before-reset'
   ```

   若备份名已存在，命令拒绝覆盖；先将该旧副本另行妥善保存。
   Compose 绑定目录则只把 `deploy/data/admin-auth.json` 移入受保护的备份目录。
3. 为新账户重新设置可选用户名，并选择**一种**新密码来源。环境变量方式重新隐藏输入并导出密码，
   删除已停止的容器后，按首次启动的 `docker run` 命令创建新容器，**仍挂载原来的 `ipqualitymonitor-data:/data`**。
   不能只用 `docker start` 更新环境变量。文件 Compose 方式更新原密码文件并保持 UID 10001 可读，
   如需改名同时更新 `deploy/.env`，再运行 `docker compose ... up -d --force-recreate`。
4. 确认新用户名/密码可登录、旧登录 Cookie 已失效，数据和采集点仍在。
   环境变量方式按前述步骤再次重建不含初始化变量的容器，移除容器配置中的明文密码。

恢复会生成新的认证安全标记使旧 Cookie 失效，不需要删除保护密钥。保留完整备份及旧认证文件，
不要用 root Web、root 入口脚本或递归 chown 宿主机共享目录来绕过恢复中的权限问题。

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

`.NET` 新检查包括状态映射、配置锁/损坏保护、启动/停止并发、凭据和模拟子进程并发、取消、超时和崩溃分类。
固定基础镜像使用已经验证的 manifest digest；更新安全补丁时应主动更新 digest 并重新构建测试。
原生 mtr 解码回归使用合成 IPv4/IPv6 数据包验证 type/code、迟到回应、外来/畸形回应与取消，不发出真实网络流量。
这些模拟不能代替实际 raw ICMP、TTL、不可达及 Docker bridge 测试。

`.github/workflows/server-preview.yml` 包含检查阶段、运行镜像构建与 bridge HTTP 冒烟测试。
工作流仅使用原生 amd64 runner；每次具体结果以对应提交的 Actions 记录为准。
没有自动推送 GHCR 或发布 Release；NAS 实际出口与公网 IPv6 仍需在目标机器验收。

验证层次是：完整编译 → 最终镜像权限与 loopback 冒烟 → 受控 IPv4/IPv6 拓扑 → NAS 长期稳定性与恢复测试。
通过之后，再接入最终 Vue 界面、完整路由历史、定位和 Windows 归档。


## NextTrace 定位注释预览

默认离线，保留 mtr 作为唯一测量来源。网页可明确选择离线、v3 PoW 或 v4；注释 API 仅读取已保存证据，查看历史不会启动定位查询。地区、ASN／组织、来源、查询时间及过期状态与原始测量分开展示。

v4 仅接受 `IPQUALITY_NEXTTRACE_TOKEN_FILE` 指定的只读挂载文件；密钥不进入网页配置、SQLite 注释、命令行或镜像。v4 失败不回退 v3。已观测公网节点 IP 及服务器出口 IP 会被 NextTrace 服务看到，启用前请阅读 [配置、隐私、许可与验证边界](NEXTTRACE-DOCKER.md)。

新增故障回归使用模拟服务／helper，不代表真实 NextTrace 服务可用性或权限确认。真实查询和部署需要单独授权。
