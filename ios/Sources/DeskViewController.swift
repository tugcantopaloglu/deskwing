import UIKit
import WebKit
import SafariServices

private final class BridgeRelay: NSObject, WKScriptMessageHandler {
    weak var owner: DeskViewController?
    func userContentController(_ userContentController: WKUserContentController, didReceive message: WKScriptMessage) { owner?.receive(message) }
}

final class DeskViewController: UIViewController, WKNavigationDelegate, WKUIDelegate {
    private var web: WKWebView!
    private var assets: LocalAssetHandler?
    private var pc: PairedPC?
    private var client: PinnedClient?
    private var penFrameClient: PinnedClient?
    private var penFrames = PenPreviewRequests()
    private var pairingClient: PinnedClient?
    private var requests = BridgeRequests()
    private var generation = 0
    private var foreground = true
    private var currentLease: String?
    private var currentPenLease: String?
    private weak var handwriting: PenInputController?
    private weak var provider: SFSafariViewController?
    private var startupWarning: String?
    private var fullscreenEnabled = false
    private var insetWebConstraints: [NSLayoutConstraint] = []
    private var fullWebConstraints: [NSLayoutConstraint] = []

    override var prefersStatusBarHidden: Bool { fullscreenEnabled }
    override var prefersHomeIndicatorAutoHidden: Bool { fullscreenEnabled }

