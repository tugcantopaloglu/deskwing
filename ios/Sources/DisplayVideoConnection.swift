import Foundation
import Security

/// Video owns a separate pinned transport. Its incremental decoder never enters the control bridge
/// and never buffers a complete response, retries a connection, or writes captured pixels to disk.
final class DisplayVideoConnection: NSObject, URLSessionDataDelegate {
    private let pc: PairedPC
    private let lease: String
    private var parser: DisplayVideoParser
    private let receive: (DisplayVideoUnit) -> Void
    private let finish: (String) -> Void
    private let lock = NSLock()
    private var stopped = false
    private var task: URLSessionDataTask?
    private lazy var session: URLSession = {
        let configuration = URLSessionConfiguration.ephemeral
        configuration.urlCache = nil; configuration.httpCookieStorage = nil; configuration.urlCredentialStorage = nil
        configuration.httpShouldSetCookies = false; configuration.requestCachePolicy = .reloadIgnoringLocalCacheData
        configuration.timeoutIntervalForRequest = 5; configuration.timeoutIntervalForResource = 12 * 60 * 60
        configuration.waitsForConnectivity = false; configuration.allowsCellularAccess = false
        configuration.httpMaximumConnectionsPerHost = 1; configuration.tlsMinimumSupportedProtocolVersion = .TLSv12
        let callbacks = OperationQueue(); callbacks.maxConcurrentOperationCount = 1; callbacks.qualityOfService = .userInitiated
        return URLSession(configuration: configuration, delegate: self, delegateQueue: callbacks)
    }()

    init(pc: PairedPC, lease: String, format: DisplayVideoFormat,
         receive: @escaping (DisplayVideoUnit) -> Void, finish: @escaping (String) -> Void) {
        self.pc = pc; self.lease = lease; self.parser = DisplayVideoParser(expected: format)
        self.receive = receive; self.finish = finish
        super.init()
    }
    func start() throws {
        guard HostPolicy.validHost(pc.host), (1...65535).contains(pc.port), pc.fingerprint.count == 64,
              HostPolicy.normalizedPin(pc.fingerprint) == pc.fingerprint, !pc.token.isEmpty,
              lease.utf8.count == 48, lease.utf8.allSatisfy({ (48...57).contains($0) || (65...70).contains($0) }) else {
            throw DeskError.message("Pair the tablet with the PC before starting its display.")
        }
        var address = URLComponents(); address.scheme = "https"; address.host = pc.host; address.port = pc.port
        address.path = "/api/computer/display/stream"
        guard let url = address.url else { throw DeskError.message("The PC address is invalid.") }
        var request = URLRequest(url: url); request.httpMethod = "POST"; request.timeoutInterval = 5
        request.setValue("Bearer " + pc.token, forHTTPHeaderField: "Authorization")
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        request.setValue("application/vnd.deskwing.h264", forHTTPHeaderField: "Accept")
        request.httpBody = try JSONSerialization.data(withJSONObject: ["lease":lease])
        lock.lock()
        guard !stopped, task == nil else { lock.unlock(); throw DeskError.message("This display session has already been used.") }
        let next = session.dataTask(with: request); task = next
        lock.unlock(); next.resume()
    }
    func stop() {
        lock.lock(); let shouldStop = !stopped; stopped = true; let running = task; task = nil; lock.unlock()
        if shouldStop { running?.cancel(); session.invalidateAndCancel() }
    }
    private var active: Bool { lock.lock(); defer { lock.unlock() }; return !stopped }
    private func end(_ message: String) {
        guard active else { return }
        stop(); DispatchQueue.main.async { [finish] in finish(message) }
    }
    func urlSession(_ session: URLSession, didReceive challenge: URLAuthenticationChallenge,
                    completionHandler: @escaping (URLSession.AuthChallengeDisposition, URLCredential?) -> Void) {
        let space = challenge.protectionSpace
        guard active, space.authenticationMethod == NSURLAuthenticationMethodServerTrust,
              space.host == pc.host, space.port == pc.port, challenge.previousFailureCount == 0,
              let trust = space.serverTrust, let chain = SecTrustCopyCertificateChain(trust) as? [SecCertificate],
              let certificate = chain.first else { completionHandler(.cancelAuthenticationChallenge, nil); return }
        let der = SecCertificateCopyData(certificate) as Data
        guard HostPolicy.pinMatches(pc.fingerprint, HostPolicy.fingerprint(der)), CertificateDates.valid(der),
              SecTrustSetPolicies(trust, SecPolicyCreateSSL(true, nil)) == errSecSuccess,
              SecTrustSetAnchorCertificates(trust, [certificate] as CFArray) == errSecSuccess,
              SecTrustSetAnchorCertificatesOnly(trust, true) == errSecSuccess,
              SecTrustEvaluateWithError(trust, nil) else { completionHandler(.cancelAuthenticationChallenge, nil); return }
        completionHandler(.useCredential, URLCredential(trust: trust))
    }
    func urlSession(_ session: URLSession, task: URLSessionTask, willPerformHTTPRedirection response: HTTPURLResponse,
                    newRequest request: URLRequest, completionHandler: @escaping (URLRequest?) -> Void) {
        completionHandler(nil)
    }
    func urlSession(_ session: URLSession, dataTask: URLSessionDataTask, didReceive response: URLResponse,
                    completionHandler: @escaping (URLSession.ResponseDisposition) -> Void) {
        guard active, let http = response as? HTTPURLResponse, http.statusCode == 200,
              http.url?.scheme == "https", http.url?.host == pc.host, http.url?.port == pc.port,
              http.url?.path == "/api/computer/display/stream", http.url?.query == nil,
              response.mimeType?.lowercased() == "application/vnd.deskwing.h264" else {
            completionHandler(.cancel); end("The PC refused the display stream. Check that its independent display is shared, then start again."); return
        }
        completionHandler(.allow)
    }
    func urlSession(_ session: URLSession, dataTask: URLSessionDataTask, didReceive data: Data) {
        guard active else { return }
        do {
            try parser.append(data) { [weak self] unit in guard let self, self.active else { return }; self.receive(unit) }
            if parser.ended { end("The PC ended this display session.") }
        } catch { end(error.localizedDescription) }
    }
    func urlSession(_ session: URLSession, task: URLSessionTask, didCompleteWithError error: Error?) {
        guard active else { return }
        if error != nil { end("The display connection stopped. Check Wi-Fi and the PC, then start it again."); return }
        do { try parser.finish(); end("The PC ended this display session.") }
        catch { end(error.localizedDescription) }
    }
}
