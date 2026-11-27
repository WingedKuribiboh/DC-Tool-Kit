// Toolkit Updater (macOS) - the Mac version of UpdateToolkit.exe, with the same window and features.
// It runs the update modules (system/Update/update_isos.py, update_tools.py) with --machine, reads their
// progress lines (@@ENTRY / @@PROG / @@DONE) and shows them as progress bars and a coloured log.
//
// Python comes from system/Update/UpdateToolkit.command --print-python (installed python3, or a portable one it downloads
// into ~/Library/Caches/DCToolKit).
//
//   open UpdateToolkit.app --args all|isos|tools [/check] [/force] [/root:/Volumes/STICK]
//
// Build: src/UpdateToolkitMac/build.sh (Xcode command line tools; writes UpdateToolkit.app at the repo root).
import AppKit

struct Mod { let label: String; let script: String }
let Isos = Mod(label: "ISOs", script: "update_isos.py")
let Tools = Mod(label: "Tools", script: "update_tools.py")

final class Controller: NSObject, NSWindowDelegate {
    let window: NSWindow
    var root: String?
    var updDir: String? { root.map { ($0 as NSString).appendingPathComponent("system/Update") } }

    let lblRoot = NSTextField(labelWithString: "")
    let lblStatus = NSTextField(labelWithString: "Ready.")
    let lblItem = NSTextField(labelWithString: "")
    let lblSummary = NSTextField(labelWithString: "")
    let pbAll = NSProgressIndicator()
    let pbItem = NSProgressIndicator()
    let logView = NSTextView()
    var btnAll: NSButton!, btnIso: NSButton!, btnTools: NSButton!, btnCancel: NSButton!, btnRoot: NSButton!
    let chkCheck = NSButton(checkboxWithTitle: "Check only (download nothing)", target: nil, action: nil)
    let chkForce = NSButton(checkboxWithTitle: "Force re-download", target: nil, action: nil)

    var running = false
    var cancel = false
    var proc: Process?
    var activity: NSObjectProtocol?
    var modIndex = 0, modCount = 1, entIndex = 1, entCount = 1
    var totalOk = 0, totalFail = 0
    var modLabel = ""

