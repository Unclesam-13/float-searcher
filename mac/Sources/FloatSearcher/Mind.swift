import AppKit
import Foundation

// ============================================================
// 追问树的数据：和 Obsidian 插件 Canvas Mindflow、Windows 版的「对话数据/*.json」格式一致。
// 把保存位置设成库里的 Mindflow/对话数据，就能和插件、Windows 版共用历史。
// 不认识的字段（插件的附件文字、编辑记录等）原样保留。
// ============================================================

private func str(_ d: [String: Any], _ k: String) -> String? { d[k] as? String }

private func i64(_ d: [String: Any], _ k: String) -> Int64? {
    if let n = d[k] as? NSNumber { return n.int64Value }
    if let s = d[k] as? String { return Int64(s) }
    return nil
}

private func rest(_ d: [String: Any], _ known: Set<String>) -> [String: Any] {
    d.filter { !known.contains($0.key) }
}

final class MindAttachment {
    var id = Mind.uid()
    /// image / pdf / text
    var kind = "image"
    var name = ""
    /// 相对路径，用 / 分隔（插件里是相对库根目录）
    var path = ""
    var mime = "image/jpeg"
    var size: Int64?
    /// 插件提取出来的文件文字
    var text: String?
    var extra: [String: Any] = [:]

    init() {}

    init(_ d: [String: Any]) {
        id = str(d, "id") ?? Mind.uid()
        kind = str(d, "kind") ?? "file"
        name = str(d, "name") ?? ""
        path = str(d, "path") ?? ""
        mime = str(d, "mime") ?? ""
        size = i64(d, "size")
        text = str(d, "text")
        extra = rest(d, ["id", "kind", "name", "path", "mime", "size", "text"])
    }

    var json: [String: Any] {
        var d = extra
        d["id"] = id; d["kind"] = kind; d["name"] = name; d["path"] = path; d["mime"] = mime
        if let size { d["size"] = size }
        if let text { d["text"] = text }
        return d
    }
}

struct MindSource {
    var index: Int
    var title: String
    var url: String
    var site: String
    var date: String

    init(_ h: SearchHit) { index = h.index; title = h.title; url = h.url; site = h.site; date = h.date }

    init(_ d: [String: Any]) {
        index = (d["index"] as? NSNumber)?.intValue ?? 0
        title = str(d, "title") ?? ""; url = str(d, "url") ?? ""
        site = str(d, "site") ?? ""; date = str(d, "date") ?? ""
    }

    var json: [String: Any] { ["index": index, "title": title, "url": url, "site": site, "date": date] }
}

final class MindMsg: Identifiable {
    var id = Mind.uid()
    var role = "user"
    var content = ""
    var createdAt = Mind.now()
    var model: String?
    var doneAt: Int64?
    /// 联网搜索引用的来源
    var sources: [MindSource]?
    /// 提问附带的图片 / 文件（和插件一样只记路径，原件在「附件」文件夹）
    var attachments: [MindAttachment]?
    var extra: [String: Any] = [:]
    /// 正在生成（不保存）
    var pending = false

    init(role: String, content: String) { self.role = role; self.content = content }

    init(_ d: [String: Any]) {
        id = str(d, "id") ?? Mind.uid()
        role = str(d, "role") ?? "user"
        content = str(d, "content") ?? ""
        createdAt = i64(d, "createdAt") ?? 0
        model = str(d, "model")
        doneAt = i64(d, "doneAt")
        if let a = d["sources"] as? [[String: Any]] { sources = a.map { MindSource($0) } }
        if let a = d["attachments"] as? [[String: Any]] { attachments = a.map { MindAttachment($0) } }
        extra = rest(d, ["id", "role", "content", "createdAt", "model", "doneAt", "sources", "attachments"])
    }

    var json: [String: Any] {
        var d = extra
        d["id"] = id; d["role"] = role; d["content"] = content; d["createdAt"] = createdAt
        if let model { d["model"] = model }
        if let doneAt { d["doneAt"] = doneAt }
        if let sources { d["sources"] = sources.map(\.json) }
        if let attachments { d["attachments"] = attachments.map(\.json) }
        return d
    }

    var images: [MindAttachment] { (attachments ?? []).filter { $0.kind == "image" } }
}

final class MindAnchor {
    var text: String
    var msgId: String
    var extra: [String: Any] = [:]

    init(text: String, msgId: String) { self.text = text; self.msgId = msgId }

