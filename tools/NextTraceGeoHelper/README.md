# NextTrace 定位组件

Go 1.26.5，Windows amd64，CGO_ENABLED=0。只进行节点定位，不发送路由探测。

- v4：使用官方 `/v4/ipGeo` 接口及 `X-NextTrace-Token` 请求头，凭证仅经父进程私有 stdin 传入，保留 TLS 证书与主机名校验，不跟随重定向。结构化返回 HTTP 状态、服务端令牌到期时间和 Retry-After；错误响应正文不输出。
- v3：直接调用随附的官方 NTrace-core v1.7.3（提交 40ac803ca233b0bc3d4b3bf1cacc8f7e0d8368f2）PoW/WebSocket 实现，upstream 原文件未修改。
- API 优先级、服务冷却、令牌状态和跨进程/跨目标的 SQLite 缓存由 C# 主程序维护。组件硬上限 10 分钟，主程序在空闲 2 分钟或取消时终止自己的组件。

构建：在此目录运行 `./build.ps1 -Go <go.exe路径>`，先进行离线协议测试再生成 `../../third_party/nexttrace/NextTraceGeoHelper.exe`。首次构建可通过 Go 模块下载依赖；完整源码交付目录另含 dependency-source 中的模块 ZIP（保留原始许可证）与 Go 模块信息。Go 模块路径以 go.mod 为准。

对应源码包包括本适配器、upstream 和全部 Go 模块依赖源码。适配器遵循 GPL-3.0-only。在线 API 使用权限须遵守服务方规则；源码许可不等同于服务授权。
