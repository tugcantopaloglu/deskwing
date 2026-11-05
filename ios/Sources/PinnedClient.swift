import Foundation
import Security

/// One private PC, one immutable certificate pin. Never follows redirects or retries an action in app code.
final class PinnedClient: NSObject, URLSessionDataDelegate {
    typealias Reply = (Result<[String: Any], Error>) -> Void
    let host: String
    let port: Int
    private let pin: String
    private let token: String
    private let waitsForConnectivity: Bool
    private let lock = NSLock()
    private var receivedPin = ""
    private var pending: [Int: Pending] = [:]
    private struct Pending {
        let task: URLSessionDataTask
        let path: String
        let release: Bool
        let reply: Reply
        var data = Data()
        var status = 0
        var bodyStarted = false
        var failure: Error?
    }
    private static let maximumReply = 6 * 1024 * 1024
    private lazy var session: URLSession = {
        let configuration = URLSessionConfiguration.ephemeral
        configuration.urlCache = nil; configuration.httpCookieStorage = nil; configuration.urlCredentialStorage = nil
        configuration.httpShouldSetCookies = false; configuration.requestCachePolicy = .reloadIgnoringLocalCacheData
        configuration.timeoutIntervalForRequest = 65; configuration.timeoutIntervalForResource = 70
        configuration.waitsForConnectivity = waitsForConnectivity; configuration.allowsCellularAccess = false
        configuration.httpMaximumConnectionsPerHost = 4; configuration.tlsMinimumSupportedProtocolVersion = .TLSv12
        let callbacks = OperationQueue(); callbacks.maxConcurrentOperationCount = 1
        return URLSession(configuration: configuration, delegate: self, delegateQueue: callbacks)
    }()
    init(host: String, port: Int, pin: String, token: String = "", waitsForConnectivity: Bool = false) {
        self.host = host; self.port = port; self.pin = pin; self.token = token; self.waitsForConnectivity = waitsForConnectivity
        super.init()
    }
    var verifiedFingerprint: String { lock.lock(); defer { lock.unlock() }; return receivedPin }

    @discardableResult
    func send(path: String, method: String, body: [String: Any], pairing: Bool = false, reply: @escaping Reply) throws -> URLSessionDataTask {
        guard HostPolicy.validHost(host), (1...65535).contains(port), HostPolicy.normalizedPin(pin) == pin,
              (pairing && path == "/api/pair" && method == "POST" && token.isEmpty) ||
                (!pairing && pin.count == 64 && !token.isEmpty && HostPolicy.allowed(path, method: method)) else {
            throw DeskError.message("This PC request is not allowed.")
        }
        var parts = URLComponents(); parts.scheme = "https"; parts.host = host; parts.port = port; parts.path = path
        if method == "GET", let revision = HostPolicy.inkRevision(path) {
            parts.path = "/api/ink"; parts.queryItems = [URLQueryItem(name: "revision", value: revision)]
        }
        guard let url = parts.url else { throw DeskError.message("Invalid PC address.") }
        var request = URLRequest(url: url); request.httpMethod = method
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        request.setValue("application/json", forHTTPHeaderField: "Accept")
        if !token.isEmpty { request.setValue("Bearer " + token, forHTTPHeaderField: "Authorization") }
        if method == "POST" {
            let data = try JSONSerialization.data(withJSONObject: body)
            guard data.count <= HostPolicy.maximumBodyBytes(path) else { throw DeskError.message("This PC request is too large.") }
            request.httpBody = data
        }
        if pairing { request.timeoutInterval = 12 }
        if path.hasSuffix("/frame") { request.timeoutInterval = 8 }
        let task = session.dataTask(with: request)
        lock.lock()
        guard pending.count < 16 else { lock.unlock(); throw DeskError.message("The PC is busy. Wait for the current action before trying again.") }
        pending[task.taskIdentifier] = Pending(task: task, path: path,
            release: HostPolicy.allowedInBackground(path, body: body), reply: reply)
        lock.unlock()
        task.resume()
        if !pairing && HostPolicy.needsDispatchDeadline(path, method: method, body: body) {
            DispatchQueue.global(qos: .userInitiated).asyncAfter(deadline: .now() + HostPolicy.dispatchDeadline(path)) { [weak self, weak task] in
                guard let self, let task else { return }
                self.lock.lock()
                guard var item = self.pending[task.taskIdentifier], !item.bodyStarted else { self.lock.unlock(); return }
                item.failure = DeskError.message("This control waited too long to send and was cancelled. If it reached the PC at the same time, it may have run; it was not repeated.")
                self.pending[task.taskIdentifier] = item; self.lock.unlock(); task.cancel()
            }
        }
        return task
    }
    func cancelPending(keepingReleases: Bool = false) {
        lock.lock(); let tasks = pending.values.filter { !(keepingReleases && $0.release) }.map(\.task); lock.unlock()
        tasks.forEach { $0.cancel() }
    }
    func invalidate() { session.invalidateAndCancel() }