    init?(_ any: Any?) {
        guard let d = any as? [String: Any] else { return nil }
        text = str(d, "text") ?? ""
        msgId = str(d, "msgId") ?? ""
        extra = rest(d, ["text", "msgId"])
    }

    var json: [String: Any] {
        var d = extra
        d["text"] = text; d["msgId"] = msgId
        return d
    }
}

/// 树上的一个「窗口」：主线，或从某个回答里划线追问出来的分支
final class MindNode: Identifiable {
    var id = Mind.uid()
    var parentId: String?
    /// 从父窗口哪条回答的哪段话追问出来的；主线为空
    var anchor: MindAnchor?
    var title: String?
    var createdAt = Mind.now()
    var messages: [MindMsg] = []
    var extra: [String: Any] = [:]

    init() {}

    init(_ d: [String: Any], key: String) {
        id = str(d, "id") ?? key
        parentId = str(d, "parentId")
        anchor = MindAnchor(d["anchor"])
        title = str(d, "title")
        createdAt = i64(d, "createdAt") ?? 0
        messages = (d["messages"] as? [[String: Any]] ?? []).map { MindMsg($0) }
        extra = rest(d, ["id", "parentId", "anchor", "title", "createdAt", "messages"])
    }

    var json: [String: Any] {
        var d = extra
        d["id"] = id
        // 根节点也照插件的样子写出 null
        if let parentId { d["parentId"] = parentId } else { d["parentId"] = NSNull() }
        if let anchor { d["anchor"] = anchor.json } else { d["anchor"] = NSNull() }
        if let title { d["title"] = title } else { d["title"] = NSNull() }
        d["createdAt"] = createdAt
        d["messages"] = messages.map(\.json)
        return d
    }
}

/// 一个对话：一棵问答树
final class MindSession: Identifiable {
    var id = Mind.uid()
    var version = 1
    var title = ""
    var createdAt = Mind.now()
    var updatedAt = Mind.now()
    var rootId = ""
    var nodes: [String: MindNode] = [:]
    var extra: [String: Any] = [:]

    init() {}

    init?(_ d: [String: Any], fileId: String) {
        guard let nd = d["nodes"] as? [String: Any], !nd.isEmpty else { return nil }
        id = str(d, "id") ?? fileId
        version = (d["version"] as? NSNumber)?.intValue ?? 1
        title = str(d, "title") ?? ""
        createdAt = i64(d, "createdAt") ?? 0
        updatedAt = i64(d, "updatedAt") ?? createdAt
        for (k, v) in nd {
            if let vd = v as? [String: Any] { nodes[k] = MindNode(vd, key: k) }
        }
        if nodes.isEmpty { return nil }
        rootId = str(d, "rootId") ?? ""
        if nodes[rootId] == nil {
            rootId = nodes.values.first(where: { $0.parentId == nil })?.id ?? nodes.keys.sorted()[0]
        }
        extra = rest(d, ["id", "version", "title", "createdAt", "updatedAt", "rootId", "nodes"])
    }

    var json: [String: Any] {
        var d = extra
        d["id"] = id; d["version"] = version; d["title"] = title
        d["createdAt"] = createdAt; d["updatedAt"] = updatedAt; d["rootId"] = rootId
        var nd: [String: Any] = [:]
        for (k, n) in nodes { nd[k] = n.json }
        d["nodes"] = nd
        return d
    }

    var root: MindNode? { nodes[rootId] }
}

/// 问答树上的一行（已经算好编号和缩进）
struct MindTreeItem: Identifiable {
    var id: String { node.id + "/" + (user?.id ?? label) }
    var label: String
    var depth: Int
    var node: MindNode
    var user: MindMsg?
    var firstOfNode: Bool
}

enum Mind {
    static func now() -> Int64 { Int64(Date().timeIntervalSince1970 * 1000) }

    /// 和插件一样的 id：时间戳 36 进制 + 6 位随机
    static func uid() -> String {
        let digits = Array("0123456789abcdefghijklmnopqrstuvwxyz")
        let r = String((0..<6).map { _ in digits[Int.random(in: 0..<36)] })
        return String(now(), radix: 36) + r
    }

    static func truncate(_ s: String?, _ n: Int) -> String {
        var t = (s ?? "").replacingOccurrences(of: "\r", with: " ").replacingOccurrences(of: "\n", with: " ")
        while t.contains("  ") { t = t.replacingOccurrences(of: "  ", with: " ") }
        t = t.trimmingCharacters(in: .whitespaces)
        return t.count > n ? String(t.prefix(n)) + "…" : t
    }

