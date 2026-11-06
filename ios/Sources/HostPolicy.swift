import Foundation
import CryptoKit
import CoreFoundation

enum DeskError: LocalizedError {
    case message(String)
    var errorDescription: String? { if case let .message(value) = self { return value }; return nil }
}

/// JavaScript restarts its numeric request counter after a renderer reload. UUID tickets keep old completions isolated.
struct BridgeRequests {
    private var active: [Int: UUID] = [:]
    var count: Int { active.count }
    func contains(_ id: Int) -> Bool { active[id] != nil }
    mutating func begin(_ id: Int) -> UUID? {
        guard active[id] == nil else { return nil }
        let ticket = UUID(); active[id] = ticket; return ticket
    }
    mutating func finish(_ id: Int, ticket: UUID) -> Bool {
        guard active[id] == ticket else { return false }
        active.removeValue(forKey: id); return true
    }
    mutating func reset() { active.removeAll() }
}

/// A preview belongs to one pen lease. An old response cannot clear a newer lease's in-flight slot.
struct PenPreviewRequests {
    private var lease: String?
    private var pending: UUID?
    mutating func activate(_ lease: String) { self.lease = lease; pending = nil }
    mutating func begin(_ lease: String) throws -> UUID {
        guard self.lease == lease else { throw DeskError.message("Start pen input before requesting a PC preview.") }
        guard pending == nil else { throw DeskError.message("A PC preview is already in progress.") }
        let ticket = UUID(); pending = ticket; return ticket
    }
    mutating func finish(_ ticket: UUID) -> Bool {
        guard pending == ticket else { return false }
        pending = nil; return true
    }
    mutating func reset() { lease = nil; pending = nil }
}

/// Binds notebook, PDF and legacy scratchpad operations to their original public pairing context.
/// It never stores or exposes the PC's authentication token or certificate pin.
struct NotebookPCRequestScope {
    let pairingID: String?