    override init() {
        window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 760, height: 600),
                          styleMask: [.titled, .closable, .miniaturizable, .resizable], backing: .buffered, defer: false)
        super.init()
        window.title = "Toolkit Updater"
        window.minSize = NSSize(width: 640, height: 480)
        window.delegate = self
        buildUI()
        window.center()
    }

    // ---------------------------------------------------------------- UI

    func button(_ title: String, _ action: Selector) -> NSButton {
        let b = NSButton(title: title, target: self, action: action)
        b.bezelStyle = .rounded
        return b
    }

    func buildUI() {
        let title = NSTextField(labelWithString: "Toolkit Updater")
        title.font = .boldSystemFont(ofSize: 20)

        btnRoot = button("Change…", #selector(pickRoot))
        lblRoot.lineBreakMode = .byTruncatingMiddle
        lblRoot.setContentCompressionResistancePriority(.defaultLow, for: .horizontal)
        let rootRow = NSStackView(views: [lblRoot, NSView(), btnRoot])

        btnAll = button("Update All", #selector(updateAll))
        btnAll.keyEquivalent = "\r"
        btnAll.font = .boldSystemFont(ofSize: NSFont.systemFontSize)
        btnIso = button("Update ISOs", #selector(updateIsos))
        btnTools = button("Update Tools", #selector(updateTools))
        btnCancel = button("Cancel", #selector(doCancel))
        btnCancel.isEnabled = false
        for b in [btnAll!, btnIso!, btnTools!] { b.controlSize = .large; b.widthAnchor.constraint(equalToConstant: 140).isActive = true }
        btnCancel.controlSize = .large
        let btnRow = NSStackView(views: [btnAll, btnIso, btnTools, btnCancel])

        let chkRow = NSStackView(views: [chkCheck, chkForce])
        chkRow.spacing = 24

        lblStatus.font = .boldSystemFont(ofSize: NSFont.systemFontSize)
        for l in [lblStatus, lblItem] { l.lineBreakMode = .byTruncatingTail; l.setContentCompressionResistancePriority(.defaultLow, for: .horizontal) }
        for p in [pbAll, pbItem] { p.style = .bar; p.isIndeterminate = false; p.minValue = 0; p.maxValue = 1000 }

        let scroll = NSScrollView()
        scroll.hasVerticalScroller = true
        scroll.hasHorizontalScroller = true
        scroll.borderType = .bezelBorder
        logView.isEditable = false
        logView.font = .monospacedSystemFont(ofSize: 11, weight: .regular)
        logView.isHorizontallyResizable = true
        logView.textContainer?.widthTracksTextView = false
        logView.textContainer?.containerSize = NSSize(width: CGFloat.greatestFiniteMagnitude, height: CGFloat.greatestFiniteMagnitude)
        logView.maxSize = NSSize(width: CGFloat.greatestFiniteMagnitude, height: CGFloat.greatestFiniteMagnitude)
        logView.autoresizingMask = [.width]
        scroll.documentView = logView

        lblSummary.font = .boldSystemFont(ofSize: NSFont.systemFontSize)
        let bottomRow = NSStackView(views: [lblSummary, NSView(), button("Open ver.log", #selector(openVerLog)),
                                            button("Close", #selector(closeWindow))])

        let stack = NSStackView(views: [title, rootRow, btnRow, chkRow, lblStatus, pbAll, lblItem, pbItem, scroll, bottomRow])
        stack.orientation = .vertical
        stack.alignment = .leading
        stack.spacing = 8
        stack.edgeInsets = NSEdgeInsets(top: 14, left: 16, bottom: 14, right: 16)
        stack.setCustomSpacing(14, after: chkRow)
        stack.translatesAutoresizingMaskIntoConstraints = false
        window.contentView = NSView()
        window.contentView!.addSubview(stack)
        NSLayoutConstraint.activate([
            stack.leadingAnchor.constraint(equalTo: window.contentView!.leadingAnchor),
            stack.trailingAnchor.constraint(equalTo: window.contentView!.trailingAnchor),
            stack.topAnchor.constraint(equalTo: window.contentView!.topAnchor),
            stack.bottomAnchor.constraint(equalTo: window.contentView!.bottomAnchor),
        ])
        for v in [rootRow, lblStatus, pbAll, lblItem, pbItem, scroll, bottomRow] as [NSView] {
            v.widthAnchor.constraint(equalTo: stack.widthAnchor, constant: -32).isActive = true
        }
        scroll.setContentHuggingPriority(.defaultLow, for: .vertical)
        scroll.heightAnchor.constraint(greaterThanOrEqualToConstant: 150).isActive = true
    }

    func appendLog(_ text: String, _ color: NSColor = .labelColor) {
        let attrs: [NSAttributedString.Key: Any] = [.foregroundColor: color, .font: logView.font!]
        logView.textStorage?.append(NSAttributedString(string: text, attributes: attrs))
        logView.scrollToEndOfDocument(nil)
    }

    func setBusy(_ busy: Bool) {
        for b in [btnAll!, btnIso!, btnTools!, btnRoot!, chkCheck, chkForce] { b.isEnabled = !busy }
        btnCancel.isEnabled = busy
    }

    func itemMarquee(_ on: Bool) {
        if on && !pbItem.isIndeterminate { pbItem.isIndeterminate = true; pbItem.startAnimation(nil) }
        else if !on && pbItem.isIndeterminate { pbItem.stopAnimation(nil); pbItem.isIndeterminate = false }
    }

    func setOverall(_ entryFraction: Double) {
        let perMod = 1.0 / Double(max(1, modCount))
        let inMod = (Double(entIndex - 1) + entryFraction) / Double(max(1, entCount))
        pbAll.doubleValue = max(0, min(1000, (Double(modIndex) + inMod) * perMod * 1000))
    }

    static func human(_ n: Int64) -> String {
        let u = ["B", "KB", "MB", "GB"]
        var d = Double(n), i = 0
        while d >= 1024 && i < u.count - 1 { d /= 1024; i += 1 }
        return i == 0 ? "\(n) B" : String(format: "%.1f %@", d, u[i])
    }

    // ---------------------------------------------------------------- root

    static func isRoot(_ dir: String) -> Bool {
        FileManager.default.fileExists(atPath: (dir as NSString).appendingPathComponent("system/Update/update_isos.py"))
    }

    static func findRoot() -> String? {
        var d = Bundle.main.bundleURL.deletingLastPathComponent().path   // folder that holds the .app
        while true {
            if isRoot(d) { return d }
            let p = (d as NSString).deletingLastPathComponent
            if p == d || p.isEmpty { return nil }
            d = p
        }
    }

    func setRoot(_ r: String?) {
        if let r = r, Controller.isRoot(r) {
            root = r.count > 1 && r.hasSuffix("/") ? String(r.dropLast()) : r
            lblRoot.stringValue = "Toolkit drive: " + root!
        } else {
            root = nil
            lblRoot.stringValue = "Toolkit drive: (not found)"
        }
    }

    @objc func pickRoot() {
        let p = NSOpenPanel()
        p.canChooseDirectories = true; p.canChooseFiles = false; p.allowsMultipleSelection = false
        p.message = "Select the toolkit drive (the folder that contains ISO, Tools and system)."
        p.directoryURL = URL(fileURLWithPath: root ?? "/Volumes")
        guard p.runModal() == .OK, let url = p.url else { return }
        setRoot(url.path)
        if root == nil { alert("That folder does not contain system/Update/update_isos.py.") }
    }

    @objc func openVerLog() {
        guard let u = updDir else { return }
        let p = (u as NSString).appendingPathComponent("ver.log")
        if FileManager.default.fileExists(atPath: p) {
            NSWorkspace.shared.open([URL(fileURLWithPath: p)], withApplicationAt: URL(fileURLWithPath: "/System/Applications/TextEdit.app"),
                                    configuration: NSWorkspace.OpenConfiguration(), completionHandler: nil)
        } else {
            alert("ver.log does not exist yet. It is created after the first run.")
        }
    }

    func alert(_ text: String) {
        let a = NSAlert(); a.messageText = "Toolkit Updater"; a.informativeText = text
        a.beginSheetModal(for: window, completionHandler: nil)
    }

    // ---------------------------------------------------------------- run control

    @objc func updateAll() { start([Isos, Tools]) }
    @objc func updateIsos() { start([Isos]) }
    @objc func updateTools() { start([Tools]) }
    @objc func closeWindow() { window.performClose(nil) }

    func start(_ mods: [Mod]) {
        if root == nil { pickRoot(); if root == nil { return } }
        if running { return }
        running = true; cancel = false
        logView.string = ""
        lblSummary.stringValue = ""; lblSummary.textColor = .labelColor
        pbAll.doubleValue = 0
        totalOk = 0; totalFail = 0
        setBusy(true)
        // Keep the Mac awake during long downloads (like caffeinate).
        activity = ProcessInfo.processInfo.beginActivity(options: [.userInitiated, .idleSystemSleepDisabled],
                                                         reason: "Downloading toolkit updates")
        let check = chkCheck.state == .on, force = chkForce.state == .on
        let root = self.root!, upd = self.updDir!
        DispatchQueue.global(qos: .userInitiated).async {
            do {
                self.ui { self.lblStatus.stringValue = "Looking for Python…"; self.itemMarquee(true); self.lblItem.stringValue = "" }
                let py = try self.ensurePython(root: root)
                self.ui { self.modCount = mods.count }
                for (i, m) in mods.enumerated() {
                    if self.cancel { break }
                    self.ui { self.modIndex = i; self.modLabel = m.label; self.entIndex = 1; self.entCount = 1 }
                    try self.runModule(py: py, mod: m, root: root, upd: upd, check: check, force: force)
                }
                self.ui { self.finish() }
            } catch {
                let msg = (error as? RunError)?.message ?? error.localizedDescription
                self.ui {
                    self.appendLog("ERROR: \(msg)\n", .systemRed)
                    self.lblStatus.stringValue = "Stopped."
                    self.lblSummary.stringValue = "Failed to run."; self.lblSummary.textColor = .systemRed
                    self.itemMarquee(false); self.pbItem.doubleValue = 0; self.lblItem.stringValue = ""
                    self.done()
                }
            }
        }
    }

    func done() {
        running = false
        setBusy(false)
        if let a = activity { ProcessInfo.processInfo.endActivity(a); activity = nil }
        cleanDotUnderscore()
    }

    func finish() {
        itemMarquee(false)
        pbItem.doubleValue = 0; lblItem.stringValue = ""
        if cancel {
            lblStatus.stringValue = "Cancelled."
            lblSummary.stringValue = "Cancelled. Partial downloads resume next time."
            lblSummary.textColor = .systemOrange
            appendLog("\nCancelled. Partial downloads (*.part) resume on the next run.\n", .systemOrange)
        } else {
            pbAll.doubleValue = 1000
            lblStatus.stringValue = "Finished."
            lblSummary.stringValue = "\(totalOk) ok, \(totalFail) failed"
            lblSummary.textColor = totalFail == 0 ? .systemGreen : .systemRed
        }
        done()
    }

    // macOS stores extended attributes as ._* files on FAT/exFAT, which Windows shows. ._. keeps the drive icon.
    func cleanDotUnderscore() {
        guard let r = root else { return }
        let p = Process()
        p.executableURL = URL(fileURLWithPath: "/usr/bin/find")
        p.arguments = [r, "-name", "._*", "-type", "f", "!", "-path", r + "/._.", "-delete"]
        p.standardError = FileHandle.nullDevice
        try? p.run()
    }

    @objc func doCancel() {
        cancel = true
        btnCancel.isEnabled = false
        lblStatus.stringValue = "Cancelling…"
        if let p = proc, p.isRunning {
            // Stop the module and anything it started (e.g. 7zz).
            let k = Process()
            k.executableURL = URL(fileURLWithPath: "/usr/bin/pkill")
            k.arguments = ["-TERM", "-P", String(p.processIdentifier)]
            try? k.run(); k.waitUntilExit()
            p.terminate()
        }
    }

    struct RunError: Error { let message: String }

    func ui(_ block: @escaping () -> Void) { DispatchQueue.main.async(execute: block) }

    // Runs a process, calling onLine for every complete stdout/stderr line. Returns the exit code.
    func runLines(_ exe: String, _ args: [String], cwd: String?, track: Bool,
                  onLine: @escaping (String, Bool) -> Void) throws -> Int32 {
        let p = Process()
        p.executableURL = URL(fileURLWithPath: exe)
        p.arguments = args
        if let cwd = cwd { p.currentDirectoryURL = URL(fileURLWithPath: cwd) }
        var env = ProcessInfo.processInfo.environment
        env["PYTHONUNBUFFERED"] = "1"
        p.environment = env
        let out = Pipe(), err = Pipe()
        p.standardOutput = out; p.standardError = err
        let group = DispatchGroup()
        for (pipe, isErr) in [(out, false), (err, true)] {
            group.enter()
            DispatchQueue.global().async {
                var buf = Data()
                let h = pipe.fileHandleForReading
                while true {
                    let chunk = h.availableData
                    if chunk.isEmpty { break }
                    buf.append(chunk)
                    // curl's progress bar uses \r; treat it like a line end.
                    while let i = buf.firstIndex(where: { $0 == 10 || $0 == 13 }) {
                        let line = String(decoding: buf[buf.startIndex..<i], as: UTF8.self)
                        buf.removeSubrange(buf.startIndex...i)
                        onLine(line, isErr)
                    }
                }
                if !buf.isEmpty { onLine(String(decoding: buf, as: UTF8.self), isErr) }
                group.leave()
            }
        }
        try p.run()
        if track { ui { self.proc = p } }
        p.waitUntilExit()
        group.wait()
        if track { ui { self.proc = nil } }
        return p.terminationStatus
    }

    func ensurePython(root: String) throws -> String {
        let helper = (root as NSString).appendingPathComponent("system/Update/UpdateToolkit.command")
        guard FileManager.default.fileExists(atPath: helper) else { throw RunError(message: "Missing \(helper)") }
        var last = ""
        let rc = try runLines("/bin/bash", [helper, "--print-python"], cwd: root, track: true) { line, isErr in
            if isErr {
                if line.trimmingCharacters(in: .whitespaces).isEmpty { return }
                if line.contains("%") && line.contains("#") {     // curl progress bar
                    self.ui { self.lblItem.stringValue = "Downloading Python: " + line.trimmingCharacters(in: CharacterSet(charactersIn: "# ")) }
                } else {
                    self.ui { self.appendLog(line + "\n"); self.lblStatus.stringValue = "Getting Python…" }
                }
            } else if !line.isEmpty { last = line }
        }
        if cancel { throw RunError(message: "Cancelled") }
        guard rc == 0, !last.isEmpty, FileManager.default.isExecutableFile(atPath: last) else {
            throw RunError(message: "Python 3 is not available. Install it from python.org or with Homebrew, then try again.")
        }
        return last
    }

    func runModule(py: String, mod: Mod, root: String, upd: String, check: Bool, force: Bool) throws {
        let script = (upd as NSString).appendingPathComponent(mod.script)
        guard FileManager.default.fileExists(atPath: script) else { throw RunError(message: "Missing \(script)") }
        var args = ["-X", "utf8", "-u", "-I", script, "--machine", "--root", root]
        if check { args.append("--list") }
        if force { args.append("--force") }
        ui { self.appendLog("=== \(mod.label)\(check ? " (check only)" : "") ===\n") }
        _ = try runLines(py, args, cwd: upd, track: true) { line, isErr in
            self.ui { self.handleLine(line, isErr) }
        }
    }

    // ---------------------------------------------------------------- protocol (same as the Windows GUI)

    func handleLine(_ line: String, _ isErr: Bool) {
        if line.hasPrefix("@@") {
            let p = line.dropFirst(2).components(separatedBy: "|")
            if p[0] == "ENTRY" && p.count >= 5 {
                entIndex = Int(p[1]) ?? 1; entCount = Int(p[2]) ?? 1
                lblStatus.stringValue = "\(modLabel) \(entIndex) of \(entCount): \(p[4])"
                lblItem.stringValue = "Checking latest version…"
                itemMarquee(true)
                setOverall(0)
            } else if p[0] == "PROG" && p.count >= 4 {
                let done = Int64(p[2]) ?? 0, total = Int64(p[3]) ?? 0
                let label = p[1].prefix(1).uppercased() + p[1].dropFirst()
                if total > 0 {
                    itemMarquee(false)
                    let f = min(1.0, Double(done) / Double(total))
                    pbItem.doubleValue = f * 1000
                    lblItem.stringValue = "\(label): \(Controller.human(done)) / \(Controller.human(total))  (\(Int(f * 100))%)"
                    setOverall(f)
                } else {
                    itemMarquee(true)
                    lblItem.stringValue = "\(label): \(Controller.human(done))"
                }
            } else if p[0] == "DONE" && p.count >= 3 {
                totalOk += Int(p[1]) ?? 0; totalFail += Int(p[2]) ?? 0
                setOverall(1.0)
            }
            return
        }
        if line.trimmingCharacters(in: .whitespaces).isEmpty { return }
        let u = line.uppercased()
        var c = NSColor.labelColor
        if isErr || u.contains("ERROR") || u.contains("FAILED") || u.contains("NOT ENOUGH SPACE") || u.contains("TRACEBACK") { c = .systemRed }
        else if u.contains("UP TO DATE") || u.contains("SHA256 OK") || u.contains("UPDATED ->") || u.contains("RECORDED AS CURRENT") || u.contains("-> LOGGED") { c = .systemGreen }
        else if u.contains("WOULD DOWNLOAD") { c = .systemBlue }
        appendLog(line + "\n", c)
    }

    // ---------------------------------------------------------------- window

    func windowShouldClose(_ sender: NSWindow) -> Bool {
        guard running else { return true }
        let a = NSAlert()
        a.messageText = "An update is running. Cancel it and close?"
        a.addButton(withTitle: "Cancel Update and Close"); a.addButton(withTitle: "Keep Running")
        if a.runModal() != .alertFirstButtonReturn { return false }
        doCancel()
        return true
    }

    func windowWillClose(_ notification: Notification) { NSApp.terminate(nil) }

    func applyArgs(_ args: [String]) {
        var rootArg: String?, run: String?
        for a in args {
            let l = a.lowercased()
            if l == "/check" || l == "-check" || l == "--list" { chkCheck.state = .on }
            else if l == "/force" || l == "-force" || l == "--force" { chkForce.state = .on }
            else if l.hasPrefix("/root:") { rootArg = String(a.dropFirst(6)) }
            else if ["all", "isos", "tools"].contains(l) { run = l }
        }
        setRoot(rootArg ?? Controller.findRoot())
        if root == nil {
            appendLog("Could not find the toolkit drive (system/Update/update_isos.py). Click \"Change…\" and pick the drive root.\n", .systemRed)
        }
        if let run = run, root != nil {
            DispatchQueue.main.async {
                switch run { case "all": self.updateAll(); case "isos": self.updateIsos(); default: self.updateTools() }
            }
        }
    }
}

final class AppDelegate: NSObject, NSApplicationDelegate {
    var c: Controller!
    func applicationDidFinishLaunching(_ n: Notification) {
        let mainMenu = NSMenu()
        let appItem = NSMenuItem(); mainMenu.addItem(appItem)
        let appMenu = NSMenu()
        appMenu.addItem(withTitle: "Quit Toolkit Updater", action: #selector(NSApplication.terminate(_:)), keyEquivalent: "q")
        appItem.submenu = appMenu
        let editItem = NSMenuItem(); mainMenu.addItem(editItem)
        let editMenu = NSMenu(title: "Edit")
        editMenu.addItem(withTitle: "Copy", action: #selector(NSText.copy(_:)), keyEquivalent: "c")
        editMenu.addItem(withTitle: "Select All", action: #selector(NSText.selectAll(_:)), keyEquivalent: "a")
        editItem.submenu = editMenu
        NSApp.mainMenu = mainMenu

        c = Controller()
        c.applyArgs(Array(CommandLine.arguments.dropFirst()).filter { !$0.hasPrefix("-psn_") })
        c.window.makeKeyAndOrderFront(nil)
        NSApp.activate(ignoringOtherApps: true)
    }
}

let app = NSApplication.shared
let delegate = AppDelegate()
app.delegate = delegate
app.setActivationPolicy(.regular)
app.run()