    private static let hmFormatter: DateFormatter = {
        let f = DateFormatter()
        f.locale = Locale(identifier: "zh_CN")
        f.dateFormat = "HH:mm"
        return f
    }()

    static func fmtTime(_ ts: Int64) -> String {
        let d = Date(timeIntervalSince1970: TimeInterval(ts) / 1000)
        let cal = Calendar.current
        let hm = hmFormatter.string(from: d)
        if cal.isDateInToday(d) { return "今天 " + hm }
        if cal.isDateInYesterday(d) { return "昨天 " + hm }
        let c = cal.dateComponents([.year, .month, .day], from: d)
        if c.year == cal.component(.year, from: Date()) { return "\(c.month ?? 0)月\(c.day ?? 0)日 \(hm)" }
        return "\(c.year ?? 0)年\(c.month ?? 0)月\(c.day ?? 0)日"
    }

    static func newSession(_ question: String) -> MindSession {
        let root = MindNode()
        root.title = truncate(question, 40)
        let s = MindSession()
        s.title = root.title ?? ""
        s.rootId = root.id
        s.nodes = [root.id: root]
        return s
    }

    // ---------------- 树 ----------------

    /// 从主线到这个窗口的一串窗口（主线在前）
    static func chain(_ s: MindSession?, _ nodeId: String?) -> [MindNode] {
        guard let s, let nodeId else { return [] }
        var list: [MindNode] = []
        var seen = Set<String>()
        var n = s.nodes[nodeId]
        while let x = n, !seen.contains(x.id) {
            seen.insert(x.id)
            list.insert(x, at: 0)
            n = x.parentId.flatMap { s.nodes[$0] }
        }
        return list
    }

    static func children(_ s: MindSession, _ id: String) -> [MindNode] {
        s.nodes.values.filter { $0.parentId == id }.sorted { $0.createdAt < $1.createdAt }
    }

    static func subtree(_ s: MindSession, _ id: String) -> [String] {
        var list: [String] = []
        var seen = Set<String>()
        func walk(_ x: String) {
            guard !seen.contains(x) else { return }
            seen.insert(x)
            list.append(x)
            for c in children(s, x) { walk(c.id) }
        }
        walk(id)
        return list
    }

    /// 一个窗口里的「一问一答」
    static func rounds(_ node: MindNode) -> [(user: MindMsg?, ids: [String])] {
        var list: [(user: MindMsg?, ids: [String])] = []
        var open = false
        for m in node.messages {
            if m.role == "user" {
                list.append((m, [m.id]))
                open = true
            } else if open {
                list[list.count - 1].ids.append(m.id)
                open = false
            } else {
                list.append((nil, [m.id]))
            }
        }
        if list.isEmpty { list.append((nil, [])) }
        return list
    }

    /// 问答树：同一个窗口里接着问的是同级（1、2、3…）；划线追问出来的窗口挂在那一问下面（1.1、1.2…）
    static func tree(_ s: MindSession?) -> [MindTreeItem] {
        guard let s, let root = s.root else { return [] }
        var list: [MindTreeItem] = []
        var seen = Set<String>()

        @discardableResult
        func walk(_ node: MindNode, _ prefix: String, _ start: Int, _ depth: Int) -> Int {
            guard !seen.contains(node.id) else { return 0 }
            seen.insert(node.id)
            let rs = rounds(node)
            var kidsBy: [Int: [MindNode]] = [:]
            for c in children(s, node.id) {
                var idx = -1
                if let a = c.anchor { idx = rs.firstIndex(where: { $0.ids.contains(a.msgId) }) ?? -1 }
                if idx < 0 { idx = rs.count - 1 }
                kidsBy[idx, default: []].append(c)
            }
            for (i, r) in rs.enumerated() {
                let label = prefix + String(start + i + 1)
                list.append(MindTreeItem(label: label, depth: depth, node: node, user: r.user, firstOfNode: i == 0))
                var k = 0
                for c in kidsBy[i] ?? [] { k += walk(c, label + ".", k, depth + 1) }
            }
            return rs.count
        }
        walk(root, "", 0, 0)
        return list
    }

    static func questionCount(_ s: MindSession) -> Int {
        s.nodes.values.reduce(0) { $0 + $1.messages.filter { $0.role == "user" }.count }
    }

    // ---------------- 发给 AI 的上下文 ----------------

    static let fullDepth = 3        // 离当前窗口 3 层以内的回答发全文
    static let truncateChars = 600  // 更远的只发开头

