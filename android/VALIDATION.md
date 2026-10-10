# 安卓构建与连接验证

## 1.1.0 六位 PIN 配对验证

验证日期：2026-10-10（北京时间）。以下结果对应 1.1.0 / versionCode 3，历史 1.0.1 记录保留在下一节。

正式 APK 为 `app/build/outputs/apk/release/app-release.apk`，99555 字节，SHA-256 为 `1e396f0662c0cba3c77606f3bf8b1db0ebbe7b48e7c080743b886759959259a3`。`aapt dump badging` 确认版本、包名 `io.github.hardwarescope.mobile`、最低 API 26、target SDK 35 与启动 Activity。`apksigner` 确认 v2 签名有效，仍使用原 RSA-3072 发布证书，其 SHA-256 为 `8e4c42717d669c54a0904946fbce3e018d437bdc472809e3036b3e8aef5d8c11`，可以覆盖更新原安装。

`assembleRelease testReleaseUnitTest lintRelease` 全部通过。最终代码以重新生成的 PIN 再次运行隔离的真实 Windows 服务集成，30 项测试全部通过，0 失败、0 跳过。没有私有连接文件时，27 项独立测试仍可运行，3 项电脑集成测试自动跳过。Android lint 为 0 错误、8 项中文界面 `SetTextI18n` 格式化提示，没有设置 lint 基线隐藏错误。

| 验证 | 结果 |
|---|---|
| PIN 与标准 SRP6a | 8 项测试通过；实际 Bouncy Castle 服务端与客户端双向证明，保留开头 0；错误 PIN 无法取得凭据；拒绝非六位 ASCII 数字、无效服务端公钥、错误 M2、AES-GCM 篡改和凭据证书不匹配；蓝牙使用相同证明协议 |
| 局域网发现输入边界 | 3 项测试通过；连接地址始终使用 UDP 发件者地址；拒绝其他产品、无效端口、超长名称和错误响应端口；发现内容中的密钥与证书信息不会授予信任 |
| 实际 TLS bootstrap 与普通请求 | 7 项测试通过；自签候选证书只用于两个配对路由，配对不发送 Authorization；第二次配对 TLS 握手更换证书时，在发送 HTTP 证明前拒绝；普通请求仍固定精确证书、携带强凭据、禁止重定向并限制 2 MB 响应。授权失效时引导以新 PIN 配对，无需用户处理强凭据 |
| Java 手机实现与 .NET Windows 服务互通 | 3 项真实集成测试通过；错误 PIN 后正确 PIN 完成 SRP6a / M2 / AES-GCM 与实际 TLS 指纹绑定，并使用取得的强凭据读取真实电脑数据；硬件与进程数据字段校验；设置采样间隔后确认并恢复；错误强凭据为 HTTP 401，错误证书被拒绝 |
| 原功能回归 | 4 项连接配置边界测试、5 项温度来源和缺失值测试全部通过 |
| 凭据与后台行为 | PIN 输入每次打开为空，验证后清空，不进入 SecureStore；仅验证成功才保存强凭据和证书，失败或取消不覆盖原连接。取消、切后台及销毁 Activity 会关闭配对连接，排队的取消操作不会消费 PIN。局域网发现仅在连接对话框及手动刷新时运行，单次约 1.5 秒 |
| APK 依赖与许可证 | Bouncy Castle 1.86 仅使用轻量 SRP 和 SHA-256 类，没有注册 provider；发布经过 R8 裁剪。APK 中确认包含 `assets/licenses/Bouncy-Castle-LICENSE.md`（1171 字节） |

真实服务通过 `HARDWARESCOPE_PC_INTEGRATION_FILE` 指向忽略发布的临时私有 JSON，其中包含 `address`、`pin`、`token` 与 `fingerprint`。测试输出不记录 PIN 或强凭据，不控制任何用户进程；测试任务在此变量存在时跳过 Gradle 结果缓存。正确 PIN 在一次测试中消费，重跑必须生成新的 PIN。

当前 `adb devices` 没有连接设备，未安装到用户手机，因此以上 JVM 与构建验证不等同于安卓实机界面、Android Keystore 或真实蓝牙配对兼容性验证。蓝牙的实际 RFCOMM 链路仍需要配对手机现场验证。

## 1.0.1 历史验证

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