    func urlSession(_ session: URLSession, didReceive challenge: URLAuthenticationChallenge,
                    completionHandler: @escaping (URLSession.AuthChallengeDisposition, URLCredential?) -> Void) {
        let space = challenge.protectionSpace
        guard space.authenticationMethod == NSURLAuthenticationMethodServerTrust,
              space.host == host, space.port == port, challenge.previousFailureCount == 0,
              let trust = space.serverTrust, let chain = SecTrustCopyCertificateChain(trust) as? [SecCertificate],
              let certificate = chain.first else { completionHandler(.cancelAuthenticationChallenge, nil); return }
        let der = SecCertificateCopyData(certificate) as Data
        let actual = HostPolicy.fingerprint(der)
        guard HostPolicy.pinMatches(pin, actual), CertificateDates.valid(der),
              SecTrustSetPolicies(trust, SecPolicyCreateSSL(true, nil)) == errSecSuccess,
              SecTrustSetAnchorCertificates(trust, [certificate] as CFArray) == errSecSuccess,
              SecTrustSetAnchorCertificatesOnly(trust, true) == errSecSuccess,
              SecTrustEvaluateWithError(trust, nil) else {
            completionHandler(.cancelAuthenticationChallenge, nil); return
        }
        lock.lock(); receivedPin = actual; lock.unlock()
        // The self-signed PC certificate is trusted only after its explicit pairing pin and validity match.
        completionHandler(.useCredential, URLCredential(trust: trust))
    }
    func urlSession(_ session: URLSession, task: URLSessionTask, willPerformHTTPRedirection response: HTTPURLResponse,
                    newRequest request: URLRequest, completionHandler: @escaping (URLRequest?) -> Void) {
        completionHandler(nil)
    }
    func urlSession(_ session: URLSession, task: URLSessionTask, didSendBodyData bytesSent: Int64,
                    totalBytesSent: Int64, totalBytesExpectedToSend: Int64) {
        guard totalBytesSent > 0 else { return }
        lock.lock(); if var item = pending[task.taskIdentifier] { item.bodyStarted = true; pending[task.taskIdentifier] = item }; lock.unlock()
    }
    func urlSession(_ session: URLSession, dataTask: URLSessionDataTask, didReceive response: URLResponse,
                    completionHandler: @escaping (URLSession.ResponseDisposition) -> Void) {
        lock.lock()
        guard var item = pending[dataTask.taskIdentifier], let http = response as? HTTPURLResponse,
              response.expectedContentLength <= Int64(Self.maximumReply), http.url?.host == host else {
            lock.unlock(); completionHandler(.cancel); return
        }
        item.status = http.statusCode; pending[dataTask.taskIdentifier] = item
        lock.unlock(); completionHandler(.allow)
    }
    func urlSession(_ session: URLSession, dataTask: URLSessionDataTask, didReceive data: Data) {
        lock.lock()
        guard var item = pending[dataTask.taskIdentifier] else { lock.unlock(); return }
        if data.count > Self.maximumReply - item.data.count {
            item.failure = DeskError.message("The PC response is too large. Reduce the board or library and retry.")
            pending[dataTask.taskIdentifier] = item; lock.unlock(); dataTask.cancel(); return
        }
        item.data.append(data); pending[dataTask.taskIdentifier] = item; lock.unlock()
    }
    func urlSession(_ session: URLSession, task: URLSessionTask, didCompleteWithError error: Error?) {
        lock.lock(); let item = pending.removeValue(forKey: task.taskIdentifier); lock.unlock()
        guard let item else { return }
        let result: Result<[String: Any], Error>
        if let failure = item.failure ?? error { result = .failure(Self.friendly(failure)) }
        else if let json = try? JSONSerialization.jsonObject(with: item.data) as? [String: Any] {
            if let message = json["error"] as? String { result = .failure(DeskError.message(String(message.prefix(1000)))) }
            else if (200...299).contains(item.status) { result = .success(json) }
            else { result = .failure(DeskError.message("The PC refused this request (\(item.status)). Check the connection and pairing.")) }
        } else { result = .failure(DeskError.message("The PC returned an unreadable response. Reopen the companion and retry.")) }
        DispatchQueue.main.async { item.reply(result) }
    }
    private static func friendly(_ error: Error) -> Error {
        if let error = error as? URLError {
            switch error.code {
            case .cancelled: return DeskError.message("Request stopped. An action already sent may have run; it was not repeated.")
            case .serverCertificateUntrusted, .serverCertificateHasBadDate, .serverCertificateHasUnknownRoot, .secureConnectionFailed:
                return DeskError.message("Cannot verify the PC certificate. Check the certificate code on the PC and pair again if it changed.")
            case .timedOut, .cannotConnectToHost, .networkConnectionLost, .notConnectedToInternet:
                return DeskError.message("Cannot reach the PC. Check local-network permission in Settings, the same Wi-Fi/LAN, and Windows Firewall. An action already sent may have run.")
            default: return DeskError.message("The local PC connection failed. Check Wi-Fi and try again.")
            }
        }
        return error
    }
}