    /// 祖先窗口的对话（到分出当前分支的那条回答为止，远的截断）+ 当前窗口的对话。
    /// 分支的第一问前面带上划线原文。图片只发当前窗口最近两次带图的提问。
    static func buildThread(_ s: MindSession, _ nodeId: String, loadImage: (MindAttachment) -> String?,
                            upto: String? = nil) -> [ChatMsg] {
        let ch = chain(s, nodeId)
        var msgs: [ChatMsg] = []
        var recentImg = Set<String>()
        if let cur = ch.last {
            // 只看 upto 之前的消息（按顺序，不按时间）
            var before = cur.messages
            if let upto, let i = cur.messages.firstIndex(where: { $0.id == upto }) { before = Array(cur.messages[..<i]) }
            for m in before.reversed() where m.role == "user" && !m.images.isEmpty {
                recentImg.insert(m.id)
                if recentImg.count >= 2 { break }
            }
        }
        for (idx, node) in ch.enumerated() {
            let dist = ch.count - 1 - idx
            let isCur = idx == ch.count - 1
            var stopAfter = isCur ? nil : ch[idx + 1].anchor?.msgId
            if let sa = stopAfter, !node.messages.contains(where: { $0.id == sa }) { stopAfter = nil }
            var firstUser = true
            for m in node.messages {
                if isCur, let upto, m.id == upto { break }
                let failed = m.role == "assistant" && m.content.hasPrefix("❌")
                if !m.pending && !failed && !m.content.isEmpty {
                    var c = m.content.replacingOccurrences(of: "\n\n*（已停止生成）*", with: "")
                        .replacingOccurrences(of: "*（已停止生成）*", with: "")
                    var images: [String] = []
                    if m.role == "user" {
                        if firstUser, let a = node.anchor, !a.text.isEmpty {
                            c = "关于你上面回答中的这段内容：\n「" + a.text + "」\n\n" + c
                        }
                        firstUser = false
                        for a in m.attachments ?? [] where a.kind != "image" {
                            if let t = a.text, !t.isEmpty { c += "\n\n【附件：\(a.name)】\n\(t)" }
                        }
                        let imgs = m.images
                        if !imgs.isEmpty && isCur && recentImg.contains(m.id) {
                            for a in imgs { if let b = loadImage(a) { images.append(b) } }
                            if images.count < imgs.count { c += "\n（有 \(imgs.count - images.count) 张图片找不到了）" }
                        } else if !imgs.isEmpty {
                            c += "\n（这条提问附带了 \(imgs.count) 张图片）"
                        }
                    } else if dist > fullDepth && c.count > truncateChars {
                        c = String(c.prefix(truncateChars)) + "\n…（较早的回答，已截断）"
                    }
                    if !c.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
                        let role = m.role == "assistant" ? "assistant" : "user"
                        if let last = msgs.last, last.role == role {
                            msgs[msgs.count - 1].content += "\n\n" + c
                            msgs[msgs.count - 1].images += images
                        } else {
                            msgs.append(ChatMsg(role: role, content: c, images: images))
                        }
                    }
                }
                if let sa = stopAfter, m.id == sa { break }
            }
        }
        if let f = msgs.first, f.role == "assistant" { msgs.insert(ChatMsg(role: "user", content: "（继续之前的讨论）"), at: 0) }
        // 角色是一问一答交替的：每次去掉第 2、3 条（一答一问），开头和顺序都不乱
        while msgs.count > 40 { msgs.remove(at: 1); msgs.remove(at: 1) }
        return msgs
    }
}

/// 对话保存在一个文件夹里，一个对话一个 json
@MainActor
final class MindStore {
    static let shared = MindStore()

    /// 保存出错时通知界面
    var onError: ((String) -> Void)?
    /// 界面正在用的对话：磁盘上的文件变了也不换成新读的对象
    var inUse: ((String) -> Bool)?

    static var defaultFolder: URL {
        let base = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
        return base.appendingPathComponent("FloatSearcher/对话数据", isDirectory: true)
    }

    /// 设置里的保存位置（快照：打字过程中不跟着变，停下来一会儿才更新）
    private var configured: String?

    func useFolderSetting(_ v: String) { configured = v }

