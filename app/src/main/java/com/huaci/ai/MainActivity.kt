package com.huaci.ai

import android.Manifest
import android.app.Activity
import android.content.Intent
import android.content.pm.PackageManager
import android.net.Uri
import android.os.Build
import android.os.Bundle
import android.provider.Settings
import android.widget.CheckBox
import android.widget.EditText
import android.widget.RadioGroup
import android.widget.TextView
import android.widget.Toast

/** 设置页：权限、AI 接口、预设问题、开关悬浮球。 */
class MainActivity : Activity() {

    private lateinit var permStatus: TextView
    private lateinit var btnPerm: TextView
    private lateinit var provider: RadioGroup
    private lateinit var providerHint: TextView
    private lateinit var baseUrl: EditText
    private lateinit var apiKey: EditText
    private lateinit var model: EditText
    private lateinit var presets: EditText
    private lateinit var autoRun: CheckBox
    private lateinit var testResult: TextView
    private lateinit var deepseekBox: android.view.View
    private lateinit var deepThink: CheckBox
    private lateinit var clipWatchStatus: TextView
    private var testClient: AiClient? = null

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(R.layout.activity_main)

        permStatus = findViewById(R.id.permStatus)
        btnPerm = findViewById(R.id.btnPerm)
        provider = findViewById(R.id.provider)
        providerHint = findViewById(R.id.providerHint)
        baseUrl = findViewById(R.id.baseUrl)
        apiKey = findViewById(R.id.apiKey)
        model = findViewById(R.id.model)
        presets = findViewById(R.id.presets)
        autoRun = findViewById(R.id.autoRun)
        testResult = findViewById(R.id.testResult)
        findViewById<RadioGroup>(R.id.modeGroup).apply {
            check(if (Prefs.mode(this@MainActivity) == Prefs.MODE_QUICK) R.id.modeQuick else R.id.modeFull)
            setOnCheckedChangeListener { _, id ->
                val quick = id == R.id.modeQuick
                Prefs.setMode(this@MainActivity, if (quick) Prefs.MODE_QUICK else Prefs.MODE_FULL)
                toast(if (quick) "已切换到中译英速查" else "已切换到完整助手")
            }
        }
        clipWatchStatus = findViewById(R.id.clipWatchStatus)
        findViewById<CheckBox>(R.id.clipCheck).apply {
            isChecked = Prefs.clipCheck(this@MainActivity)
            setOnCheckedChangeListener { _, on -> Prefs.setClipCheck(this@MainActivity, on) }
        }
        findViewById<TextView>(R.id.btnClipWatch).setOnClickListener {
            try { startActivity(Intent(Settings.ACTION_ACCESSIBILITY_SETTINGS)) }
            catch (_: Exception) { toast("打不开无障碍设置，请到系统设置里手动找") }
        }
        deepseekBox = findViewById(R.id.deepseekBox)
        deepThink = findViewById(R.id.deepThink)
        deepThink.isChecked = Prefs.deepThink(this)
        findViewById<CheckBox>(R.id.webSearch).apply {
            isChecked = Prefs.webSearch(this@MainActivity)
            setOnCheckedChangeListener { _, on -> Prefs.setWebSearch(this@MainActivity, on) }
        }
        findViewById<TextView>(R.id.pickFlash).setOnClickListener { model.setText("deepseek-flash") }
        findViewById<TextView>(R.id.pickPro).setOnClickListener { model.setText("deepseek-v4-pro") }

        // 回填已保存的设置
        val p = Prefs.provider(this)
        provider.check(when (p) {
            Prefs.ANTHROPIC -> R.id.pAnthropic
            Prefs.DEEPSEEK -> R.id.pDeepseek
            else -> R.id.pOpenai
        })
        baseUrl.setText(Prefs.baseUrl(this))
        apiKey.setText(Prefs.apiKey(this))
        model.setText(Prefs.model(this))
        presets.setText(Prefs.presetsText(this))
        autoRun.isChecked = Prefs.autoRun(this)
        updateProviderHint(p)

        provider.setOnCheckedChangeListener { _, _ ->
            val now = currentProvider()
            // 地址/模型为空或还是某一家的默认值时，自动换成这一家的默认值
            val bases = Prefs.ALL.map { Prefs.defaultBase(it) }
            val models = Prefs.ALL.map { Prefs.defaultModel(it) }
            if (baseUrl.text.isBlank() || baseUrl.text.toString() in bases)
                baseUrl.setText(Prefs.defaultBase(now))
            if (model.text.isBlank() || model.text.toString() in models || model.text.startsWith("deepseek-"))
                model.setText(Prefs.defaultModel(now))
            updateProviderHint(now)
        }

        btnPerm.setOnClickListener {
            startActivity(Intent(
                Settings.ACTION_MANAGE_OVERLAY_PERMISSION,
                Uri.parse("package:$packageName")
            ))
        }

        findViewById<TextView>(R.id.btnResetPresets).setOnClickListener {
            presets.setText(Prefs.DEFAULT_PRESETS)
        }

        findViewById<TextView>(R.id.btnTest).setOnClickListener { testConnection() }

