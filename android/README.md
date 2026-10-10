# HardwareScope 手机端

原生 Kotlin Android 应用，支持 Android 8.0 及以上。五个页面分别展示性能总览、分类传感器与最近 90 次采样曲线、可排序应用列表、超频和调校证据、连接与电脑端设置。所有控制命令均携带电脑端进程实例标识，结束进程和设置优先级需要确认。

1.0.1 总览新增逐设备温度区，覆盖主板、CPU、GPU、NPU、内存、显存、存储和网卡；温度名称、来源与缺失原因直接展示，点击读数可进入该设备所有温度及趋势。可读值优先，CPU Tctl/Tdie、GPU 核心及 NVMe Composite 优先展示；不将温度阈值、频率或 0 填充值当作温度。温度由电脑端采集，底层传感器可能需要电脑管理员权限，手机无需额外权限。

## 安装和连接

安装 1.1.0 / versionCode 3 的 `app/build/outputs/apk/release/app-release.apk`，电脑端需 HardwareScope 1.3.0 或以上。在电脑端启用 HTTPS 手机连接并生成六位 PIN；手机与电脑连接同一局域网，点“连接”，从自动发现的电脑列表中选择电脑，输入 PIN 即可验证并连接。PIN 的开头 0 必须保留，不会保存在手机上。若局域网广播被路由器屏蔽，可用“手动填写地址”输入电脑端 HTTPS 地址，再以同一 PIN 验证。

蓝牙先在手机系统设置中与电脑配对，在电脑端启用蓝牙服务并生成 PIN，然后选择已配对电脑，输入 PIN 即可连接。Android 12 及以上需授予“附近的设备”连接权限。使用已认证和加密的 RFCOMM 通道及 UUID `9174d900-1895-4d3e-a219-3a1927f05b81`。未实测配对的手机和电脑不能视为蓝牙兼容验证通过。

首次验证使用 Bouncy Castle 1.86 的 SRP6a（3072 位标准群、SHA-256），PIN 不以明文或普通摘要发送。双方验证证明后，以 AES-256-GCM 取得电脑的强连接凭据；局域网配对同时验证凭据中的证书指纹与两次请求实际观察到的 TLS 证书一致。自动发现仅提供电脑名称及地址，不提供信任。后续请求严格固定该证书并携带强凭据，拒绝明文 HTTP 和重定向。凭据使用 Android Keystore AES-GCM 加密保存，应用禁止系统备份；重新连接已保存的电脑无需再次输入 PIN，电脑轮换凭据后须重新配对。

手机仅在前台每 2 秒请求一次；切至后台会停止轮询并关闭活动连接，回到前台后继续。传感器缺失值显示“未提供”，图表保留空缺；不会伪造温度、功耗、NPU 或网络进程数据。运行调校软件仅标记证据，不等同于证明超频归因。

## 构建

使用 JDK 17、Gradle 8.9、Android SDK 35 / Build Tools 35.0.0、AGP 8.7.3 与 Kotlin 2.1.21；原生界面与系统 HTTPS / 蓝牙传输，只有配对密码学依赖 Bouncy Castle 1.86，发布时经 R8 移除未使用的实现。许可证位于 `app/src/main/assets/licenses/Bouncy-Castle-LICENSE.md`，随 APK 提供。运行 `pwsh -File build.ps1 -AndroidSdk D:\DevSpace\android-sdk`。首次运行创建独立正式签名密钥，以后复用 `signing/hardwarescope-release.jks` 和 `signing/release.properties`。请离线备份整个 `signing` 目录并限制访问；文件已被 Git 忽略，不放入公开源码包。

构建会执行 SRP 双向证明、错误 PIN、证书变化、加密凭据篡改、安全输入边界与实际 TLS 传输测试、Android lint，并验证 APK 签名。可将 `HARDWARESCOPE_PC_INTEGRATION_FILE` 环境变量指向包含 `address`、`fingerprint`、`token` 的私有 JSON 文件，启用真实电脑集成；再加入新生成的 `pin` 字段，可额外测试一次错误 PIN 后正确配对并以所得凭据读取真实数据。PIN 只消费一次，重复执行须生成新 PIN；私有文件不能放入源码或安装包。完整结果见 `VALIDATION.md`。本项目不会自动安装到已有用户设备，vivo 办公套件没有集成未经核实的私有 SDK。