    var folder: URL {
        let setting = configured ?? AppSettings.shared.mindFolder
        let raw = (setting as NSString).expandingTildeInPath
            .trimmingCharacters(in: .whitespacesAndNewlines)
        if raw.isEmpty { return MindStore.defaultFolder }
        if raw.hasPrefix("/") { return URL(fileURLWithPath: raw, isDirectory: true).standardizedFileURL }
        return MindStore.defaultFolder.deletingLastPathComponent().appendingPathComponent(raw, isDirectory: true)
    }

    private var cache: [String: (s: MindSession, mtime: Date)] = [:]
    private var paths: [String: URL] = [:]
    private var unsaved = Set<String>()
    private var deleted = Set<String>()
    private var toWrite: [String: MindSession] = [:]
    private var timers: [String: Task<Void, Never>] = [:]
    private var cacheFolder: URL?

    private func pathOf(_ id: String) -> URL { paths[id] ?? folder.appendingPathComponent(id + ".json") }

    private func checkFolder() {
        let f = folder
        if cacheFolder == f { return }
        flush() // 先把没写完的写回各自原来的文件
        cache.removeAll()
        unsaved.removeAll()
        cacheFolder = f
    }

    private static func mtime(_ u: URL) -> Date? {
        (try? FileManager.default.attributesOfItem(atPath: u.path))?[.modificationDate] as? Date
    }

    /// 所有对话，最近的在前。文件没变就用缓存。
    func list() -> [MindSession] {
        checkFolder()
        var seen = Set<String>()
        var byPath: [String: String] = [:]
        for (id, u) in paths { byPath[u.standardizedFileURL.path] = id }
        let files = (try? FileManager.default.contentsOfDirectory(at: folder, includingPropertiesForKeys: [.contentModificationDateKey])) ?? []
        for f in files where f.pathExtension.lowercased() == "json" {
            guard let mt = MindStore.mtime(f) else { continue }
            let fp = f.standardizedFileURL.path
            let known = byPath[fp] ?? f.deletingPathExtension().lastPathComponent
            if let c = cache[known], byPath[fp] != nil || paths[known] == nil {
                seen.insert(known)
                if c.mtime == mt || timers[known] != nil || (inUse?(known) ?? false) { continue }
            }
            guard let s = MindStore.read(f), !deleted.contains(s.id) else { continue }
            // 同步软件留下的冲突副本（同一个 id 的另一个文件）：只认原来那个
            if let mapped = paths[s.id], mapped.standardizedFileURL.path != fp,
               FileManager.default.fileExists(atPath: mapped.path) { continue }
            seen.insert(s.id)
            if cache[s.id] != nil && (timers[s.id] != nil || (inUse?(s.id) ?? false)) { continue }
            cache[s.id] = (s, mt)
            paths[s.id] = f
        }
        for id in Array(cache.keys) where !seen.contains(id) && timers[id] == nil && !unsaved.contains(id) {
            cache.removeValue(forKey: id)
        }
        return cache.values.map(\.s).sorted { $0.updatedAt > $1.updatedAt }
    }

    private static func read(_ f: URL) -> MindSession? {
        guard let data = try? Data(contentsOf: f),
              let d = (try? JSONSerialization.jsonObject(with: data)) as? [String: Any] else { return nil }
        return MindSession(d, fileId: f.deletingPathExtension().lastPathComponent)
    }

    func add(_ s: MindSession) {
        checkFolder()
        cache[s.id] = (s, .distantPast)
        paths[s.id] = folder.appendingPathComponent(s.id + ".json")
        unsaved.insert(s.id)
    }

    /// 保存：默认合并 0.4 秒内的多次修改；immediate 立刻写
    func save(_ s: MindSession, immediate: Bool = false) {
        if deleted.contains(s.id) { return }
        s.updatedAt = Mind.now()
        timers[s.id]?.cancel()
        timers[s.id] = nil
        if immediate {
            toWrite.removeValue(forKey: s.id)
            write(s)
            return
        }
        toWrite[s.id] = s
        let id = s.id
        timers[id] = Task { @MainActor [weak self] in
            try? await Task.sleep(nanoseconds: 400_000_000)
            guard let self, !Task.isCancelled else { return }
            self.timers[id] = nil
            if let obj = self.toWrite.removeValue(forKey: id) { self.write(obj) }
        }
    }

    func flush() {
        for t in timers.values { t.cancel() }
        timers.removeAll()
        for s in toWrite.values { write(s) }
        toWrite.removeAll()
    }

