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
    private var pairingClient: PinnedClient?
    private var requestIDs = Set<Int>()
    private var generation = 0
    private var foreground = true
    private var currentLease: String?
    private weak var handwriting: PenInputController?
    private weak var provider: SFSafariViewController?
    private var startupWarning: String?

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
        web.allowsLinkPreview = false; web.translatesAutoresizingMaskIntoConstraints = false
        view.addSubview(web)
        NSLayoutConstraint.activate([web.leadingAnchor.constraint(equalTo: view.leadingAnchor), web.trailingAnchor.constraint(equalTo: view.trailingAnchor),
            web.topAnchor.constraint(equalTo: view.safeAreaLayoutGuide.topAnchor), web.bottomAnchor.constraint(equalTo: view.safeAreaLayoutGuide.bottomAnchor)])
        NotificationCenter.default.addObserver(self, selector: #selector(suspend), name: UIApplication.willResignActiveNotification, object: nil)
        NotificationCenter.default.addObserver(self, selector: #selector(resume), name: UIApplication.didBecomeActiveNotification, object: nil)
        web.load(URLRequest(url: URL(string: "deskwing://app/index.html")!))
        UIApplication.shared.isIdleTimerDisabled = true
    }
    deinit { NotificationCenter.default.removeObserver(self); client?.invalidate(); pairingClient?.invalidate() }
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
        guard !requestIDs.contains(id) else { return }
        guard requestIDs.count < 32 else { sendDirect(id, ["error":"Too many pending requests. Wait for the PC, then try again."]); return }
        requestIDs.insert(id)
        let body = request["body"] as? [String: Any] ?? [:]
        guard method == "GET" || method == "POST", path == "/local/export-image" || bytes.count <= 16384 else {
            fail(id, "This request is invalid or too large."); return
        }
        if path.hasPrefix("/native/") || path.hasPrefix("/local/") {
            guard method == "POST" else { fail(id, "This local request requires POST."); return }
            local(id, path: path, body: body); return
        }
        guard HostPolicy.allowed(path, method: method) else { fail(id, "This request is not allowed from the tablet."); return }
        guard foreground || HostPolicy.allowedInBackground(path, body: body) else { fail(id, "Controls are paused while DeskWing is in the background."); return }
        guard let client else { fail(id, "Pair this tablet with your Windows PC first."); return }
        let epoch = generation
        do {
            try client.send(path: path, method: method, body: body) { [weak self] result in
                guard let self else { return }
                guard epoch == self.generation || HostPolicy.allowedInBackground(path, body: body) else {
                    self.fail(id, "The connection changed before confirmation. The action may have run; it was not repeated."); return
                }
                switch result {
                case .success(let response):
                    if path == "/api/hold" {
                        if body["operation"] as? String == "release" { self.currentLease = nil }
                        else if let lease = response["lease"] as? String { self.currentLease = lease }
                    }
                    self.complete(id, response)
                case .failure(let error): self.fail(id, error.localizedDescription)
                }
            }
        } catch { fail(id, error.localizedDescription) }
    }
    private func local(_ id: Int, path: String, body: [String: Any]) {
        if path == "/native/connection" || path == "/native/diagnostics/reset" { complete(id, connection()); return }
        if path == "/native/chat", body["operation"] as? String == "hide" { provider?.dismiss(animated: true); complete(id, ["ok":true]); return }
        guard foreground else { fail(id, "Open DeskWing before using this tool."); return }
        do {
            switch path {
            case "/native/pair": try pair(id, body: body)
            case "/native/forget":
                try PairingStore.forget(); retireClient(); pc = nil; complete(id, connection())
            case "/native/transport":
                guard body["mode"] as? String == "wifi" else { throw DeskError.message("iOS connects to your PC over Wi-Fi/LAN. Android's USB tunnel is not supported on iOS.") }
                guard let current = pc, body["host"] as? String == current.host else { throw DeskError.message("Pair again to verify a different PC address.") }
                complete(id, connection())
            case "/native/scan": throw DeskError.message("Use manual pairing on iOS: enter the PC address, certificate code, and PIN.")
            case "/local/handwriting": try showHandwriting(id, body: body)
            case "/local/export-image": try exportImage(id, body: body)
            case "/native/chat": try showChat(id, body: body)
            default: throw DeskError.message("This local request is not supported on iOS.")
            }
        } catch { fail(id, error.localizedDescription) }
    }
    private func connection() -> [String: Any] {
        ["paired":pc != nil, "host":pc?.host ?? "", "mode":"wifi", "pairingId":pc?.pairingID ?? "",
         "port":pc?.port ?? 8743, "controlPort":pc?.controlPort ?? 8744, "fastConnected":false,
         "roundTripMs":-1, "diagnostics":[:], "platform":"ios"]
    }
    private func pair(_ id: Int, body: [String: Any]) throws {
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
                guard epoch == self.generation, self.foreground else { self.fail(id, "Pairing stopped when the app left the foreground. Generate a fresh PIN and try again."); return }
                do {
                    let response = try result.get()
                    guard let fingerprint = response["fingerprint"] as? String, let token = response["token"] as? String,
                          fingerprint == pairing.verifiedFingerprint, HostPolicy.pinMatches(pin, fingerprint) else {
                        throw DeskError.message("The pairing response did not match the verified PC certificate.")
                    }
                    let saved = PairedPC(host: host, port: port, controlPort: controlPort, fingerprint: fingerprint, token: token, pairingID: UUID().uuidString)
                    try PairingStore.save(saved); self.retireClient(); self.pc = saved; self.client = self.makeClient(saved)
                    self.complete(id, self.connection())
                } catch { self.fail(id, error.localizedDescription) }
            }
        } catch { pairingClient = nil; pairing.invalidate(); throw error }
    }
    private func retireClient() {
        generation += 1; releaseHeldKey()
        let old = client; old?.cancelPending(keepingReleases: true); client = nil
        DispatchQueue.main.asyncAfter(deadline: .now() + 2) { old?.invalidate() }
    }
    private func releaseHeldKey() {
        guard let lease = currentLease, let client else { return }; currentLease = nil
        try? client.send(path: "/api/hold", method: "POST", body: ["operation":"release", "lease":lease]) { _ in }
    }
    @objc private func suspend() {
        UIApplication.shared.isIdleTimerDisabled = false
        foreground = false; generation += 1; pairingClient?.invalidate(); pairingClient = nil
        releaseHeldKey(); client?.cancelPending(keepingReleases: true); handwriting?.cancel()
        web?.evaluateJavaScript("if(typeof releaseDeckHold==='function')releaseDeckHold();if(window.monitorSuspend)window.monitorSuspend();if(window.penSuspend)window.penSuspend();if(window.deckStreamClosed)window.deckStreamClosed();", completionHandler: nil)
    }
    @objc private func resume() {
        UIApplication.shared.isIdleTimerDisabled = true
        foreground = true
        web?.evaluateJavaScript("if(window.monitorResume)window.monitorResume();if(window.DeskPen&&window.DeskPen.resume)window.DeskPen.resume();if(typeof refresh==='function')refresh(true);", completionHandler: nil)
    }
    private func showHandwriting(_ id: Int, body: [String: Any]) throws {
        guard presentedViewController == nil else { throw DeskError.message("Finish the open sheet before using handwriting.") }
        let text = body["text"] as? String ?? ""
        guard text.utf16.count <= 8000 else { throw DeskError.message("Handwriting fields support up to 8,000 characters.") }
        let editor = PenInputController(text: text) { [weak self] text, cancelled in self?.complete(id, ["text":text,"cancelled":cancelled]) }
        handwriting = editor
        let navigation = UINavigationController(rootViewController: editor); navigation.modalPresentationStyle = .formSheet
        present(navigation, animated: true)
    }
    private func exportImage(_ id: Int, body: [String: Any]) throws {
        guard presentedViewController == nil else { throw DeskError.message("Finish the open sheet before exporting a drawing.") }
        let data = try PNGExport.decode(body["dataUrl"] as? String ?? "")
        let folder = FileManager.default.temporaryDirectory.appendingPathComponent("DeskWing-export-" + UUID().uuidString, isDirectory: true)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        let url = folder.appendingPathComponent(PNGExport.safeName(body["name"] as? String ?? ""))
        try data.write(to: url, options: [.atomic, .completeFileProtection])
        let sheet = UIActivityViewController(activityItems: [url], applicationActivities: nil)
        sheet.popoverPresentationController?.sourceView = view
        sheet.popoverPresentationController?.sourceRect = CGRect(x: view.bounds.midX, y: view.bounds.midY, width: 1, height: 1)
        sheet.completionWithItemsHandler = { [weak self] _, completed, _, error in
            try? FileManager.default.removeItem(at: folder)
            if let error { self?.fail(id, error.localizedDescription) } else { self?.complete(id, ["saved":completed,"cancelled":!completed]) }
        }
        present(sheet, animated: true)
    }
    private func showChat(_ id: Int, body: [String: Any]) throws {
        if body["operation"] as? String == "bounds" { complete(id, ["ok":true]); return }
        guard body["operation"] as? String == "show", presentedViewController == nil,
              let providerName = body["provider"] as? String, let channel = body["channel"] as? String else { throw DeskError.message("Close the open sheet before loading a provider chat.") }
        let pattern = providerName == "twitch" ? "^[a-z0-9_]{1,25}$" : providerName == "kick" ? "^[a-z0-9_-]{1,50}$" : "(?!)"
        guard channel.range(of: pattern, options: .regularExpression) != nil else { throw DeskError.message("Enter a valid Twitch or Kick channel name.") }
        let address = providerName == "twitch" ? "https://www.twitch.tv/popout/\(channel)/chat?popout=" : "https://kick.com/popout/\(channel)/chat"
        guard let url = URL(string: address) else { throw DeskError.message("Invalid provider address.") }
        let sheet = SFSafariViewController(url: url); provider = sheet; present(sheet, animated: true)
        complete(id, ["ok":true])
        let event: [String: Any] = ["session":body["session"] as? Int ?? 0, "state":"Chat opened in a separate provider viewer. It has no PC-control bridge."]
        if let data = try? JSONSerialization.data(withJSONObject: event), let json = String(data: data, encoding: .utf8) {
            web.evaluateJavaScript("if(window.monitorChatEvent)window.monitorChatEvent(\(json));", completionHandler: nil)
        }
    }
    private func fail(_ id: Int, _ message: String) { complete(id, ["error":String(message.prefix(1000))]) }
    private func complete(_ id: Int, _ result: [String: Any]) {
        guard requestIDs.remove(id) != nil else { return }; sendDirect(id, result)
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
    func webViewWebContentProcessDidTerminate(_ webView: WKWebView) {
        releaseHeldKey(); client?.cancelPending(keepingReleases: true); requestIDs.removeAll(); generation += 1
        webView.reload()
    }
}
