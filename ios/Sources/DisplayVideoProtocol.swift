import Foundation
import CoreFoundation

struct DisplayVideoFormat: Equatable {
    let width: Int
    let height: Int
    let refreshRate: Int
}

struct DisplayVideoUnit {
    let bytes: Data
    let keyframe: Bool
    let configuration: Bool
    let presentationMicroseconds: UInt64
}

/// Incremental framing only. A connection owns one parser and cannot replace its negotiated format.
struct DisplayVideoParser {
    static let maximumAccessUnit = 2 * 1024 * 1024
    let expected: DisplayVideoFormat
    private var buffer = Data()
    private(set) var acceptedHeader = false
    private(set) var ended = false
    private var lastPresentation: UInt64?
    private var lastHeartbeat: UInt64?

    init(expected: DisplayVideoFormat) { self.expected = expected }

    mutating func append(_ bytes: Data, heartbeat: (UInt64) -> Void = { _ in }, consume: (DisplayVideoUnit) throws -> Void) throws {
        guard !ended else { throw DeskError.message("The display stream already ended.") }
        // URLSession may combine multiple frames in one callback. Consume bounded pieces rather
        // than retaining an arbitrarily large callback while waiting for a claimed payload length.
        var offset = 0
        while offset < bytes.count {
            let count = min(64 * 1024, bytes.count - offset)
            guard buffer.count <= Self.maximumAccessUnit + 16 else { throw DeskError.message("The display packet is too large.") }
            let first = bytes.index(bytes.startIndex, offsetBy: offset), last = bytes.index(first, offsetBy: count)
            buffer.append(bytes.subdata(in: first..<last)); offset += count
            try drain(heartbeat: heartbeat, consume: consume)
            if ended && offset < bytes.count { throw DeskError.message("Unexpected data after the display stream ended.") }
        }
    }
    private mutating func drain(heartbeat: (UInt64) -> Void, consume: (DisplayVideoUnit) throws -> Void) throws {
        if !acceptedHeader {
            guard buffer.count >= 32 else { return }
            guard Array(buffer.prefix(4)) == Array("DWV1".utf8), number(4, 2) == 1, number(6, 2) == 32,
                  number(8, 4) == UInt64(expected.width), number(12, 4) == UInt64(expected.height),
                  number(16, 4) == UInt64(expected.refreshRate), number(20, 4) == 1,
                  number(24, 4) == UInt64(Self.maximumAccessUnit), number(28, 4) == 0 else {
                throw DeskError.message("The PC returned an unsupported display stream.")
            }
            buffer.removeFirst(32); acceptedHeader = true
        }
        while buffer.count >= 16 {
            let length = Int(number(0, 4)), flags = number(4, 4), presentation = number(8, 8)
            guard length <= Self.maximumAccessUnit, flags & ~UInt64(15) == 0 else { throw DeskError.message("The display frame exceeds its limits.") }
            if flags & 8 != 0 {
                guard flags == 8, length == 0, presentation <= UInt64(Int64.max),
                      lastHeartbeat.map({ presentation > $0 }) ?? true else { throw DeskError.message("The display heartbeat is invalid.") }
                lastHeartbeat = presentation; buffer.removeFirst(16); heartbeat(presentation); continue
            }
            if flags & 4 != 0 {
                guard flags == 4, length == 0, buffer.count == 16 else { throw DeskError.message("The display end packet is invalid.") }
                buffer.removeAll(keepingCapacity: false); ended = true; return
            }
            guard length >= 4 else { throw DeskError.message("The display frame is empty.") }
            guard presentation <= UInt64(Int64.max) else { throw DeskError.message("The PC returned an invalid display timestamp.") }
            guard buffer.count >= 16 + length else { return }
            let configuration = flags & 2 != 0
            if !configuration {
                guard lastPresentation.map({ presentation > $0 }) ?? true else {
                    throw DeskError.message("The PC returned an out-of-order display frame.")
                }
                lastPresentation = presentation
            }
            let first = buffer.index(buffer.startIndex, offsetBy: 16), last = buffer.index(first, offsetBy: length)
            let unit = DisplayVideoUnit(bytes: buffer.subdata(in: first..<last), keyframe: flags & 1 != 0,
                                        configuration: configuration, presentationMicroseconds: presentation)
            buffer.removeFirst(16 + length)
            try consume(unit)
        }
    }
    private func number(_ offset: Int, _ length: Int) -> UInt64 {
        buffer[buffer.index(buffer.startIndex, offsetBy: offset)..<buffer.index(buffer.startIndex, offsetBy: offset + length)]
            .reduce(UInt64(0)) { ($0 << 8) | UInt64($1) }
    }
    func finish() throws {
        guard acceptedHeader, ended, buffer.isEmpty else { throw DeskError.message("The display connection ended unexpectedly. Start it again when the PC is ready.") }
    }
}

