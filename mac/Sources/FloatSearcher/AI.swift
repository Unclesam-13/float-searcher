import Foundation

struct ChatMsg: Identifiable {
    let id = UUID()
    var role: String
    var content: String
}

/// 一条搜索结果；index 在整次回答里唯一，回答里用 [index] 引用
struct SearchHit: Identifiable, Hashable {
    var id: Int { index }
    var index: Int
    var title: String
    var url: String
    var site: String
    var date: String
    var summary: String
}

/// 给用户看的中文错误说明
struct AIError: LocalizedError {
    let message: String
    init(_ m: String) { message = m }
    var errorDescription: String? { message }
}

/// 一次请求用到的接口配置（从设置里取一份快照）
struct AIConfig {
    var provider: Provider
    var base: String
    var key: String
    var model: String
    var deepThink: Bool

    static func current() -> AIConfig {
        let s = AppSettings.shared
        var base = s.effectiveBase
        while base.hasSuffix("/") { base.removeLast() }
        return AIConfig(provider: s.provider, base: base,
                        key: s.apiKey.trimmingCharacters(in: .whitespacesAndNewlines),
                        model: s.effectiveModel, deepThink: s.deepThink)
    }
}

/// 联网过程的回调
struct WebHooks {
    var onSearch: (String) -> Void      // 开始搜索某个关键词
    var onReset: () -> Void             // 之前流出的文字作废（模型决定先去搜）
    var onSources: ([SearchHit]) -> Void // 回答引用的来源
}

/// 流式调用 AI：Claude、DeepSeek、或任何 OpenAI 兼容接口。
/// DeepSeek 可联网：模型自己决定搜不搜、搜什么，程序调用 DeepSeek 搜索接口把结果交给它。
/// 全部在主线程上 await（网络等待不会卡界面），回调直接更新界面。
@MainActor
enum AI {
    static let session: URLSession = {
        let c = URLSessionConfiguration.default
        c.timeoutIntervalForRequest = 60
        c.timeoutIntervalForResource = 600
        return URLSession(configuration: c)
    }()

    static func stream(
        _ cfg: AIConfig, system: String, history: [ChatMsg],
        noThinking: Bool = false, web: WebHooks? = nil,
        onThinking: ((Int) -> Void)? = nil,
        onDelta: @escaping (String) -> Void
    ) async throws -> String {
        if cfg.key.isEmpty { throw AIError("还没填 API Key：点菜单栏里的图标 → 设置。") }
        if cfg.model.isEmpty { throw AIError("还没填模型名：点菜单栏里的图标 → 设置。") }

        switch cfg.provider {
        case .anthropic:
            let r = try await runRound(anthropicRequest(cfg, system: system, history: history),
                                       anthropic: true, collectTools: false, onDelta: onDelta, onThinking: onThinking)
            return r.content
        case .deepseek where web != nil:
            return try await webLoop(cfg, system: system, history: history, web: web!, onDelta: onDelta)
        default:
            var msgs: [[String: Any]] = [["role": "system", "content": system]]
            for m in history { msgs.append(["role": m.role, "content": m.content]) }
            var body: [String: Any] = ["model": cfg.model, "stream": true, "messages": msgs]
            if cfg.provider == .deepseek {
                // DeepSeek 默认开启思考；这里默认关掉求快，设置里可打开
                body["thinking"] = ["type": (cfg.deepThink && !noThinking) ? "enabled" : "disabled"]
            }
            let r = try await runRound(try openAIRequest(cfg, body), anthropic: false, collectTools: false,
                                       onDelta: onDelta, onThinking: onThinking)
            return r.content
        }
    }

    // ================= 一轮请求 =================

    final class ToolCall {
        var id = ""
        var name = ""
        var args = ""
    }

    struct Round {
        var content: String
        var reasoning: String
        var calls: [ToolCall]
    }