    private func write(_ s: MindSession) {
        if deleted.contains(s.id) { return }
        let path = pathOf(s.id)
        do {
            try FileManager.default.createDirectory(at: path.deletingLastPathComponent(), withIntermediateDirectories: true)
            let data = try JSONSerialization.data(withJSONObject: s.json, options: [.prettyPrinted, .withoutEscapingSlashes])
            try data.write(to: path, options: .atomic)
            paths[s.id] = path
            unsaved.remove(s.id)
            // 换过保存位置的旧对话不放进新位置的列表
            if path.deletingLastPathComponent().standardizedFileURL == folder.standardizedFileURL {
                cache[s.id] = (s, MindStore.mtime(path) ?? Date())
            }
        } catch {
            onError?("对话没保存成功：\(error.localizedDescription)（可以在设置里换一个保存位置）")
        }
    }

    func delete(_ id: String) {
        deleted.insert(id)
        timers[id]?.cancel()
        timers[id] = nil
        toWrite.removeValue(forKey: id)
        let p = pathOf(id)
        cache.removeValue(forKey: id)
        unsaved.remove(id)
        paths.removeValue(forKey: id)
        try? FileManager.default.removeItem(at: p)
    }

    // ---------------- 附件 ----------------
    // 插件的布局：<库>/<Mindflow>/对话数据/*.json 和 <库>/<Mindflow>/附件/，消息里记「相对库根目录」的路径。
    // 这里一样：附件放在对话数据旁边的「附件」文件夹；往上找到 .obsidian 就以那里为根，找不到就以上一层为根。

    private var chatDir: URL {
        let f = folder.standardizedFileURL
        return f.lastPathComponent == "对话数据" ? f.deletingLastPathComponent() : f
    }

    private var rootFor: URL?
    private var rootVal: URL?

    private var baseRoot: URL {
        let chat = chatDir
        if rootFor == chat, let r = rootVal { return r }
        var root: URL?
        var d = chat
        while true {
            if FileManager.default.fileExists(atPath: d.appendingPathComponent(".obsidian").path) { root = d; break }
            let up = d.deletingLastPathComponent()
            if up.path == d.path || d.path == "/" { break }
            d = up
        }
        let r = root ?? chat.deletingLastPathComponent()
        rootFor = chat
        rootVal = r
        return r
    }

    var attachDir: URL { chatDir.appendingPathComponent("附件", isDirectory: true) }

    /// 附件的完整路径
    func resolve(_ a: MindAttachment) -> URL? {
        if a.path.isEmpty { return nil }
        if a.path.hasPrefix("/") { return URL(fileURLWithPath: a.path) }
        let p = baseRoot.appendingPathComponent(a.path)
        if FileManager.default.fileExists(atPath: p.path) { return p }
        // 兜底：按文件名在附件文件夹里找
        let q = attachDir.appendingPathComponent((a.path as NSString).lastPathComponent)
        return FileManager.default.fileExists(atPath: q.path) ? q : p
    }

    /// 把一张 JPEG 存进附件文件夹，返回附件记录
    func saveImage(_ jpeg: Data, name: String = "粘贴的图片") throws -> MindAttachment {
        try FileManager.default.createDirectory(at: attachDir, withIntermediateDirectories: true)
        let f = DateFormatter()
        f.dateFormat = "yyyyMMdd-HHmmss"
        let digits = Array("0123456789abcdefghijklmnopqrstuvwxyz")
        let stamp = f.string(from: Date()) + "-" + String((0..<4).map { _ in digits[Int.random(in: 0..<36)] })
        var base = name
        for ch in ["/", ":", "\\", "#", "^", "[", "]", "|", "?", "*", "\"", "<", ">"] {
            base = base.replacingOccurrences(of: ch, with: " ")
        }
        base = base.trimmingCharacters(in: .whitespaces)
        if base.isEmpty { base = "粘贴的图片" }
        var file = attachDir.appendingPathComponent("\(stamp)-\(base).jpg")
        var k = 2
        while FileManager.default.fileExists(atPath: file.path) {
            file = attachDir.appendingPathComponent("\(stamp)-\(base) \(k).jpg")
            k += 1
        }
        try jpeg.write(to: file, options: .atomic)
        let a = MindAttachment()
        a.kind = "image"
        a.name = file.lastPathComponent
        a.mime = "image/jpeg"
        a.size = Int64(jpeg.count)
        let rootPath = baseRoot.standardizedFileURL.path
        let full = file.standardizedFileURL.path
        if full.hasPrefix(rootPath + "/") {
            a.path = String(full.dropFirst(rootPath.count + 1))
        } else {
            a.path = full
        }
        return a
    }
}