        findViewById<TextView>(R.id.btnStart).setOnClickListener {
            save()
            if (!Settings.canDrawOverlays(this)) {
                toast("还差一步：先开启悬浮窗权限")
                btnPerm.performClick()
                return@setOnClickListener
            }
            FloatService.start(this, FloatService.ACTION_BUBBLE)
            toast("悬浮球已开启。去别的 App 选中文字试试")
            // 需要先弹通知权限时留在本页，等用户选完再退到后台
            if (!askNotificationPermission()) moveTaskToBack(true)
        }

        findViewById<TextView>(R.id.btnStop).setOnClickListener {
            stopService(Intent(this, FloatService::class.java))
            toast("悬浮球已关闭")
        }
    }

    override fun onResume() {
        super.onResume()
        val ok = Settings.canDrawOverlays(this)
        permStatus.text = if (ok) "✓ 已允许。悬浮球和回答浮窗可以显示在所有 App 上面。"
                          else "还没允许。点下面的按钮，在列表里找到「划词助手」并打开开关。"
        permStatus.setTextColor(getColor(if (ok) R.color.accent else R.color.ink_soft))
        btnPerm.text = if (ok) "重新查看权限" else "去开启悬浮窗权限"

        val watching = isClipWatchOn()
        clipWatchStatus.text = if (watching)
            "✓ 已开启。在任意 App 点「复制」后，悬浮球旁会弹出「问 AI」。（需要悬浮球开着）"
        else "未开启。打开后，复制完不用再点悬浮球，提示会自己弹出来。"
        clipWatchStatus.setTextColor(getColor(if (watching) R.color.accent else R.color.ink_soft))
    }

    override fun onPause() {
        super.onPause()
        save()
    }

    override fun onDestroy() {
        testClient?.cancel()
        super.onDestroy()
    }

    private fun isClipWatchOn(): Boolean {
        val list = Settings.Secure.getString(contentResolver, Settings.Secure.ENABLED_ACCESSIBILITY_SERVICES)
            ?: return false
        return list.split(':').any { it.startsWith("$packageName/") }
    }

    private fun currentProvider() = when (provider.checkedRadioButtonId) {
        R.id.pAnthropic -> Prefs.ANTHROPIC
        R.id.pDeepseek -> Prefs.DEEPSEEK
        else -> Prefs.OPENAI
    }

    private fun updateProviderHint(p: String) {
        deepseekBox.visibility = if (p == Prefs.DEEPSEEK) android.view.View.VISIBLE else android.view.View.GONE
        if (p == Prefs.DEEPSEEK) {
            providerHint.text = "DeepSeek 官方接口。Key 在 platform.deepseek.com 的「API keys」里创建，充值后即可使用。"
            model.hint = "deepseek-flash 或 deepseek-v4-pro"
        } else if (p == Prefs.ANTHROPIC) {
            providerHint.text = "使用 Anthropic 官方 Claude API，Key 在 console.anthropic.com 获取。用中转服务时把接口地址换成中转地址即可。"
            model.hint = "例如 claude-sonnet-5-5 或 claude-haiku-5-5"
        } else {
            providerHint.text = "适用于 OpenAI 及兼容它的服务（通义千问、Kimi、智谱等）。接口地址填到 /v1 这一级。"
            model.hint = "填服务商给的模型名"
        }
        baseUrl.hint = Prefs.defaultBase(p)
    }

    private fun save() {
        Prefs.save(
            this,
            currentProvider(),
            baseUrl.text.toString().ifBlank { Prefs.defaultBase(currentProvider()) },
            apiKey.text.toString(),
            model.text.toString(),
            presets.text.toString(),
            autoRun.isChecked,
            deepThink.isChecked
        )
    }

    private fun testConnection() {
        save()
        testResult.setTextColor(getColor(R.color.ink_soft))
        testResult.text = "连接中…"
        val sb = StringBuilder()
        val client = testClient ?: AiClient(this).also { testClient = it }
        client.stream(
            listOf(AiClient.Msg("user", "只回复四个字：连接成功")),
            onDelta = { sb.append(it); testResult.text = sb.toString() },
            onDone = {
                testResult.setTextColor(getColor(R.color.accent))
                testResult.text = "✓ 可以用了：" + sb.toString().trim().take(30)
            },
            onError = {
                testResult.setTextColor(getColor(R.color.danger))
                testResult.text = it
            }
        )
    }

    /** 返回 true 表示弹出了权限请求。 */
    private fun askNotificationPermission(): Boolean {
        if (Build.VERSION.SDK_INT >= 33 &&
            checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED
        ) {
            requestPermissions(arrayOf(Manifest.permission.POST_NOTIFICATIONS), 1)
            return true
        }
        return false
    }

    override fun onRequestPermissionsResult(requestCode: Int, permissions: Array<out String>, grantResults: IntArray) {
        super.onRequestPermissionsResult(requestCode, permissions, grantResults)
        if (requestCode == 1) moveTaskToBack(true)
    }

    private fun toast(msg: String) = Toast.makeText(this, msg, Toast.LENGTH_SHORT).show()
}