    static func runRound(_ req: URLRequest, anthropic: Bool, collectTools: Bool,
                         onDelta: (String) -> Void, onThinking: ((Int) -> Void)?) async throws -> Round {
        let bytes: URLSession.AsyncBytes
        let resp: URLResponse
        do {
            (bytes, resp) = try await session.bytes(for: req)
        } catch let e as URLError {
            throw mapURLError(e)
        }
        guard let http = resp as? HTTPURLResponse else { throw AIError("服务器没有正常回应，请再试一次。") }

        if !(200..<300).contains(http.statusCode) {
            var data = Data()
            do {
                for try await b in bytes {
                    data.append(b)
                    if data.count > 20000 { break }
                }
            } catch {}
            try Task.checkCancellation()
            throw AIError(describeHTTP(http.statusCode, String(data: data, encoding: .utf8) ?? ""))
        }

        var content = ""
        var reasoning = ""
        var tools: [Int: ToolCall] = [:]
        do {
            for try await line in bytes.lines {
                try Task.checkCancellation()
                guard line.hasPrefix("data:") else { continue }
                let data = line.dropFirst(5).trimmingCharacters(in: .whitespaces)
                if data.isEmpty { continue }
                if data == "[DONE]" { break }
                guard let obj = (try? JSONSerialization.jsonObject(with: Data(data.utf8))) as? [String: Any] else { continue }

                if anthropic {
                    let type = obj["type"] as? String
                    if type == "content_block_delta",
                       let delta = obj["delta"] as? [String: Any],
                       delta["type"] as? String == "text_delta",
                       let text = delta["text"] as? String, !text.isEmpty {
                        content += text
                        onDelta(text)
                    } else if type == "error" {
                        let msg = (obj["error"] as? [String: Any])?["message"] as? String
                        throw AIError(msg ?? "服务返回错误")
                    }
                    continue
                }

                if let err = obj["error"] as? [String: Any] {
                    throw AIError(err["message"] as? String ?? "服务返回错误")
                }
                guard let choices = obj["choices"] as? [[String: Any]], let c0 = choices.first,
                      let delta = c0["delta"] as? [String: Any] else { continue }
                if let r = delta["reasoning_content"] as? String, !r.isEmpty {
                    reasoning += r
                    onThinking?(reasoning.count)
                }
                // 工具调用（联网搜索）分成很多碎片流过来，按 index 拼起来
                if collectTools, let tcs = delta["tool_calls"] as? [[String: Any]] {
                    for tc in tcs {
                        let idx = (tc["index"] as? Int) ?? tools.count
                        let call = tools[idx] ?? ToolCall()
                        tools[idx] = call
                        if let id = tc["id"] as? String, !id.isEmpty { call.id = id }
                        if let fn = tc["function"] as? [String: Any] {
                            if let n = fn["name"] as? String { call.name += n }
                            if let a = fn["arguments"] as? String { call.args += a }
                        }
                    }
                }
                if let text = delta["content"] as? String, !text.isEmpty {
                    content += text
                    onDelta(text)
                }
            }
        } catch let e as URLError {
            throw mapURLError(e)
        }
        try Task.checkCancellation()
        let calls = tools.keys.sorted().compactMap { tools[$0] }
        return Round(content: content, reasoning: reasoning, calls: calls)
    }

    // ================= DeepSeek 联网 =================

    static let searchTool: [String: Any] = [
        "type": "function",
        "function": [
            "name": "web_search",
            "description": "联网搜索网页，返回编号、标题、网址、发布日期和摘要。需要最新信息、新闻、价格、版本、具体数据或核实事实时使用。",
            "parameters": [
                "type": "object",
                "properties": [
                    "query": [
                        "type": "string",
                        "description": "搜索关键词，简短明确，不超过 60 个字。可以用 site:、after:2025、lang:zh,en 等语法。",
                    ] as [String: Any],
                ] as [String: Any],
                "required": ["query"],
            ] as [String: Any],
        ] as [String: Any],
    ]

    static func webNote() -> String {
        let f = DateFormatter()
        f.locale = Locale(identifier: "zh_CN")
        f.dateFormat = "yyyy年M月d日"
        return "\n\n你可以使用 web_search 工具联网搜索。涉及最新动态、新闻、价格、版本、天气、具体数据，或你不确定的事实时，先搜索再回答；"
            + "常识和不需要时效的问题不必搜索。最多搜索 3 次。"
            + "回答时，在用到搜索结果的句子末尾标注来源编号，例如 [1] 或 [2][3]，编号和搜索结果里的一致；不要在末尾另列来源清单。"
            + "今天是 \(f.string(from: Date()))。"
    }

