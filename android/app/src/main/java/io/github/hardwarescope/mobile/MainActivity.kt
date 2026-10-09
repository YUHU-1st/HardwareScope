package io.github.hardwarescope.mobile

import android.Manifest
import android.annotation.SuppressLint
import android.app.Activity
import android.app.AlertDialog
import android.bluetooth.BluetoothManager
import android.content.pm.PackageManager
import android.graphics.Color
import android.graphics.Typeface
import android.graphics.drawable.GradientDrawable
import android.os.Build
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.text.Editable
import android.text.InputType
import android.text.TextWatcher
import android.view.Gravity
import android.view.View
import android.view.ViewGroup
import android.view.WindowManager
import android.widget.*
import org.json.JSONArray
import org.json.JSONObject
import java.text.DecimalFormat
import java.time.Instant
import java.time.ZoneId
import java.time.format.DateTimeFormatter
import java.util.concurrent.Executors

class MainActivity : Activity() {
    private val bg = Color.rgb(16, 21, 30)
    private val surface = Color.rgb(27, 35, 47)
    private val textColor = Color.rgb(234, 241, 249)
    private val muted = Color.rgb(151, 167, 188)
    private val accent = Color.rgb(96, 212, 190)
    private val handler = Handler(Looper.getMainLooper())
    private val worker = Executors.newSingleThreadExecutor()
    private lateinit var store: SecureStore
    private lateinit var root: LinearLayout
    private lateinit var status: TextView
    private lateinit var content: LinearLayout
    private lateinit var scroll: ScrollView
    private lateinit var tabs: LinearLayout
    private var transport: Transport? = null
    private var config: ConnectionConfig? = null
    private var snapshot: JSONObject? = null
    private var pcSettings: JSONObject? = null
    private var connectionInfo: JSONObject? = null
    private var refreshCount = 0
    private var foreground = false
    private var connected = false
    private var busy = false
    private var page = 0
    private var hardwareCategory = "全部"
    private val expanded = mutableSetOf<String>()
    private var selectedSensor = ""
    private var temperatureDeviceId: String? = null
    private val history = mutableMapOf<String, ArrayDeque<Double?>>()
    private var lastSequence = -1L
    private var processSort = "cpuPercent"
    private var processQuery = ""
    private var visibleProcesses = 80
    private var processesPanel: LinearLayout? = null
    private var sessionGeneration = 0
    private val poll = Runnable { refresh() }
    private val categories = linkedMapOf("Motherboard" to "主板", "CPU" to "CPU", "GPU" to "GPU", "NPU" to "NPU", "Memory" to "内存", "VRAM" to "显存", "Network" to "网卡", "Storage" to "磁盘")

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        window.addFlags(WindowManager.LayoutParams.FLAG_SECURE)
        store = SecureStore(this)
        try { config = store.load() } catch (_: Exception) { Toast.makeText(this, "保存的密钥无法解密，请重新输入。", Toast.LENGTH_LONG).show() }
        root = column().apply { setBackgroundColor(bg) }
        if (Build.VERSION.SDK_INT >= 35) root.setOnApplyWindowInsetsListener { view, insets ->
            val systemBars = insets.getInsets(android.view.WindowInsets.Type.systemBars())
            view.setPadding(systemBars.left, systemBars.top, systemBars.right, systemBars.bottom)
            insets
        }
        val header = row().apply { setPadding(dp(18), dp(12), dp(14), dp(8)) }
        val heading = column()
        heading.addView(label("HardwareScope", 23f, textColor, true))
        status = label("尚未连接电脑", 12f, muted)
        heading.addView(status)
        header.addView(heading, LinearLayout.LayoutParams(0, -2, 1f))
        header.addView(button("连接") { connectionDialog() })
        root.addView(header)
        scroll = ScrollView(this).apply { isFillViewport = true }
        content = column().apply { setPadding(dp(16), dp(10), dp(16), dp(20)) }
        scroll.addView(content)
        root.addView(scroll, LinearLayout.LayoutParams(-1, 0, 1f))
        tabs = row().apply { setPadding(dp(4), dp(6), dp(4), dp(6)); setBackgroundColor(surface) }
        root.addView(tabs)
        setContentView(root)
        selectPage(0)
    }

    override fun onResume() {
        super.onResume(); foreground = true
        if (connected) handler.post(poll)
    }

    override fun onPause() {
        foreground = false; handler.removeCallbacks(poll)
        transport?.close()
        super.onPause()
    }

    override fun onDestroy() {
        handler.removeCallbacksAndMessages(null)
        (transport as? BluetoothTransport)?.dispose() ?: transport?.close()
        worker.shutdownNow()
        super.onDestroy()
    }

    private fun selectPage(index: Int) {
        page = index; processesPanel = null; tabs.removeAllViews()
        listOf("总览", "硬件", "应用", "调校", "设置").forEachIndexed { i, title ->
            tabs.addView(button(title, i == page) { selectPage(i) }, LinearLayout.LayoutParams(0, dp(48), 1f))
        }
        renderPage()
        scroll.scrollTo(0, 0)
    }

    private fun renderPage(preserveScroll: Boolean = false) {
        if (page == 2 && processesPanel != null && preserveScroll) { renderProcesses(); return }
        val oldScroll = if (preserveScroll) scroll.scrollY else 0
        content.removeAllViews()
        if (page == 4) renderSettings() else if (snapshot == null) {
            val card = card("电脑性能，随手查看")
            card.addView(label("在电脑端启用手机连接，复制 HTTPS 地址、证书指纹和访问密钥；也可在系统设置中配对蓝牙后连接。", 15f, muted))
            card.addView(button("连接电脑", true) { connectionDialog() })
            card.addView(label("仅在此页面位于前台时同步，每 2 秒请求一次。应用切到后台后停止同步。", 12f, muted))
        } else when (page) {
            0 -> renderOverview()
            1 -> renderHardware()
            2 -> renderApplications()
            3 -> renderTuning()
        }
        if (preserveScroll) scroll.post { scroll.scrollTo(0, oldScroll) }
    }

    private fun hardware() = snapshot?.optJSONArray("hardware").objects()
    private fun sensors(item: JSONObject) = item.optJSONArray("sensors").objects()

    private fun renderOverview() {
        val snap = snapshot!!
        title("性能总览", "${snap.optString("machineName")} · ${if (snap.optBoolean("isAdministrator")) "管理员采样" else "普通用户采样"}")
        val items = hardware()
        listOf("CPU", "GPU", "NPU", "Memory", "VRAM", "Network").forEach { category ->
            val group = items.filter { it.optString("category") == category }
            val card = card(categories[category] ?: category)
            if (group.isEmpty()) card.addView(label("未检测到此硬件或驱动未暴露读数", 13f, muted))
            else group.forEach { item ->
                card.addView(label(item.optString("name"), 14f, textColor, true))
                val all = sensors(item)
                val load = all.firstOrNull { it.optString("kind") == "Load" && Regex("total|总体|总占用|memory", RegexOption.IGNORE_CASE).containsMatchIn(it.optString("name")) }
                    ?: all.firstOrNull { it.optString("kind") == "Load" }
                val shown = listOfNotNull(load, temperatureReadings(item).firstOrNull(), all.firstOrNull { it.optString("kind") == "Power" }, all.firstOrNull { it.optString("kind") == "Throughput" })
                if (shown.isEmpty()) card.addView(label("驱动未提供实时传感器数据", 12f, muted))
                shown.forEach { sensor ->
                    metric(card, sensor)
                    if (sensor === load && sensor.number("value") != null) {
                        val progress = ProgressBar(this, null, android.R.attr.progressBarStyleHorizontal).apply { max = 100; progress = (sensor.number("value") ?: 0.0).toInt().coerceIn(0, 100); progressTintList = android.content.res.ColorStateList.valueOf(accent) }
                        card.addView(progress, LinearLayout.LayoutParams(-1, dp(6)))
                    }
                }
                card.setOnClickListener { hardwareCategory = category; temperatureDeviceId = null; selectPage(1) }
            }
        }
        content.addView(label("设备温度", 22f, textColor, true))
        content.addView(label("逐设备显示温度来源；点击读数查看该设备的全部温度与趋势。", 12f, muted).apply { setPadding(0, dp(5), 0, dp(12)) })
        categories.forEach { (category, name) ->
            val group = items.filter { it.optString("category") == category }
            val card = card("$name · 温度")
            if (group.isEmpty()) card.addView(label("温度：未提供\n电脑未检测到此类设备，无法读取温度。", 13f, muted))
            group.forEach { item ->
                val deviceId = item.optString("id")
                card.addView(label(item.optString("name"), 14f, textColor, true).apply { setPadding(0, dp(8), 0, dp(3)) })
                val temperatures = temperatureReadings(item)
                fun openTemperature(sensorId: String = temperatures.firstOrNull()?.optString("id").orEmpty()) {
                    hardwareCategory = category; temperatureDeviceId = deviceId; selectedSensor = sensorId; expanded.add(deviceId); selectPage(1)
                }
                if (temperatures.isEmpty()) {
                    val unavailable = label("温度：未提供\n设备或驱动未公开温度读数；点击查看设备详情。", 12f, muted)
                    unavailable.setOnClickListener { openTemperature() }
                    card.addView(unavailable)
                } else {
                    temperatures.take(2).forEach { sensor ->
                        metric(card, sensor) { openTemperature(sensor.optString("id")) }
                        val source = sensor.optString("source").takeIf { it.isNotBlank() && it != "null" } ?: "未提供来源"
                        val reason = sensor.optString("unavailableReason").takeIf { it.isNotBlank() && it != "null" }
                        card.addView(label("来源：$source" + if (sensor.number("value") == null) "\n${reason ?: "设备或驱动未公开此温度读数。"}" else "", 10f, muted))
                    }
                    if (temperatures.size > 2) {
                        val readable = temperatures.count { it.number("value") != null }
                        card.addView(button("全部 ${temperatures.size} 个来源 · $readable 个可读") { openTemperature() })
                    }
                }
            }
        }
        val diagnostics = snap.optJSONObject("diagnostics")
        val efficiency = card("采样开销")
        efficiency.addView(label("电脑监控进程 CPU ${format(diagnostics?.number("monitorCpuPercent"), "%")} · 内存 ${format(diagnostics?.number("monitorMemoryMb"), "MB")}\n采样耗时 ${format(diagnostics?.number("sampleDurationMs"), "ms")} · 间隔 ${diagnostics?.optInt("intervalMs") ?: 0} ms", 12f, muted))
        val notices = snap.optJSONArray("notices")
        if (notices != null && notices.length() > 0) {
            val notes = card("采样说明")
            for (i in 0 until notices.length()) notes.addView(label("• ${notices.optString(i)}", 12f, muted))
        }
    }

    private fun renderHardware() {
        val temperatureView = temperatureDeviceId != null
        title(if (temperatureView) "设备温度" else "硬件与传感器", "点击传感器查看趋势；缺失读数显示为“未提供”。")
        val filters = row()
        (listOf("全部") + categories.keys).forEach { category -> filters.addView(button(categories[category] ?: category, hardwareCategory == category) { hardwareCategory = category; temperatureDeviceId = null; selectedSensor = ""; renderPage() }) }
        content.addView(HorizontalScrollView(this).apply { isHorizontalScrollBarEnabled = false; addView(filters) })
        if (temperatureView) content.addView(button("查看此分类全部传感器") { temperatureDeviceId = null; renderPage() })
        val items = hardware().filter { (hardwareCategory == "全部" || it.optString("category") == hardwareCategory) && (!temperatureView || it.optString("id") == temperatureDeviceId) }
        val allSensors = items.flatMap { if (temperatureView) temperatureReadings(it) else sensors(it) }
        val chosen = allSensors.firstOrNull { it.optString("id") == selectedSensor } ?: allSensors.firstOrNull { it.number("value") != null } ?: allSensors.firstOrNull()
        if (chosen != null) {
            val owner = items.firstOrNull { item -> sensors(item).any { it.optString("id") == chosen.optString("id") } }
            val trend = card("${owner?.optString("name").orEmpty()} · ${chosen.optString("name")} · ${value(chosen)}")
            trend.addView(HistoryChart(this).apply { values = history[chosen.optString("id")]?.toList().orEmpty(); unit = chosen.optString("unit") }, LinearLayout.LayoutParams(-1, dp(180)))
            trend.addView(label("保留本次连接最近 90 次电脑采样，缺失值留空。", 11f, muted))
        }
        if (items.isEmpty()) card("没有此类硬件").addView(label("电脑未检测到此硬件，或驱动未暴露数据。", 13f, muted))
        items.forEach { item ->
            val id = item.optString("id")
            val card = card("${categories[item.optString("category")] ?: item.optString("category")} · ${item.optString("name")}")
            card.addView(label(item.optString("status"), 12f, muted))
            val all = if (temperatureView) temperatureReadings(item) else sensors(item)
            val visible = if (temperatureView || expanded.contains(id)) all else all.take(8)
            if (temperatureView && all.isEmpty()) card.addView(label("温度：未提供\n设备或驱动未公开温度读数。", 13f, muted))
            visible.forEach { sensor ->
                metric(card, sensor) { selectedSensor = sensor.optString("id"); renderPage(); scroll.post { scroll.smoothScrollTo(0, 0) } }
                if (temperatureView || expanded.contains(id)) {
                    val source = sensor.optString("source").takeIf { it.isNotBlank() && it != "null" } ?: "未提供来源"
                    val reason = sensor.optString("unavailableReason").takeIf { it.isNotBlank() && it != "null" }
                    card.addView(label("来源：$source" + if (sensor.number("value") == null) "\n${reason ?: "设备或驱动未公开此读数。"}" else "", 10f, muted))
                }
            }
            if (!temperatureView && all.size > 8) card.addView(button(if (expanded.contains(id)) "收起" else "查看全部 ${all.size} 个传感器") { if (!expanded.add(id)) expanded.remove(id); renderPage(true) })
            val oc = item.optJSONObject("overclock")
            if (oc != null && !temperatureView) card.addView(label("超频状态：${overclockState(oc.optString("state"))}\n${oc.optString("evidence")}", 12f, muted))
        }
    }

    private fun renderApplications() {
        title("应用与进程", "按电脑采样数据排序。点击应用可调整优先级或结束进程。")
        val search = EditText(this).apply { setSingleLine(); hint = "搜索应用名称或 PID"; setText(processQuery); setTextColor(textColor); setHintTextColor(muted); textSize = 14f }
        content.addView(search)
        search.addTextChangedListener(object : TextWatcher {
            override fun beforeTextChanged(s: CharSequence?, start: Int, count: Int, after: Int) {}
            override fun onTextChanged(s: CharSequence?, start: Int, before: Int, count: Int) { processQuery = s.toString(); visibleProcesses = 80; renderProcesses() }
            override fun afterTextChanged(s: Editable?) {}
        })
        val sortFields = linkedMapOf("CPU 占用" to "cpuPercent", "内存" to "memoryMb", "GPU 占用" to "gpuPercent", "NPU 占用" to "npuPercent", "显存" to "vramMb", "I/O 读取" to "ioReadBytesPerSecond", "I/O 写入" to "ioWriteBytesPerSecond", "网络速率" to "networkBytesPerSecond")
        val spinner = Spinner(this).apply { adapter = ArrayAdapter(this@MainActivity, android.R.layout.simple_spinner_dropdown_item, sortFields.keys.toList()); setSelection(sortFields.values.indexOf(processSort).coerceAtLeast(0)) }
        spinner.onItemSelectedListener = object : AdapterView.OnItemSelectedListener {
            override fun onItemSelected(parent: AdapterView<*>?, view: View?, position: Int, id: Long) { processSort = sortFields.values.elementAt(position); visibleProcesses = 80; renderProcesses() }
            override fun onNothingSelected(parent: AdapterView<*>?) {}
        }
        content.addView(spinner)
        processesPanel = column().also { content.addView(it) }
        renderProcesses()
    }

    private fun renderProcesses() {
        val panel = processesPanel ?: return
        panel.removeAllViews()
        val processes = snapshot?.optJSONArray("processes").objects().filter { process -> process.optString("name").contains(processQuery, true) || process.optInt("pid").toString().contains(processQuery) }
            .sortedWith(compareByDescending<JSONObject> { it.number(processSort) ?: Double.NEGATIVE_INFINITY }.thenBy { it.optInt("pid") })
        panel.addView(label("${processes.size} 个进程 · 从高到低 · 未提供的数据排在最后", 11f, muted))
        processes.take(visibleProcesses).forEach { process ->
            val card = card("${process.optString("name")}  ·  ${process.optInt("pid")}", panel)
            card.addView(label("CPU ${format(process.number("cpuPercent"), "%")}     内存 ${format(process.number("memoryMb"), "MB")}\nGPU ${format(process.number("gpuPercent"), "%")}     NPU ${format(process.number("npuPercent"), "%")}\n显存 ${format(process.number("vramMb"), "MB")}     优先级 ${priorityName(process.optString("priority"))}\n读取 ${rate(process.number("ioReadBytesPerSecond"))} · 写入 ${rate(process.number("ioWriteBytesPerSecond"))} · 网络 ${rate(process.number("networkBytesPerSecond"))}", 12f, muted))
            card.contentDescription = "${process.optString("name")}，PID ${process.optInt("pid")}，点击查看操作"
            card.setOnClickListener { processDialog(process) }
        }
        if (processes.size > visibleProcesses) panel.addView(button("继续显示 ${minOf(80, processes.size - visibleProcesses)} 个进程") { visibleProcesses += 80; renderProcesses() })
    }

    private fun processDialog(process: JSONObject) {
        val title = "${process.optString("name")}（PID ${process.optInt("pid")}）"
        val details = "${process.optString("path")}\n优先级：${priorityName(process.optString("priority"))}"
        if (!process.optBoolean("canControl")) { AlertDialog.Builder(this).setTitle(title).setMessage("$details\n${process.optString("controlReason").ifBlank { "此进程不允许远程控制。" }}").setPositiveButton("关闭", null).show(); return }
        AlertDialog.Builder(this).setTitle(title).setMessage(details)
            .setPositiveButton("优先级") { _, _ -> priorityDialog(process) }
            .setNegativeButton("结束进程") { _, _ ->
                AlertDialog.Builder(this).setTitle("结束 ${process.optString("name")}？").setMessage("未保存的数据可能丢失。将请求电脑结束 PID ${process.optInt("pid")} 对应的当前进程实例。")
                    .setPositiveButton("确认结束") { _, _ -> command("/api/processes/${process.optInt("pid")}/terminate", JSONObject().put("instanceId", process.optString("instanceId"))) }
                    .setNegativeButton("取消", null).show()
            }.setNeutralButton("取消", null).show()
    }

    private fun priorityDialog(process: JSONObject) {
        val priorities = listOf("Idle", "BelowNormal", "Normal", "AboveNormal", "High")
        AlertDialog.Builder(this).setTitle("设置 ${process.optString("name")} 的优先级")
            .setItems(priorities.map { priorityName(it) }.toTypedArray()) { _, index ->
                AlertDialog.Builder(this).setTitle("确认调整优先级").setMessage("将 ${process.optString("name")} 设置为“${priorityName(priorities[index])}”。高优先级可能影响其他应用响应。")
                    .setPositiveButton("设置") { _, _ -> command("/api/processes/${process.optInt("pid")}/priority", JSONObject().put("instanceId", process.optString("instanceId")).put("priority", priorities[index])) }
                    .setNegativeButton("取消", null).show()
            }.setNegativeButton("取消", null).show()
    }

    private fun renderTuning() {
        title("超频与调校", "运行调校软件只构成线索，不能证明该软件改变了频率。")
        hardware().forEach { item ->
            val oc = item.optJSONObject("overclock") ?: return@forEach
            val card = card(item.optString("name"))
            card.addView(label(overclockState(oc.optString("state")), 18f, accent, true))
            card.addView(label("频率偏移 ${format(oc.number("offsetMHz"), "MHz")} · 幅度 ${format(oc.number("percent"), "%")}\n依据：${oc.optString("evidence").ifBlank { "未提供" }}\n归因：${oc.optString("attribution").ifBlank { "无法确认" }}", 13f, muted))
        }
        val tools = snapshot?.optJSONArray("tuningTools").objects()
        val card = card("调校软件线索")
        if (tools.isEmpty()) card.addView(label("未发现已知调校软件的运行线索。", 13f, muted))
        tools.forEach { tool -> card.addView(label("${tool.optString("name")}\n${tool.optString("evidence")}\n${tool.optString("attribution")}", 13f, muted)) }
        card.addView(label("动态睿频会超过基础频率，单凭当前频率无法判定超频。此页面用于查看证据，不改写硬件频率。", 12f, muted))
    }

    private fun renderSettings() {
        title("连接与设置", "访问密钥以 Android Keystore 加密保存在本机。")
        val connection = card("手机连接")
        connection.addView(label(if (config == null) "尚未配置电脑" else "${config!!.mode} · ${config!!.address}", 13f, muted))
        connection.addView(button("更改连接") { connectionDialog() })
        connectionInfo?.let { info -> connection.addView(label("电脑 HTTPS：${if (info.optBoolean("remoteEnabled")) "已运行" else "未运行"} · 控制权限：${if (info.optBoolean("remoteControlEnabled")) "允许" else "关闭"}\n电脑蓝牙：${info.optString("bluetoothStatus")}", 12f, muted)) }
        if (connected) connection.addView(button("断开连接") { disconnect(); renderPage() })
        connection.addView(button("清除保存的连接") {
            AlertDialog.Builder(this).setTitle("清除手机连接？").setMessage("将断开连接并移除手机保存的电脑地址、证书指纹和访问密钥。")
                .setPositiveButton("清除") { _, _ -> disconnect(); store.clear(); config = null; snapshot = null; history.clear(); lastSequence = -1; renderPage() }.setNegativeButton("取消", null).show()
        })
        val settings = pcSettings
        if (settings == null) { card("电脑端设置").addView(label("连接电脑后读取和修改。", 13f, muted)); return }
        val card = card("电脑端设置")
        card.addView(button("从电脑重新读取设置") {
            runRequest { current ->
                val updated = current.request("GET", "/api/settings")
                val info = current.request("GET", "/api/connection")
                handler.post { pcSettings = updated; connectionInfo = info; renderPage() }
            }
        })
        val interval = EditText(this).apply { hint = "采样间隔（毫秒）"; inputType = InputType.TYPE_CLASS_NUMBER; setText(settings.optInt("samplingIntervalMs", 2000).toString()); setTextColor(textColor) }
        card.addView(label("采样间隔（1000–10000 毫秒）", 12f, muted)); card.addView(interval)
        val toggles = linkedMapOf<String, Switch>()
        linkedMapOf("startWithWindows" to "开机自动启动", "minimizeToTray" to "关闭窗口后保留任务栏托盘", "remoteEnabled" to "允许 HTTPS 手机连接", "remoteControlEnabled" to "允许远程修改设置和控制进程", "bluetoothEnabled" to "允许蓝牙手机连接").forEach { (key, name) ->
            val toggle = Switch(this).apply { text = name; setTextColor(textColor); textSize = 14f; isChecked = settings.optBoolean(key); setPadding(0, dp(10), 0, dp(10)) }
            toggles[key] = toggle; card.addView(toggle)
        }
        val port = EditText(this).apply { inputType = InputType.TYPE_CLASS_NUMBER; setText(settings.optInt("port", 47831).toString()); setTextColor(textColor) }
        card.addView(label("HTTPS 端口（更改后须重新连接）", 12f, muted)); card.addView(port)
        card.addView(button("保存电脑设置", true) {
            val ms = interval.text.toString().toIntOrNull()
            val portNumber = port.text.toString().toIntOrNull()
            if (ms == null || ms !in 1000..10000 || portNumber == null || portNumber !in 1024..65535) { showError("采样间隔应为 1000–10000 ms，端口应为 1024–65535。"); return@button }
            val proposed = JSONObject(settings.toString()).put("samplingIntervalMs", ms).put("port", portNumber)
            toggles.forEach { (key, toggle) -> proposed.put(key, toggle.isChecked) }
            val breaksConnection = settings.optInt("port") != portNumber || (config?.mode == "HTTPS" && !proposed.optBoolean("remoteEnabled")) || (config?.mode == "Bluetooth" && !proposed.optBoolean("bluetoothEnabled"))
            AlertDialog.Builder(this).setTitle("保存电脑端设置？").setMessage(if (breaksConnection) "这些设置会改变当前连接。保存后请在电脑端核对连接信息并重新连接。" else "这些设置将立即应用到连接的电脑。")
                .setPositiveButton("保存") { _, _ -> saveSettings(proposed, breaksConnection) }.setNegativeButton("取消", null).show()
        })
        card.addView(label("设备是否支持各项温度、功耗与性能限制信息取决于电脑硬件和驱动。\nvivo 跨网络连接尚无公开 SDK 集成；可通过自己配置的 VPN 使用已固定证书的 HTTPS 地址。", 12f, muted))
    }

    private fun saveSettings(proposed: JSONObject, breaksConnection: Boolean) {
        runRequest { current ->
            val response = current.request("PUT", "/api/settings", proposed)
            if (response.has("success") && !response.optBoolean("success")) throw IllegalStateException(response.optString("message"))
            handler.post {
                pcSettings = proposed
                Toast.makeText(this, "电脑设置已保存", Toast.LENGTH_SHORT).show()
                if (breaksConnection) disconnect()
                renderPage()
            }
        }
    }

    private fun command(path: String, body: JSONObject) {
        runRequest { current ->
            val response = current.request("POST", path, body)
            if (!response.optBoolean("success")) throw IllegalStateException(response.optString("message").ifBlank { "操作未成功。" })
            handler.post { Toast.makeText(this, response.optString("message").ifBlank { "操作完成" }, Toast.LENGTH_SHORT).show(); handler.removeCallbacks(poll); if (foreground && connected) handler.post(poll) }
        }
    }

    private fun runRequest(action: (Transport) -> Unit) {
        if (!connected || transport == null) { showError("请先连接电脑。"); return }
        if (busy) { showError("正在同步，请稍后再试。"); return }
        val current = transport ?: return
        busy = true; handler.removeCallbacks(poll)
        val generation = sessionGeneration
        worker.execute {
            try { action(current) } catch (e: Exception) { handler.post { if (generation == sessionGeneration) showError(errorMessage(e)) } }
            finally { handler.post { busy = false; if (connected && foreground) { handler.removeCallbacks(poll); handler.postDelayed(poll, 2000) } } }
        }
    }

    private fun refresh() {
        if (!connected || !foreground || busy) return
        busy = true
        val generation = sessionGeneration
        val current = transport ?: return
        worker.execute {
            try {
                val data = current.request("GET", "/api/snapshot")
                val readConfiguration = pcSettings == null || refreshCount++ % 5 == 0
                val settings = if (readConfiguration) current.request("GET", "/api/settings") else null
                val info = if (readConfiguration) current.request("GET", "/api/connection") else null
                handler.post {
                    if (generation != sessionGeneration) return@post
                    val firstSettings = pcSettings == null
                    snapshot = data; if (settings != null) pcSettings = settings; if (info != null) connectionInfo = info
                    recordHistory(data)
                    val timestamp = try { DateTimeFormatter.ofPattern("HH:mm:ss").withZone(ZoneId.systemDefault()).format(Instant.parse(data.optString("timestamp"))) } catch (_: Exception) { "刚刚" }
                    status.text = "${data.optString("machineName")} · ${config?.mode} · $timestamp"
                    if (page != 4 || firstSettings) renderPage(true)
                }
            } catch (e: Exception) {
                handler.post { if (generation == sessionGeneration) status.text = "同步失败：${errorMessage(e)} · 将重试" }
            } finally {
                handler.post { busy = false; if (connected && foreground) { handler.removeCallbacks(poll); handler.postDelayed(poll, 2000) } }
            }
        }
    }

    private fun recordHistory(data: JSONObject) {
        val sequence = data.optLong("sequence")
        if (sequence == lastSequence) return
        lastSequence = sequence
        val seen = mutableSetOf<String>()
        data.optJSONArray("hardware").objects().forEach { item -> sensors(item).forEach { sensor ->
            val id = sensor.optString("id"); seen.add(id)
            val samples = history.getOrPut(id) { ArrayDeque() }
            if (samples.size >= 90) samples.removeFirst()
            samples.addLast(sensor.number("value"))
        } }
        history.keys.retainAll(seen)
    }

    private fun disconnect() {
        connected = false; sessionGeneration++; handler.removeCallbacks(poll)
        (transport as? BluetoothTransport)?.dispose() ?: transport?.close()
        transport = null; pcSettings = null; connectionInfo = null
        status.text = "已断开 · 页面保留上次读数"
    }

    private fun connect(newConfig: ConnectionConfig) {
        try {
            val validated = newConfig.validated()
            store.save(validated)
            disconnect(); config = validated; history.clear(); snapshot = null; lastSequence = -1L; refreshCount = 0
            transport = if (validated.mode == "Bluetooth") BluetoothTransport(this, validated) else HttpsTransport(validated)
            connected = true; status.text = "正在连接 ${validated.address}…"; renderPage()
            handler.post(poll)
        } catch (e: Exception) { showError(errorMessage(e)) }
    }

    private fun connectionDialog() {
        val form = column().apply { setPadding(dp(20), dp(8), dp(20), 0) }
        val modes = Spinner(this).apply { adapter = ArrayAdapter(this@MainActivity, android.R.layout.simple_spinner_dropdown_item, listOf("HTTPS（局域网 / VPN）", "蓝牙（已配对电脑）")); setSelection(if (config?.mode == "Bluetooth") 1 else 0) }
        form.addView(modes)
        val address = EditText(this).apply { hint = "https://192.168.1.10:47831"; inputType = InputType.TYPE_CLASS_TEXT or InputType.TYPE_TEXT_VARIATION_URI; setSingleLine(); if (config?.mode == "HTTPS") setText(config?.address) }
        form.addView(address)
        val fingerprint = EditText(this).apply { hint = "电脑证书 SHA-256 指纹"; inputType = InputType.TYPE_CLASS_TEXT or InputType.TYPE_TEXT_FLAG_NO_SUGGESTIONS; setText(config?.fingerprint.orEmpty()); textSize = 12f }
        form.addView(fingerprint)
        val bluetoothAddress = label(if (config?.mode == "Bluetooth") config?.address.orEmpty() else "尚未选择电脑", 13f, muted)
        form.addView(bluetoothAddress)
        val paired = button("选择已配对电脑") { selectPairedDevice { bluetoothAddress.text = it } }
        form.addView(paired)
        val token = EditText(this).apply { hint = "电脑端访问密钥"; inputType = InputType.TYPE_CLASS_TEXT or InputType.TYPE_TEXT_VARIATION_PASSWORD; setSingleLine(); setText(config?.token.orEmpty()) }
        form.addView(token)
        form.addView(label("从电脑端连接页手动填写。蓝牙先在手机系统设置中与电脑配对。访问密钥用于所有请求。", 12f, muted))
        modes.onItemSelectedListener = object : AdapterView.OnItemSelectedListener {
            override fun onItemSelected(parent: AdapterView<*>?, view: View?, position: Int, id: Long) {
                address.visibility = if (position == 0) View.VISIBLE else View.GONE
                fingerprint.visibility = address.visibility
                paired.visibility = if (position == 1) View.VISIBLE else View.GONE
                bluetoothAddress.visibility = paired.visibility
            }
            override fun onNothingSelected(parent: AdapterView<*>?) {}
        }
        val dialog = AlertDialog.Builder(this).setTitle("连接电脑").setView(ScrollView(this).apply { addView(form) }).setPositiveButton("保存并连接", null).setNegativeButton("取消", null).create()
        dialog.setOnShowListener {
            dialog.getButton(AlertDialog.BUTTON_POSITIVE).setOnClickListener {
                if (modes.selectedItemPosition == 1 && !hasBluetoothPermission()) { requestBluetoothPermission(); return@setOnClickListener }
                try {
                    val newConfig = ConnectionConfig(if (modes.selectedItemPosition == 0) "HTTPS" else "Bluetooth", if (modes.selectedItemPosition == 0) address.text.toString() else bluetoothAddress.text.toString(), fingerprint.text.toString(), token.text.toString()).validated()
                    connect(newConfig); dialog.dismiss()
                } catch (e: Exception) { showError(errorMessage(e)) }
            }
        }
        dialog.show()
    }

    private fun hasBluetoothPermission() = Build.VERSION.SDK_INT < 31 || checkSelfPermission(Manifest.permission.BLUETOOTH_CONNECT) == PackageManager.PERMISSION_GRANTED
    private fun requestBluetoothPermission() { if (Build.VERSION.SDK_INT >= 31) requestPermissions(arrayOf(Manifest.permission.BLUETOOTH_CONNECT), 1) }

    @SuppressLint("MissingPermission")
    private fun selectPairedDevice(selected: (String) -> Unit) {
        if (!hasBluetoothPermission()) { requestBluetoothPermission(); return }
        val adapter = getSystemService(BluetoothManager::class.java)?.adapter
        if (adapter == null || !adapter.isEnabled) { showError("请在手机系统设置中打开蓝牙，并与电脑完成配对。"); return }
        val devices = adapter.bondedDevices.sortedBy { it.name.orEmpty() }
        if (devices.isEmpty()) { showError("未找到已配对设备，请先在系统蓝牙设置中与电脑配对。"); return }
        AlertDialog.Builder(this).setTitle("选择已配对的电脑").setItems(devices.map { "${it.name ?: "蓝牙设备"}\n${it.address}" }.toTypedArray()) { _, index -> selected(devices[index].address) }.setNegativeButton("取消", null).show()
    }

    private fun title(name: String, description: String) { content.addView(label(name, 24f, textColor, true)); content.addView(label(description, 12f, muted).apply { setPadding(0, dp(5), 0, dp(12)) }) }
    private fun card(title: String, parent: LinearLayout = content): LinearLayout {
        val card = column().apply { background = rounded(surface); setPadding(dp(14), dp(12), dp(14), dp(12)) }
        card.addView(label(title, 16f, textColor, true).apply { setPadding(0, 0, 0, dp(8)) })
        parent.addView(card, LinearLayout.LayoutParams(-1, -2).apply { bottomMargin = dp(12) })
        return card
    }
    private fun metric(parent: LinearLayout, sensor: JSONObject, click: (() -> Unit)? = null) {
        val metric = row().apply { setPadding(0, dp(7), 0, dp(7)) }
        metric.addView(label(sensor.optString("name"), 13f, muted), LinearLayout.LayoutParams(0, -2, 1f))
        val v = label(value(sensor), 15f, if (sensor.optString("kind") == "Limit" && (sensor.number("value") ?: 0.0) > 0) Color.rgb(255, 185, 109) else textColor, true)
        metric.addView(v)
        metric.contentDescription = "${sensor.optString("name")}：${value(sensor)}"
        if (click != null) metric.setOnClickListener { click() }
        parent.addView(metric)
    }
    private fun label(text: String, size: Float, color: Int, bold: Boolean = false) = TextView(this).apply { this.text = text; textSize = size; setTextColor(color); if (bold) setTypeface(typeface, Typeface.BOLD); setLineSpacing(dp(3).toFloat(), 1f) }
    private fun button(text: String, selected: Boolean = false, action: () -> Unit) = Button(this).apply { this.text = text; textSize = 13f; isAllCaps = false; setTextColor(if (selected) bg else accent); background = rounded(if (selected) accent else surface); minHeight = dp(44); minimumHeight = dp(44); setPadding(dp(12), dp(7), dp(12), dp(7)); setOnClickListener { action() }; layoutParams = LinearLayout.LayoutParams(-2, dp(44)).apply { topMargin = dp(6); rightMargin = dp(4) } }
    private fun rounded(color: Int) = GradientDrawable().apply { setColor(color); cornerRadius = dp(12).toFloat() }
    private fun column() = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL }
    private fun row() = LinearLayout(this).apply { orientation = LinearLayout.HORIZONTAL; gravity = Gravity.CENTER_VERTICAL }
    private fun dp(value: Int) = (value * resources.displayMetrics.density + 0.5f).toInt()
    private fun showError(message: String) { if (!isFinishing) AlertDialog.Builder(this).setTitle("操作提示").setMessage(message).setPositiveButton("知道了", null).show() }
    private fun errorMessage(error: Exception): String {
        return when (error) {
            is javax.net.ssl.SSLException -> "TLS 校验失败，请核对电脑证书指纹和手机时间。"
            is java.net.SocketTimeoutException -> "电脑连接超时，请检查地址、网络和防火墙。"
            is java.net.ConnectException -> "无法连接电脑，请检查服务是否启动及网络是否可达。"
            is SecurityException -> "没有蓝牙连接权限，请授权“附近的设备”。"
            else -> error.message?.take(180) ?: "连接失败，请检查电脑服务与网络。"
        }
    }
    private fun priorityName(name: String) = mapOf("Idle" to "低", "BelowNormal" to "低于正常", "Normal" to "正常", "AboveNormal" to "高于正常", "High" to "高", "RealTime" to "实时")[name] ?: name
    private fun overclockState(name: String) = mapOf("Unknown" to "无法确认", "Detected" to "检测到超频依据", "Overclocked" to "检测到超频", "NotDetected" to "未检测到超频", "Unsupported" to "未提供证据", "Confirmed" to "已确认超频", "Stock" to "默认参数", "Suspected" to "疑似调校", "FactoryBoost" to "动态睿频")[name] ?: name.ifBlank { "无法确认" }
    private fun value(sensor: JSONObject): String {
        val number = sensor.number("value") ?: return "未提供"
        if (sensor.optString("kind") == "Limit") return if (number > 0.0) "已触发" else "未触发"
        return format(number, sensor.optString("unit"))
    }
    private fun format(value: Double?, unit: String) = if (value == null) "未提供" else "${DecimalFormat("#,##0.#").format(value)} $unit".trim()
    private fun rate(value: Double?): String = when {
        value == null -> "未提供"
        value >= 1024 * 1024 -> format(value / (1024 * 1024), "MB/s")
        value >= 1024 -> format(value / 1024, "KB/s")
        else -> format(value, "B/s")
    }
}

private fun JSONArray?.objects(): List<JSONObject> = if (this == null) emptyList() else (0 until length()).mapNotNull { optJSONObject(it) }
private fun JSONObject.number(key: String): Double? = if (!has(key) || isNull(key)) null else (opt(key) as? Number)?.toDouble()?.takeIf { it.isFinite() }

internal fun temperatureReadings(device: JSONObject): List<JSONObject> = device.optJSONArray("sensors").objects()
    .filter { it.optString("kind") == "Temperature" }
    .sortedWith(compareByDescending<JSONObject> { it.number("value") != null }.thenBy { sensor ->
        val name = sensor.optString("name")
        when {
            name.contains("Tctl", true) || (name.contains("Tdie", true) && !name.contains("CCD", true)) -> 0
            name.contains("Composite", true) -> 0
            device.optString("category") == "GPU" && (name.contains("GPU", true) || name.contains("核心", true)) && !name.contains("memory", true) && !name.contains("内存") && !name.contains("显存") && !name.contains("hot spot", true) && !name.contains("hotspot", true) && !name.contains("热点") -> 0
            else -> 1
        }
    }.thenByDescending { it.number("value") ?: Double.NEGATIVE_INFINITY })