    override func viewDidLoad() {
        super.viewDidLoad(); view.backgroundColor = UIColor(red: 16/255, green: 19/255, blue: 24/255, alpha: 1)
        do { pc = try PairingStore.load(); if let pc { client = makeClient(pc) } }
        catch { startupWarning = error.localizedDescription }
        do { assets = try LocalAssetHandler() } catch { showFatal(error.localizedDescription); return }
        let configuration = WKWebViewConfiguration(); configuration.websiteDataStore = .default()
        configuration.preferences.javaScriptCanOpenWindowsAutomatically = false
        configuration.allowsInlineMediaPlayback = false; configuration.mediaTypesRequiringUserActionForPlayback = .all
        configuration.setURLSchemeHandler(assets, forURLScheme: "deskwing")
        let relay = BridgeRelay(); relay.owner = self
        configuration.userContentController.add(relay, name: "deck")
        let bridge = """
        Object.defineProperty(window,'DeckNative',{value:Object.freeze({dispatch:function(message){window.webkit.messageHandlers.deck.postMessage(message);}}),writable:false,configurable:false});
        Object.defineProperty(window,'DeckCapabilities',{value:Object.freeze({platform:'ios',transports:['wifi'],qrPairing:false,handwriting:true,fastLink:false}),writable:false,configurable:false});
        """
        configuration.userContentController.addUserScript(WKUserScript(source: bridge, injectionTime: .atDocumentStart, forMainFrameOnly: true))
        if let file = Bundle.main.url(forResource: "ios-platform", withExtension: "js"), let adapter = try? String(contentsOf: file, encoding: .utf8) {
            configuration.userContentController.addUserScript(WKUserScript(source: adapter, injectionTime: .atDocumentEnd, forMainFrameOnly: true))
        }
        web = WKWebView(frame: .zero, configuration: configuration); web.navigationDelegate = self; web.uiDelegate = self
        web.isOpaque = false; web.backgroundColor = view.backgroundColor; web.scrollView.backgroundColor = view.backgroundColor
        web.scrollView.contentInsetAdjustmentBehavior = .never
        web.allowsLinkPreview = false; web.translatesAutoresizingMaskIntoConstraints = false
        view.addSubview(web)
        NSLayoutConstraint.activate([web.leadingAnchor.constraint(equalTo: view.leadingAnchor), web.trailingAnchor.constraint(equalTo: view.trailingAnchor)])
        insetWebConstraints = [web.topAnchor.constraint(equalTo: view.safeAreaLayoutGuide.topAnchor), web.bottomAnchor.constraint(equalTo: view.safeAreaLayoutGuide.bottomAnchor)]
        fullWebConstraints = [web.topAnchor.constraint(equalTo: view.topAnchor), web.bottomAnchor.constraint(equalTo: view.bottomAnchor)]
        NSLayoutConstraint.activate(insetWebConstraints)
        NotificationCenter.default.addObserver(self, selector: #selector(suspend), name: UIApplication.willResignActiveNotification, object: nil)
        NotificationCenter.default.addObserver(self, selector: #selector(resume), name: UIApplication.didBecomeActiveNotification, object: nil)
        web.load(URLRequest(url: URL(string: "deskwing://app/index.html")!))
        UIApplication.shared.isIdleTimerDisabled = true
    }
    deinit { NotificationCenter.default.removeObserver(self); client?.invalidate(); pairingClient?.invalidate(); penFrameClient?.invalidate() }
    private func makeClient(_ pc: PairedPC) -> PinnedClient { PinnedClient(host: pc.host, port: pc.port, pin: pc.fingerprint, token: pc.token) }
    private func showFatal(_ message: String) {
        let label = UILabel(); label.text = message; label.numberOfLines = 0; label.textAlignment = .center
        label.textColor = .white; label.frame = view.bounds.insetBy(dx: 24, dy: 24); label.autoresizingMask = [.flexibleWidth, .flexibleHeight]; view.addSubview(label)
    }
    fileprivate func receive(_ message: WKScriptMessage) {
        guard message.name == "deck", message.frameInfo.isMainFrame, message.frameInfo.securityOrigin.protocol == "deskwing",
              message.frameInfo.securityOrigin.host == "app", HostPolicy.trustedPage(message.frameInfo.request.url),
              HostPolicy.trustedPage(web.url), let raw = message.body as? String,
              raw.utf8.count <= 12 * 1024 * 1024, let bytes = raw.data(using: .utf8),
              let request = try? JSONSerialization.jsonObject(with: bytes) as? [String: Any],
              let number = request["id"] as? NSNumber, CFGetTypeID(number) != CFBooleanGetTypeID(),
              number.doubleValue == Double(number.intValue), (1...Int(Int32.max)).contains(number.intValue),
              let path = request["path"] as? String, let method = request["method"] as? String else { return }
        let id = number.intValue
        guard !requests.contains(id) else { return }
        guard requests.count < 32 else { sendDirect(id, ["error":"Too many pending requests. Wait for the PC, then try again."]); return }
        guard let ticket = requests.begin(id) else { return }
        let body = request["body"] as? [String: Any] ?? [:]
        guard method == "GET" || method == "POST", bytes.count <= HostPolicy.maximumBridgeBytes(path) else {
            fail(id, ticket: ticket, "This request is invalid or too large."); return
        }
        if path.hasPrefix("/native/") || path.hasPrefix("/local/") {
            guard method == "POST" else { fail(id, ticket: ticket, "This local request requires POST."); return }
            local(id, ticket: ticket, path: path, body: body); return
        }
        guard HostPolicy.allowed(path, method: method) else { fail(id, ticket: ticket, "This request is not allowed from the tablet."); return }
        guard foreground || HostPolicy.allowedInBackground(path, body: body) else { fail(id, ticket: ticket, "Controls are paused while DeskWing is in the background."); return }
        if path == "/api/pen/frame" { requestPenFrame(id, ticket: ticket, body: body); return }
        guard let client else { fail(id, ticket: ticket, "Pair this tablet with your Windows PC first."); return }
        if path == "/api/pen" {
            let operation = body["operation"] as? String ?? ""
            if operation == "begin" || (["end", "cancel"].contains(operation) && body["lease"] as? String == currentPenLease) { stopPenFrames() }
        }
        let epoch = generation
        do {
            try client.send(path: path, method: method, body: body) { [weak self] result in
                guard let self else { return }
                guard epoch == self.generation || HostPolicy.allowedInBackground(path, body: body) else {
                    // A begin response can arrive after suspension. Release that exact old-PC lease instead of retaining it.
                    if case .success(let response) = result, let lease = response["lease"] as? String,
                       body["operation"] as? String == "begin", path == "/api/hold" || path == "/api/pen" {
                        let operation = path == "/api/pen" ? "cancel" : "release"
                        try? client.send(path: path, method: "POST", body: ["operation":operation, "lease":lease]) { _ in }
                    }
                    self.fail(id, ticket: ticket, "The connection changed before confirmation. The action may have run; it was not repeated."); return
                }
                switch result {
                case .success(let response):
                    if path == "/api/hold" {
                        if body["operation"] as? String == "release", body["lease"] as? String == self.currentLease { self.currentLease = nil }
                        else if let lease = response["lease"] as? String { self.currentLease = lease }
                    }
                    if path == "/api/pen" {
                        if ["end", "cancel"].contains(body["operation"] as? String ?? ""), body["lease"] as? String == self.currentPenLease { self.currentPenLease = nil }
                        else if body["operation"] as? String == "begin", let lease = response["lease"] as? String {
                            self.stopPenFrames(); self.currentPenLease = lease; self.penFrames.activate(lease)
                        }
                    }
                    self.complete(id, ticket: ticket, response)
                case .failure(let error): self.fail(id, ticket: ticket, error.localizedDescription)
                }
            }
        } catch { fail(id, ticket: ticket, error.localizedDescription) }
    }
    private func requestPenFrame(_ id: Int, ticket: UUID, body: [String: Any]) {
        do {
            let lease = try HostPolicy.penFrameLease(body)
            guard foreground, presentedViewController == nil, let pc, lease == currentPenLease else { throw DeskError.message("Start pen input before requesting a PC preview.") }
            let frameTicket = try penFrames.begin(lease)
            if penFrameClient == nil {
                penFrameClient = PinnedClient(host: pc.host, port: pc.port, pin: pc.fingerprint, token: pc.token, penPreviewOnly: true)
            }
            guard let preview = penFrameClient else { _ = penFrames.finish(frameTicket); throw DeskError.message("The PC preview is unavailable.") }
            let epoch = generation
            do {
                try preview.send(path: "/api/pen/frame", method: "POST", body: body) { [weak self] result in
                    guard let self else { return }
                    let current = self.penFrames.finish(frameTicket)
                    guard current, self.foreground, self.presentedViewController == nil, epoch == self.generation,
                          self.currentPenLease == lease, self.penFrameClient === preview else {
                        self.fail(id, ticket: ticket, "The PC preview stopped when the pen session changed."); return
                    }
                    switch result {
                    case .success(let response): self.complete(id, ticket: ticket, response)
                    case .failure(let error): self.fail(id, ticket: ticket, error.localizedDescription)
                    }
                }
            } catch { _ = penFrames.finish(frameTicket); throw error }
        } catch { fail(id, ticket: ticket, error.localizedDescription) }
    }
    private func stopPenFrames() {
        penFrames.reset(); penFrameClient?.invalidate(); penFrameClient = nil
    }
    private func local(_ id: Int, ticket: UUID, path: String, body: [String: Any]) {
        if path == "/native/connection" || path == "/native/diagnostics/reset" { complete(id, ticket: ticket, connection()); return }
        if path == "/native/chat", body["operation"] as? String == "hide" { provider?.dismiss(animated: true); complete(id, ticket: ticket, ["ok":true]); return }
        if path == "/local/fullscreen" {
            do {
                let enabled = try HostPolicy.requestedFullscreen(body, foreground: foreground, presentingTool: presentedViewController != nil)
                setFullscreen(enabled, reason: "request")
                complete(id, ticket: ticket, ["enabled":fullscreenEnabled, "supported":true])
            } catch { fail(id, ticket: ticket, error.localizedDescription) }
            return
        }
        guard foreground else { fail(id, ticket: ticket, "Open DeskWing before using this tool."); return }
        do {
            switch path {
            case "/native/pair": try pair(id, ticket: ticket, body: body)
            case "/native/forget":
                try PairingStore.forget(); retireClient(); pc = nil; complete(id, ticket: ticket, connection())
            case "/native/transport":
                guard body["mode"] as? String == "wifi" else { throw DeskError.message("iOS connects to your PC over Wi-Fi/LAN. Android's USB tunnel is not supported on iOS.") }
                guard let current = pc, body["host"] as? String == current.host else { throw DeskError.message("Pair again to verify a different PC address.") }
                complete(id, ticket: ticket, connection())
            case "/native/scan": throw DeskError.message("Use manual pairing on iOS: enter the PC address, certificate code, and PIN.")
            case "/local/handwriting": try showHandwriting(id, ticket: ticket, body: body)
            case "/local/export-image": try exportImage(id, ticket: ticket, body: body)
            case "/native/chat": try showChat(id, ticket: ticket, body: body)
            default: throw DeskError.message("This local request is not supported on iOS.")
            }
        } catch { fail(id, ticket: ticket, error.localizedDescription) }
    }
    private func connection() -> [String: Any] {
        ["paired":pc != nil, "host":pc?.host ?? "", "mode":"wifi", "pairingId":pc?.pairingID ?? "",
         "port":pc?.port ?? 8743, "controlPort":pc?.controlPort ?? 8744, "fastConnected":false,
         "roundTripMs":-1, "diagnostics":[:], "platform":"ios"]
    }
    private func pair(_ id: Int, ticket: UUID, body: [String: Any]) throws {
        guard pairingClient == nil else { throw DeskError.message("Pairing is already in progress.") }
        let host = (body["host"] as? String ?? "").trimmingCharacters(in: .whitespacesAndNewlines)
        guard (body["mode"] as? String ?? "wifi") == "wifi", HostPolicy.validHost(host) else { throw DeskError.message("Use the PC's private numeric IPv4 address on the same Wi-Fi/LAN.") }
        guard let pin = HostPolicy.normalizedPin(body["certificate"] as? String ?? "") else { throw DeskError.message("Enter the 24-character certificate code shown on your PC.") }
        let code = body["code"] as? String ?? ""
        guard code.count == 6, code.utf8.allSatisfy({ (48...57).contains($0) }) else { throw DeskError.message("Enter the six-digit pairing PIN.") }
        let port = body["port"] as? Int ?? 8743, controlPort = body["controlPort"] as? Int ?? 8744
        guard (1...65535).contains(port), (1...65535).contains(controlPort) else { throw DeskError.message("Invalid pairing ports.") }
        let pairing = PinnedClient(host: host, port: port, pin: pin, waitsForConnectivity: true)
        pairingClient = pairing; let epoch = generation
        do {
            try pairing.send(path: "/api/pair", method: "POST", body: ["code":code, "name":"DeskWing " + UIDevice.current.model], pairing: true) { [weak self] result in
                guard let self else { return }
                defer { pairing.invalidate(); if self.pairingClient === pairing { self.pairingClient = nil } }
                guard epoch == self.generation, self.foreground else { self.fail(id, ticket: ticket, "Pairing stopped when the app left the foreground. Generate a fresh PIN and try again."); return }
                do {
                    let response = try result.get()
                    guard let fingerprint = response["fingerprint"] as? String, let token = response["token"] as? String,
                          fingerprint == pairing.verifiedFingerprint, HostPolicy.pinMatches(pin, fingerprint) else {
                        throw DeskError.message("The pairing response did not match the verified PC certificate.")
                    }
                    let saved = PairedPC(host: host, port: port, controlPort: controlPort, fingerprint: fingerprint, token: token, pairingID: UUID().uuidString)
                    try PairingStore.save(saved); self.retireClient(); self.pc = saved; self.client = self.makeClient(saved)
                    self.complete(id, ticket: ticket, self.connection())
                } catch { self.fail(id, ticket: ticket, error.localizedDescription) }
            }
        } catch { pairingClient = nil; pairing.invalidate(); throw error }
    }
    private func retireClient() {
        setFullscreen(false, reason: "disconnect")
        generation += 1; releaseInputLeases()
        let old = client; old?.cancelPending(keepingReleases: true); client = nil
        DispatchQueue.main.asyncAfter(deadline: .now() + 2) { old?.invalidate() }
    }
    private func releaseInputLeases() {
        stopPenFrames()
        let keyboard = currentLease, pen = currentPenLease; currentLease = nil; currentPenLease = nil
        guard let client else { return }
        if let keyboard { try? client.send(path: "/api/hold", method: "POST", body: ["operation":"release", "lease":keyboard]) { _ in } }
        if let pen { try? client.send(path: "/api/pen", method: "POST", body: ["operation":"cancel", "lease":pen]) { _ in } }
    }
    @objc private func suspend() {
        setFullscreen(false, reason: "background")
        UIApplication.shared.isIdleTimerDisabled = false
        foreground = false; generation += 1; pairingClient?.invalidate(); pairingClient = nil
        releaseInputLeases(); client?.cancelPending(keepingReleases: true); handwriting?.cancel()
        web?.evaluateJavaScript("if(typeof releaseDeckHold==='function')releaseDeckHold();if(window.penTabletSuspend)window.penTabletSuspend();if(window.monitorSuspend)window.monitorSuspend();if(window.penSuspend)window.penSuspend();if(window.deckStreamClosed)window.deckStreamClosed();", completionHandler: nil)
    }
    @objc private func resume() {
        UIApplication.shared.isIdleTimerDisabled = true
        foreground = true
        web?.evaluateJavaScript("if(window.monitorResume)window.monitorResume();if(window.DeskPen&&window.DeskPen.resume)window.DeskPen.resume();if(window.penTabletResume)window.penTabletResume();if(typeof refresh==='function')refresh(true);", completionHandler: nil)
    }
    private func showHandwriting(_ id: Int, ticket: UUID, body: [String: Any]) throws {
        guard presentedViewController == nil else { throw DeskError.message("Finish the open sheet before using handwriting.") }
        let text = body["text"] as? String ?? ""
        guard text.utf16.count <= 8000 else { throw DeskError.message("Handwriting fields support up to 8,000 characters.") }
        stopPenFrames()
        setFullscreen(false, reason: "tool")
        let editor = PenInputController(text: text) { [weak self] text, cancelled in self?.complete(id, ticket: ticket, ["text":text,"cancelled":cancelled]) }
        handwriting = editor
        let navigation = UINavigationController(rootViewController: editor); navigation.modalPresentationStyle = .formSheet
        present(navigation, animated: true)
    }
    private func exportImage(_ id: Int, ticket: UUID, body: [String: Any]) throws {
        guard presentedViewController == nil else { throw DeskError.message("Finish the open sheet before exporting a drawing.") }
        let data = try PNGExport.decode(body["dataUrl"] as? String ?? "")
        stopPenFrames()
        setFullscreen(false, reason: "tool")
        let folder = FileManager.default.temporaryDirectory.appendingPathComponent("DeskWing-export-" + UUID().uuidString, isDirectory: true)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        let url = folder.appendingPathComponent(PNGExport.safeName(body["name"] as? String ?? ""))
        try data.write(to: url, options: [.atomic, .completeFileProtection])
        let sheet = UIActivityViewController(activityItems: [url], applicationActivities: nil)
        sheet.popoverPresentationController?.sourceView = view
        sheet.popoverPresentationController?.sourceRect = CGRect(x: view.bounds.midX, y: view.bounds.midY, width: 1, height: 1)
        sheet.completionWithItemsHandler = { [weak self] _, completed, _, error in
            try? FileManager.default.removeItem(at: folder)
            if let error { self?.fail(id, ticket: ticket, error.localizedDescription) } else { self?.complete(id, ticket: ticket, ["saved":completed,"cancelled":!completed]) }
        }
        present(sheet, animated: true)
    }
    private func showChat(_ id: Int, ticket: UUID, body: [String: Any]) throws {
        if body["operation"] as? String == "bounds" { complete(id, ticket: ticket, ["ok":true]); return }
        guard body["operation"] as? String == "show", presentedViewController == nil,
              let providerName = body["provider"] as? String, let channel = body["channel"] as? String else { throw DeskError.message("Close the open sheet before loading a provider chat.") }
        let pattern = providerName == "twitch" ? "^[a-z0-9_]{1,25}$" : providerName == "kick" ? "^[a-z0-9_-]{1,50}$" : "(?!)"
        guard channel.range(of: pattern, options: .regularExpression) != nil else { throw DeskError.message("Enter a valid Twitch or Kick channel name.") }
        let address = providerName == "twitch" ? "https://www.twitch.tv/popout/\(channel)/chat?popout=" : "https://kick.com/popout/\(channel)/chat"
        guard let url = URL(string: address) else { throw DeskError.message("Invalid provider address.") }
        stopPenFrames()
        setFullscreen(false, reason: "tool")
        let sheet = SFSafariViewController(url: url); provider = sheet; present(sheet, animated: true)
        complete(id, ticket: ticket, ["ok":true])
        let event: [String: Any] = ["session":body["session"] as? Int ?? 0, "state":"Chat opened in a separate provider viewer. It has no PC-control bridge."]
        if let data = try? JSONSerialization.data(withJSONObject: event), let json = String(data: data, encoding: .utf8) {
            web.evaluateJavaScript("if(window.monitorChatEvent)window.monitorChatEvent(\(json));", completionHandler: nil)
        }
    }
    private func fail(_ id: Int, ticket: UUID, _ message: String) { complete(id, ticket: ticket, ["error":String(message.prefix(1000))]) }
    private func setFullscreen(_ enabled: Bool, reason: String) {
        guard fullscreenEnabled != enabled else { return }
        fullscreenEnabled = enabled
        NSLayoutConstraint.deactivate(enabled ? insetWebConstraints : fullWebConstraints)
        NSLayoutConstraint.activate(enabled ? fullWebConstraints : insetWebConstraints)
        setNeedsStatusBarAppearanceUpdate()
        setNeedsUpdateOfHomeIndicatorAutoHidden()
        view.setNeedsLayout()
        guard HostPolicy.trustedPage(web?.url),
              let data = try? JSONSerialization.data(withJSONObject: ["enabled":enabled, "reason":reason]),
              let json = String(data: data, encoding: .utf8) else { return }
        web.evaluateJavaScript("document.dispatchEvent(new CustomEvent('deskwing-fullscreen',{detail:\(json)}));", completionHandler: nil)
    }
    private func complete(_ id: Int, ticket: UUID, _ result: [String: Any]) {
        guard requests.finish(id, ticket: ticket) else { return }; sendDirect(id, result)
    }
    private func sendDirect(_ id: Int, _ result: [String: Any]) {
        guard HostPolicy.trustedPage(web?.url), JSONSerialization.isValidJSONObject(result),
              let data = try? JSONSerialization.data(withJSONObject: result, options: [.fragmentsAllowed]),
              let json = String(data: data, encoding: .utf8) else { return }
        web.evaluateJavaScript("if(window.nativeResult)window.nativeResult(\(id),\(json));", completionHandler: nil)
    }
    func webView(_ webView: WKWebView, decidePolicyFor navigationAction: WKNavigationAction, decisionHandler: @escaping (WKNavigationActionPolicy) -> Void) {
        decisionHandler(navigationAction.targetFrame?.isMainFrame == true && HostPolicy.trustedPage(navigationAction.request.url) ? .allow : .cancel)
    }
    func webView(_ webView: WKWebView, didFinish navigation: WKNavigation!) {
        if let warning = startupWarning {
            startupWarning = nil
            let alert = UIAlertController(title: "PC pairing", message: warning, preferredStyle: .alert)
            alert.addAction(UIAlertAction(title: "OK", style: .default)); present(alert, animated: true)
        }
    }
    func webView(_ webView: WKWebView, didStartProvisionalNavigation navigation: WKNavigation!) {
        resetWebSession(reason: "navigation")
    }
    func webViewWebContentProcessDidTerminate(_ webView: WKWebView) {
        resetWebSession(reason: "renderer")
        webView.reload()
    }
    private func resetWebSession(reason: String) {
        setFullscreen(false, reason: reason)
        generation += 1; requests.reset(); handwriting?.cancel(); provider?.dismiss(animated: false)
        pairingClient?.invalidate(); pairingClient = nil
        releaseInputLeases(); client?.cancelPending(keepingReleases: true)
    }
}