    init(path: String, expectedPairingID: Any?, currentPairingID: String?) throws {
        guard HostPolicy.notebookNeedsContext(path) else { pairingID = nil; return }
        guard let expected = expectedPairingID as? String, Self.validID(expected),
              let current = currentPairingID, Self.validID(current), expected == current else {
            throw DeskError.message("The notebook's connected PC changed. Reopen its library before continuing.")
        }
        pairingID = expected
    }
    private static func validID(_ value: String) -> Bool {
        guard value.utf8.count == 36, let range = value.range(of: #"^[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}$"#, options: .regularExpression) else { return false }
        return range == (value.startIndex..<value.endIndex)
    }
    func requireCurrent(_ currentPairingID: String?) throws {
        guard let expected = pairingID else { return }
        guard currentPairingID == expected else {
            throw DeskError.message("The notebook's connected PC changed. Reopen its library before continuing.")
        }
    }
    func perform<T>(currentPairingID: String?, _ operation: () throws -> T) throws -> T {
        try requireCurrent(currentPairingID)
        return try operation()
    }
}

enum HostPolicy {
    static let getPaths: Set<String> = [
        "/api/computer/media", "/api/computer", "/api/computer/display", "/api/computer/display/input", "/api/computer/display/stream/status", "/api/ping", "/api/mixer",
        "/api/soundpad/recent", "/api/profiles", "/api/soundpad/favourites", "/api/board", "/api/state",
        "/api/execution", "/api/apps", "/api/soundpad/library", "/api/ink", "/api/pen"
    ]
    static let postPaths: Set<String> = [
        "/api/computer/follow", "/api/computer/media/action", "/api/computer/action", "/api/computer/preset",
        "/api/computer/display/frame", "/api/monitor/frame", "/api/ping", "/api/profile-lock",
        "/api/soundpad/recent/clear", "/api/profile", "/api/soundpad/favourites", "/api/cancel", "/api/hold",
        "/api/action", "/api/audio", "/api/mute", "/api/apps", "/api/layout", "/api/soundpad/play", "/api/soundpad/library", "/api/ink", "/api/pen", "/api/pen/frame"
    ]
    static func validHost(_ host: String) -> Bool {
        let parts = host.split(separator: ".", omittingEmptySubsequences: false)
        guard parts.count == 4 else { return false }
        var bytes: [Int] = []
        for part in parts {
            guard !part.isEmpty, part.count <= 3, part.utf8.allSatisfy({ $0 >= 48 && $0 <= 57 }),
                  (part.count == 1 || part.first != "0"), let number = Int(part), number <= 255 else { return false }
            bytes.append(number)
        }
        return bytes[0] == 10 || (bytes[0] == 192 && bytes[1] == 168) ||
            (bytes[0] == 172 && (16...31).contains(bytes[1])) ||
            (bytes[0] == 169 && bytes[1] == 254) || (bytes[0] == 100 && (64...127).contains(bytes[1]))
    }
    static func normalizedPin(_ value: String) -> String? {
        let pin = value.filter { !$0.isWhitespace && !"-:".contains($0) }.uppercased()
        guard [24, 64].contains(pin.count), pin.utf8.allSatisfy({ (48...57).contains($0) || (65...70).contains($0) }) else { return nil }
        return pin
    }
    static func pinMatches(_ expected: String, _ actual: String) -> Bool {
        guard [24, 64].contains(expected.count), actual.count == 64 else { return false }
        let left = Array(expected.utf8), right = Array(actual.utf8.prefix(left.count))
        return zip(left, right).reduce(UInt8(0)) { $0 | ($1.0 ^ $1.1) } == 0
    }
    static func allowed(_ path: String, method: String) -> Bool {
        (method == "GET" && (getPaths.contains(path) || inkRevision(path) != nil)) ||
            (method == "POST" && postPaths.contains(path)) || notebookAllowed(path, method: method)
    }
    private enum NotebookRoute: Equatable { case none, catalog, page, history, restore, revisionPage }
    private static func notebookRoute(_ path: String) -> NotebookRoute {
        guard path.utf8.count <= 256 else { return .none }
        if path == "/api/notebooks" { return .catalog }
        func isPage(_ value: String) -> Bool {
            guard let range = value.range(of: #"^/api/notebooks/[0-9A-Fa-f]{32}/pages/[0-9A-Fa-f]{32}$"#, options: .regularExpression) else { return false }
            return range == (value.startIndex..<value.endIndex)
        }
        let parts = path.split(separator: "?", omittingEmptySubsequences: false)
        if parts.count > 1 {
            guard parts.count == 2, isPage(String(parts[0])), parts[1].hasPrefix("revision=") else { return .none }
            let revision = parts[1].dropFirst("revision=".count)
            guard revision.utf8.count == 64, revision.utf8.allSatisfy({ (48...57).contains($0) || (65...70).contains($0) || (97...102).contains($0) }) else { return .none }
            return .revisionPage
        }
        if isPage(path) { return .page }
        if path.hasSuffix("/history/restore"), isPage(String(path.dropLast("/history/restore".count))) { return .restore }
        if path.hasSuffix("/history"), isPage(String(path.dropLast("/history".count))) { return .history }
        return .none
    }
    static func notebookAllowed(_ path: String, method: String) -> Bool {
        let route = notebookRoute(path)
        return method == "GET" && [.catalog,.page,.history,.revisionPage].contains(route) ||
            method == "POST" && [.catalog,.page,.restore].contains(route)
    }
    static func notebookNeedsContext(_ path: String) -> Bool {
        path == "/api/ink" || inkRevision(path) != nil ||
            path == "/api/notebooks" || path.hasPrefix("/api/notebooks/") || path.hasPrefix("/api/notebooks?")
    }
    static func notebookRevision(_ path: String) -> String? {
        guard notebookRoute(path) == .revisionPage, let question = path.firstIndex(of: "?") else { return nil }
        return String(path[path.index(after: question)...].dropFirst("revision=".count))
    }
    static func inkRevision(_ path: String) -> String? {
        let prefix = "/api/ink?revision="
        guard path.hasPrefix(prefix) else { return nil }
        let revision = String(path.dropFirst(prefix.count))
        guard revision.utf8.count == 64, revision.utf8.allSatisfy({ (48...57).contains($0) || (65...70).contains($0) || (97...102).contains($0) }) else { return nil }
        return revision
    }
    static func allowedInBackground(_ path: String, body: [String: Any]) -> Bool {
        path == "/api/cancel" || (path == "/api/hold" && body["operation"] as? String == "release") ||
            (path == "/api/pen" && ["end", "cancel"].contains(body["operation"] as? String ?? ""))
    }
    static func maximumBodyBytes(_ path: String) -> Int {
        if path == "/api/pen/frame" { return 1024 }
        if [.catalog,.page].contains(notebookRoute(path)) { return 4 * 1024 * 1024 }
        return path == "/api/ink" ? 4 * 1024 * 1024 : 16384
    }
    static func maximumBridgeBytes(_ path: String, method: String = "POST") -> Int {
        if path == "/local/export-image" { return 12 * 1024 * 1024 }
        if path == "/api/ink", method == "POST" { return maximumBodyBytes(path) + 16384 }
        if method == "POST", [.catalog,.page].contains(notebookRoute(path)) { return maximumBodyBytes(path) + 16384 }
        return 16384
    }
    static func maximumResponseBytes(_ path: String) -> Int {
        notebookRoute(path) == .none ? 6 * 1024 * 1024 : 8 * 1024 * 1024
    }
    static func requestedFullscreen(_ body: [String: Any], foreground: Bool, presentingTool: Bool) throws -> Bool {
        guard body.count == 1, let value = body["enabled"] as? NSNumber,
              CFGetTypeID(value) == CFBooleanGetTypeID() else {
            throw DeskError.message("Full screen requires an enabled boolean.")
        }
        let enabled = value.boolValue
        guard !enabled || (foreground && !presentingTool) else {
            throw DeskError.message("Return to DeskWing and close the open sheet before entering full screen.")
        }
        return enabled
    }
    static func penFrameLease(_ body: [String: Any]) throws -> String {
        guard Set(body.keys).isSubset(of: ["lease", "width"]), let lease = body["lease"] as? String,
              !lease.isEmpty, lease.utf8.count <= 128 else {
            throw DeskError.message("A PC preview requires the active pen lease.")
        }
        if let width = body["width"] {
            guard let number = width as? NSNumber, CFGetTypeID(number) != CFBooleanGetTypeID(),
                  number.doubleValue.isFinite, (160.0...1600.0).contains(number.doubleValue),
                  number.doubleValue == Double(number.intValue) else {
                throw DeskError.message("PC preview width must be a whole number from 160 to 1,600.")
            }
        }
        return lease
    }
    static func dispatchDeadline(_ path: String) -> TimeInterval { path == "/api/pen" ? 0.5 : 2 }
    static func needsDispatchDeadline(_ path: String, method: String, body: [String: Any]) -> Bool {
        method == "POST" && !allowedInBackground(path, body: body) &&
            !["/api/ping", "/api/monitor/frame", "/api/computer/display/frame", "/api/pen/frame", "/api/soundpad/library"].contains(path)
    }
    static func trustedPage(_ url: URL?) -> Bool {
        guard let url else { return false }
        return url.scheme == "deskwing" && url.host == "app" && url.port == nil && url.user == nil &&
            url.password == nil && url.path == "/index.html" && url.query == nil
    }
    static func fingerprint(_ der: Data) -> String { SHA256.hash(data: der).map { String(format: "%02X", $0) }.joined() }
}

/// Extract only X.509 validity, with strict DER lengths. Trust/signature checks still use Security.framework.
enum CertificateDates {
    static func valid(_ certificate: Data, now: Date = Date()) -> Bool {
        do {
            var certificateReader = DERReader(certificate)
            var outer = DERReader(try certificateReader.take(tag: 0x30))
            guard certificateReader.atEnd else { return false }
            var tbs = DERReader(try outer.take(tag: 0x30))
            if tbs.peek == 0xA0 { _ = try tbs.take(tag: 0xA0) }
            _ = try tbs.take(tag: 0x02) // Serial number.
            _ = try tbs.take(tag: 0x30) // Signature algorithm.
            _ = try tbs.take(tag: 0x30) // Issuer.
            var validity = DERReader(try tbs.take(tag: 0x30))
            let start = try validity.time(), end = try validity.time()
            return validity.atEnd && start <= now && now <= end
        } catch { return false }
    }
    private struct DERReader {
        let bytes: [UInt8]
        var index = 0
        init(_ data: Data) { bytes = Array(data) }
        var atEnd: Bool { index == bytes.count }
        var peek: UInt8? { index < bytes.count ? bytes[index] : nil }
        mutating func take(tag: UInt8) throws -> Data {
            guard index + 2 <= bytes.count, bytes[index] == tag else { throw DeskError.message("Invalid certificate.") }
            index += 1
            var length = Int(bytes[index]); index += 1
            if length & 0x80 != 0 {
                let count = length & 0x7F
                guard count > 0, count <= 4, index + count <= bytes.count, bytes[index] != 0 else { throw DeskError.message("Invalid certificate length.") }
                length = 0
                for _ in 0..<count { length = length * 256 + Int(bytes[index]); index += 1 }
                guard length >= 128 else { throw DeskError.message("Non-DER certificate.") }
            }
            guard length >= 0, length <= bytes.count - index else { throw DeskError.message("Truncated certificate.") }
            defer { index += length }
            return Data(bytes[index..<(index + length)])
        }
        mutating func time() throws -> Date {
            guard let tag = peek, tag == 0x17 || tag == 0x18 else { throw DeskError.message("Unsupported certificate date.") }
            let data = try take(tag: tag)
            guard let text = String(data: data, encoding: .ascii), text.count == (tag == 0x17 ? 13 : 15),
                  text.last == "Z", text.dropLast().utf8.allSatisfy({ (48...57).contains($0) }) else { throw DeskError.message("Invalid certificate date.") }
            let expanded: String
            if tag == 0x17 { expanded = (Int(text.prefix(2))! >= 50 ? "19" : "20") + text } else { expanded = text }
            let formatter = DateFormatter(); formatter.locale = Locale(identifier: "en_US_POSIX")
            formatter.timeZone = TimeZone(secondsFromGMT: 0); formatter.calendar = Calendar(identifier: .gregorian)
            formatter.dateFormat = "yyyyMMddHHmmss'Z'"; formatter.isLenient = false
            guard let result = formatter.date(from: expanded), formatter.string(from: result) == expanded else { throw DeskError.message("Invalid certificate date.") }
            return result
        }
    }
}
