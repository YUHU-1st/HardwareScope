# 安卓构建与连接验证

验证日期：2026-10-09（北京时间）。

正式 APK：`app/build/outputs/apk/release/app-release.apk`，677376 字节。包名 `io.github.hardwarescope.mobile`，版本 `1.0.1` / versionCode `2`，最低 Android 8.0（API 26），target / compile SDK 35。

APK SHA-256：`13c870874adf8165087ffb1bc758f0059362bbbd850b89e3984a7aaf3e6c6f44`。

APK 使用独立 RSA-3072 发布密钥；`apksigner verify --verbose --print-certs` 验证通过，APK Signature Scheme v2 有效。签名证书 SHA-256 为 `8e4c42717d669c54a0904946fbce3e018d437bdc472809e3036b3e8aef5d8c11`。这个指纹属于 APK 发布签名，手机连接时应填写电脑服务证书的指纹。

`build.ps1` 的 `assembleRelease testReleaseUnitTest lintRelease` 全部通过，13 项测试通过、0 失败；2 项需要私有连接文件的 PC 集成测试本次跳过。Android lint 为 0 错误、5 项中文界面字符串格式化提示。没有采用 lint 基线来隐藏错误。

| 验证 | 结果 |
|---|---|
| HTTPS URL、证书指纹、已配对蓝牙地址与密钥输入边界 | 4 项单元测试通过；HTTP、地址内账号、路径、查询参数、无效端口、无效指纹、密钥换行均拒绝 |
| 实际 TLS socket 上的手机网络实现 | 4 项测试通过；精确指纹可连接自签证书；错误指纹在发送 HTTP 请求和密钥前被拒绝；307 不跳转到 HTTP；响应超过 2 MB 被拒绝 |
| 总览温度筛选与来源优先级 | 5 项测试通过；首个 null 不会隐藏后续可读值；仅使用 Temperature 类型；频率/功耗/负载及 Critical Temperature 阈值不代替实测温度；缺失原因和 null 保留；8 类设备使用各自温度来源；Tctl/Tdie、GPU 核心、Composite 优先于次级来源 |
| 真实 HardwareScope PC Core + 手机 Kotlin `HttpsTransport` 的 JVM 集成 | 1.0.0 曾通过 2 项实际服务集成：读取实际硬件、进程、传感器及超频字段；读取设置；连接状态不泄露密钥；PUT 采样间隔 3000 ms 并读取确认后恢复原设置；错误密钥 HTTP 401；错误证书指纹拒绝。1.0.1 没有修改传输协议，本次未重跑此项 |
| APK 元数据 | `aapt dump badging` 确认 1.0.1 / versionCode 2、包名、API 范围及启动 Activity；蓝牙为可选硬件，仅请求互联网和蓝牙连接权限 |
| 密钥与备份设计 | Android Keystore AES-GCM；Android 8–11 禁止备份，Android 12 及以上显式排除云备份及设备迁移中的连接偏好；APK 和源码不含 PC 访问密钥 |

真实 PC 集成使用隔离的临时服务及外部私有连接文件，通过 `HARDWARESCOPE_PC_INTEGRATION_FILE` 环境变量启用；测试不记录凭据，不结束或调整任何用户进程。没有该环境变量时，2 项 PC 集成测试自动跳过；其余 13 项可独立运行。测试资源 `https-test.p12` 是公开的、仅供本地 TLS 测试的临时证书，不是发布签名或电脑连接凭据。

1.0.1 总览保留原性能卡片，新增覆盖主板、CPU、GPU、NPU、内存、显存、存储和网卡的逐设备温度区；每设备预览两个有名称、来源的读数，更多来源可进入设备温度详情与原有 90 点趋势。没有温度传感器时明确显示“未提供”及原因，温度详情不使用频率曲线替代。未增加网络请求、后台轮询、依赖库或历史采样长度。

本机没有 adb 设备，也没有现成 AVD。未安装到任何用户手机，因此没有把 JVM TLS 集成或编译验证表述为安卓实机界面、Keystore 或蓝牙兼容性测试。蓝牙 RFCOMM、权限请求和串行 JSON 收发代码已实现，真实配对兼容性仍需要用户手机与电脑的现场验证。

构建脚本针对 JDK 17 在中文 Windows 上的 Gradle 测试参数文件路径问题，临时使用空闲盘符映射项目目录，并在完成后卸载映射；不修改系统字符集或持久化盘符。源码包应排除 `.tools`、`.gradle`、`signing` 和构建目录；正式 APK 单独交付。