enum DisplayVideoPolicy {
    static func session(_ value: Any?) throws -> String {
        guard let text = value as? String, text.count == 36, UUID(uuidString: text) != nil else {
            throw DeskError.message("Start a fresh display session.")
        }
        return text.lowercased()
    }
    static func refresh(_ value: Any?, maximum: Int, current: Int? = nil) throws -> Int {
        guard let mode = value as? String, mode == "60" || mode == "match" else {
            throw DeskError.message("Choose 60 Hz or Match tablet refresh rate.")
        }
        guard maximum >= 60 else { throw DeskError.message("This display does not report a 60 Hz mode.") }
        let measured = current.map { min(maximum, $0) } ?? maximum
        guard mode != "match" || measured >= 60 else { throw DeskError.message("The tablet is currently refreshing below 60 Hz. Check power and display settings, then try again.") }
        return mode == "match" ? min(measured, 240) : 60
    }
    static func begin(_ body: [String: Any], requestedRefreshRate: Int) throws -> (String, DisplayVideoFormat, Int) {
        guard let lease = body["lease"] as? String, lease.utf8.count == 48,
              lease.utf8.allSatisfy({ (48...57).contains($0) || (65...70).contains($0) }),
              body["codec"] as? String == "h264", let width = integer(body["width"]), let height = integer(body["height"]),
              [(1280, 800), (1600, 900), (1920, 1080)].contains(where: { $0.0 == width && $0.1 == height }),
              let refresh = integer(body["refreshRate"]), refresh == requestedRefreshRate, (60...240).contains(refresh),
              let source = integer(body["sourceRefreshRate"]), (refresh...240).contains(source) else {
            throw DeskError.message("The PC returned an invalid display session.")
        }
        return (lease, DisplayVideoFormat(width: width, height: height, refreshRate: refresh), source)
    }
    private static func integer(_ value: Any?) -> Int? {
        guard let number = value as? NSNumber, CFGetTypeID(number) != CFBooleanGetTypeID(), number.doubleValue.isFinite,
              number.doubleValue >= 0, number.doubleValue <= 10000,
              number.doubleValue == Double(number.intValue) else { return nil }
        return number.intValue
    }
}

/// Start-code removal happens before Core Media receives data. Every NAL remains bounded by the
/// authenticated packet limit; no arbitrary codec or decoder configuration is accepted.
enum DisplayH264 {
    // Frame-rate accounting uses actual VCL NALs, not transport configuration flags. This bounded
    // allocation-free check is not bitstream validation; nals() and Core Media still validate decoding.
    static func containsPicture(_ data: Data) -> Bool {
        guard data.count >= 4, data.count <= DisplayVideoParser.maximumAccessUnit else { return false }
        return data.withUnsafeBytes { raw in
            let bytes = raw.bindMemory(to: UInt8.self)
            var index = 0
            while index + 3 < bytes.count {
                if bytes[index] == 0, bytes[index + 1] == 0, bytes[index + 2] == 1 {
                    let header = bytes[index + 3]
                    if header & 0x80 == 0, (1...5).contains(header & 0x1F) { return true }
                    index += 4
                } else { index += 1 }
            }
            return false
        }
    }
    static func nals(_ data: Data) throws -> [Data] {
        guard data.count >= 4, data.count <= DisplayVideoParser.maximumAccessUnit else { throw DeskError.message("Invalid H.264 frame size.") }
        let bytes = [UInt8](data)
        var starts: [(Int, Int)] = [], index = 0
        while index + 3 <= bytes.count {
            if bytes[index] == 0, bytes[index + 1] == 0 {
                if bytes[index + 2] == 1 { starts.append((index, index + 3)); index += 3; continue }
                if index + 4 <= bytes.count, bytes[index + 2] == 0, bytes[index + 3] == 1 { starts.append((index, index + 4)); index += 4; continue }
            }
            index += 1
        }
        guard starts.first?.0 == 0, starts.count <= 128 else { throw DeskError.message("Unsupported H.264 packet framing.") }
        return try starts.enumerated().map { position, start in
            var end = position + 1 < starts.count ? starts[position + 1].0 : bytes.count
            while end > start.1 && bytes[end - 1] == 0 { end -= 1 }
            guard end > start.1, bytes[start.1] & 0x80 == 0, (1...12).contains(bytes[start.1] & 0x1F) else {
                throw DeskError.message("Invalid H.264 NAL unit.")
            }
            return Data(bytes[start.1..<end])
        }
    }
}