    /// 最多 3 轮搜索；第 4 轮不给工具，必须作答
    static func webLoop(_ cfg: AIConfig, system: String, history: [ChatMsg], web: WebHooks,
                        onDelta: @escaping (String) -> Void) async throws -> String {
        var msgs: [[String: Any]] = [["role": "system", "content": system + webNote()]]
        for m in history { msgs.append(["role": m.role, "content": m.content]) }
        let question = history.last(where: { $0.role == "user" })?.content ?? ""
        var hits: [SearchHit] = []
        var round = 0
        while true {
            try Task.checkCancellation()
            let allowTools = round < 3
            var body: [String: Any] = [
                "model": cfg.model,
                "stream": true,
                "messages": msgs,
                // 联网时关闭深度思考：带工具的思考模式要求每次都回传思考内容，容易出错
                "thinking": ["type": "disabled"],
            ]
            if allowTools { body["tools"] = [searchTool] }
            let r = try await runRound(try openAIRequest(cfg, body), anthropic: false, collectTools: allowTools,
                                       onDelta: onDelta, onThinking: nil)
            let calls = r.calls.filter { $0.name == "web_search" }
            if calls.isEmpty {
                web.onSources(cited(r.content, hits))
                return r.content
            }

            web.onReset() // 前面流出来的「我来搜一下」不显示
            for (i, c) in calls.enumerated() where c.id.isEmpty { c.id = "call_\(round)_\(i)" }
            let toolCalls: [[String: Any]] = calls.map { c -> [String: Any] in
                ["id": c.id, "type": "function", "function": ["name": c.name, "arguments": c.args] as [String: Any]]
            }
            var asst: [String: Any] = ["role": "assistant", "content": r.content, "tool_calls": toolCalls]
            if !r.reasoning.isEmpty { asst["reasoning_content"] = r.reasoning }
            msgs.append(asst)

            for c in calls {
                let q = queryFrom(c.args)
                var text: String
                if q.isEmpty {
                    text = "搜索关键词为空。"
                } else {
                    web.onSearch(q)
                    do {
                        var found = try await search(cfg, query: q, question: question)
                        for i in found.indices { found[i].index = hits.count + 1 + i }
                        hits += found
                        text = formatHits(q, found)
                    } catch is CancellationError {
                        throw CancellationError()
                    } catch {
                        text = "搜索失败：\(error.localizedDescription)。请根据已有知识回答，并说明没能联网核实。"
                    }
                }
                msgs.append(["role": "tool", "tool_call_id": c.id, "content": text])
            }
            round += 1
        }
    }

    static func queryFrom(_ args: String) -> String {
        let raw = args.trimmingCharacters(in: .whitespacesAndNewlines)
        guard let obj = (try? JSONSerialization.jsonObject(with: Data((raw.isEmpty ? "{}" : raw).utf8))) as? [String: Any],
              let q = obj["query"] as? String else { return "" }
        return q.trimmingCharacters(in: .whitespacesAndNewlines)
    }

    /// DeepSeek 搜索接口：POST /v1/tools/search
    static func search(_ cfg: AIConfig, query: String, question: String) async throws -> [SearchHit] {
        let root = cfg.base.hasSuffix("/v1") ? String(cfg.base.dropLast(3)) : cfg.base
        guard let url = URL(string: root + "/v1/tools/search") else { throw AIError("接口地址不对") }
        var body: [String: Any] = ["query": String(query.prefix(128)), "count": 6]
        let oq = String(question.replacingOccurrences(of: "\n", with: " ").trimmingCharacters(in: .whitespaces).prefix(128))
        if !oq.isEmpty { body["ori_question"] = oq }

        var req = URLRequest(url: url)
        req.httpMethod = "POST"
        req.timeoutInterval = 25
        req.setValue("application/json", forHTTPHeaderField: "Content-Type")
        req.setValue("Bearer \(cfg.key)", forHTTPHeaderField: "Authorization")
        req.httpBody = try JSONSerialization.data(withJSONObject: body)

        let data: Data
        let resp: URLResponse
        do {
            (data, resp) = try await session.data(for: req)
        } catch let e as URLError {
            throw mapURLError(e)
        }
        let text = String(data: data, encoding: .utf8) ?? ""
        if let http = resp as? HTTPURLResponse, !(200..<300).contains(http.statusCode) {
            throw AIError(describeHTTP(http.statusCode, text))
        }
        guard let obj = (try? JSONSerialization.jsonObject(with: data)) as? [String: Any],
              let results = obj["results"] as? [[String: Any]] else { return [] }
        var out: [SearchHit] = []
        for it in results {
            func str(_ k: String) -> String { it[k] as? String ?? "" }
            let u = str("url")
            if u.isEmpty { continue }
            var summary = str("summary")
            if summary.isEmpty { summary = str("snippet") }
            out.append(SearchHit(index: 0, title: str("title"), url: u, site: str("site_name"),
                                 date: str("date"), summary: String(summary.prefix(800))))
        }
        return out
    }

    static func formatHits(_ query: String, _ hits: [SearchHit]) -> String {
        if hits.isEmpty { return "搜索「\(query)」没有找到结果。" }
        var s = "搜索「\(query)」的结果：\n"
        for h in hits {
            s += "\n[\(h.index)] \(h.title)"
            if !h.site.isEmpty { s += " — \(h.site)" }
            if h.date.count >= 10 { s += " · \(h.date.prefix(10))" }
            s += "\n\(h.url)\n\(h.summary)\n"
        }
        return s
    }

    /// 回答里引用了哪些 [编号]；一个都没引用时给前 5 条
    static func cited(_ answer: String, _ hits: [SearchHit]) -> [SearchHit] {
        var used = Set<Int>()
        if let re = try? NSRegularExpression(pattern: "\\[(\\d{1,2})\\]") {
            let ns = answer as NSString
            for m in re.matches(in: answer, range: NSRange(location: 0, length: ns.length)) {
                if let n = Int(ns.substring(with: m.range(at: 1))), n >= 1, n <= hits.count { used.insert(n) }
            }
        }
        if used.isEmpty { return Array(hits.prefix(5)) }
        return used.sorted().map { hits[$0 - 1] }
    }

    // ================= 请求构造 =================

    static func anthropicRequest(_ cfg: AIConfig, system: String, history: [ChatMsg]) throws -> URLRequest {
        let urlStr = cfg.base.hasSuffix("/v1") ? cfg.base + "/messages" : cfg.base + "/v1/messages"
        guard let url = URL(string: urlStr) else { throw AIError("接口地址不对：\(cfg.base)") }
        let msgs: [[String: Any]] = history.map { ["role": $0.role, "content": $0.content] }
        let body: [String: Any] = ["model": cfg.model, "max_tokens": 2048, "stream": true, "system": system, "messages": msgs]
        var req = URLRequest(url: url)
        req.httpMethod = "POST"
        req.setValue("application/json", forHTTPHeaderField: "Content-Type")
        req.setValue("text/event-stream", forHTTPHeaderField: "Accept")
        req.setValue(cfg.key, forHTTPHeaderField: "x-api-key")
        req.setValue("2023-06-01", forHTTPHeaderField: "anthropic-version")
        req.httpBody = try JSONSerialization.data(withJSONObject: body)
        return req
    }

    static func openAIRequest(_ cfg: AIConfig, _ body: [String: Any]) throws -> URLRequest {
        let urlStr = cfg.base.hasSuffix("/chat/completions") ? cfg.base : cfg.base + "/chat/completions"
        guard let url = URL(string: urlStr) else { throw AIError("接口地址不对：\(cfg.base)") }
        var req = URLRequest(url: url)
        req.httpMethod = "POST"
        req.setValue("application/json", forHTTPHeaderField: "Content-Type")
        req.setValue("text/event-stream", forHTTPHeaderField: "Accept")
        req.setValue("Bearer \(cfg.key)", forHTTPHeaderField: "Authorization")
        req.httpBody = try JSONSerialization.data(withJSONObject: body)
        return req
    }

    // ================= 错误说明 =================

    static func mapURLError(_ e: URLError) -> Error {
        switch e.code {
        case .cancelled:
            return CancellationError()
        case .timedOut:
            return AIError("等太久没有回应，请检查网络后再试。")
        case .notConnectedToInternet, .cannotFindHost, .cannotConnectToHost, .dnsLookupFailed, .networkConnectionLost:
            return AIError("连不上服务器：请检查网络，或接口地址是否写对。")
        default:
            return AIError("网络出错：\(e.localizedDescription)")
        }
    }

    static func describeHTTP(_ code: Int, _ body: String) -> String {
        var detail = ""
        if let obj = (try? JSONSerialization.jsonObject(with: Data(body.utf8))) as? [String: Any] {
            if let e = obj["error"] as? [String: Any], let m = e["message"] as? String { detail = m }
            else if let e = obj["error"] as? String { detail = e }
            else if let m = obj["message"] as? String { detail = m }
        }
        if detail.isEmpty { detail = String(body.prefix(160)) }
        let hint: String
        switch code {
        case 400: hint = "请求有误，常见原因是模型名写错"
        case 401: hint = "API Key 不对或已失效"
        case 402: hint = "账户余额不足"
        case 403: hint = "这个 Key 没有权限，或所在地区不支持"
        case 404: hint = "接口地址或模型名不对"
        case 429: hint = "请求太频繁，或额度用完了"
        case 500, 502, 503, 504, 529: hint = "服务暂时繁忙，稍后再试"
        default: hint = "请求失败"
        }
        let d = detail.trimmingCharacters(in: .whitespacesAndNewlines)
        return d.isEmpty ? "\(hint)（\(code)）" : "\(hint)（\(code)）：\(d)"
    }
}
