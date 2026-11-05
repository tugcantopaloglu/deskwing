import XCTest
import CoreMedia
import UIKit
import WebKit
@testable import DeskWing

final class DisplayVideoTests: XCTestCase {
    @MainActor
    func testNativeViewerKeepsTheWebControlPageVisibleToItsLifecycle() async throws {
        let priorWindow = UIApplication.shared.windows.first(where: \.isKeyWindow)
        let window = UIWindow(frame: UIScreen.main.bounds), host = UIViewController()
        let web = WKWebView(frame: window.bounds)
        window.rootViewController = host; host.view.addSubview(web); window.makeKeyAndVisible()
        let viewer = DisplayVideoController(format: DisplayVideoFormat(width: 1280, height: 800, refreshRate: 60), sourceRefresh: 60) { _ in }
        defer { viewer.stop(); viewer.dismiss(animated: false); window.isHidden = true; priorWindow?.makeKeyAndVisible() }
        let loaded = expectation(description: "Isolated local web page loaded")
        let navigation = DisplayTestNavigation { loaded.fulfill() }
        web.navigationDelegate = navigation
        web.loadHTMLString("<!doctype html><html><body>Isolated display lifecycle fixture</body></html>", baseURL: nil)
        await fulfillment(of: [loaded], timeout: 15)
        let before = try await web.evaluateJavaScript("document.visibilityState") as? String
        XCTAssertEqual(before, "visible")
        let presented = expectation(description: "Native video viewer presented")
        host.present(viewer, animated: false) { presented.fulfill() }
        await fulfillment(of: [presented], timeout: 10)
        XCTAssertNotNil(web.window, "The control page must stay attached while the native viewer covers it.")
        let after = try await web.evaluateJavaScript("document.visibilityState") as? String
        XCTAssertEqual(after, "visible")
        withExtendedLifetime(navigation) {}
    }
    func testActualWindowsEncoderConfigurationsAcceptVisibleMacroblockCropping() throws {
        // Captured from the Windows Media Foundation encoder using synthetic frames only, 2026-11-05.
        // 900/1080 visible rows use 912/1088 coded rows; no PC content is part of these codec headers.
        let pps = try XCTUnwrap(Data(base64Encoded: "aM48gA=="))
        let fixtures: [(Int, Int, String)] = [
            (1280, 800, "Z0LAKJWgFAGWwEQAAAMABAAAAwHgNoIhGoA="),
            (1600, 900, "Z0LAKpWgGQHP58BEAAADAAQAAAMB4DaCIRqA"),
            (1920, 1080, "Z0LAKpWgHgCJ+XARAAADAAEAAAMAeA2giEag")
        ]
        for (width, height, encoded) in fixtures {
            let sps = try XCTUnwrap(Data(base64Encoded: encoded))
            let expected = DisplayVideoFormat(width: width, height: height, refreshRate: 60)
            let format = try DisplayH264Format.make(sps: sps, pps: pps, expected: expected)
            let presented = CMVideoFormatDescriptionGetPresentationDimensions(format, usePixelAspectRatio: false, useCleanAperture: true)
            XCTAssertEqual(presented.width, CGFloat(width), accuracy: 0.01)
            XCTAssertEqual(presented.height, CGFloat(height), accuracy: 0.01)
            XCTAssertThrowsError(try DisplayH264Format.make(sps: sps, pps: pps,
                expected: DisplayVideoFormat(width: width - 16, height: height, refreshRate: 60)))
        }
    }
    private let format = DisplayVideoFormat(width: 1280, height: 800, refreshRate: 60)
    private let picture = Data([0, 0, 0, 1, 0x65, 0x80])

    private func bigEndian(_ value: UInt64, bytes: Int) -> Data {
        Data((0..<bytes).reversed().map { UInt8(truncatingIfNeeded: value >> ($0 * 8)) })
    }
    private func header(_ format: DisplayVideoFormat? = nil) -> Data {
        let value = format ?? self.format
        var data = Data("DWV1".utf8)
        data.append(bigEndian(1, bytes: 2)); data.append(bigEndian(32, bytes: 2))
        for number in [value.width, value.height, value.refreshRate, 1, DisplayVideoParser.maximumAccessUnit, 0] {
            data.append(bigEndian(UInt64(number), bytes: 4))
        }
        return data
    }
    private func packet(_ payload: Data, flags: UInt64 = 1, presentation: UInt64 = 0, length: UInt64? = nil) -> Data {
        var data = bigEndian(length ?? UInt64(payload.count), bytes: 4)
        data.append(bigEndian(flags, bytes: 4)); data.append(bigEndian(presentation, bytes: 8)); data.append(payload)
        return data
    }
    private var end: Data { packet(Data(), flags: 4) }
    private func stream() -> Data { header() + packet(picture) + end }
    private func validBegin(refresh: Int = 60, source: Int = 120) -> [String: Any] {
        let first = UUID().uuidString.replacingOccurrences(of: "-", with: "")
        let second = UUID().uuidString.replacingOccurrences(of: "-", with: "")
        return ["lease": first + String(second.prefix(16)), "codec": "h264", "width": 1280,
                "height": 800, "refreshRate": refresh, "sourceRefreshRate": source]
    }

    func testEveryPacketSplitAndSingleByteFragmentation() throws {
        let wire = stream()
        for split in 0...wire.count {
            var parser = DisplayVideoParser(expected: format), units: [DisplayVideoUnit] = []
            if split > 0 { try parser.append(wire.prefix(split)) { units.append($0) } }
            if split < wire.count { try parser.append(wire.suffix(wire.count - split)) { units.append($0) } }
            try parser.finish()
            XCTAssertEqual(units.count, 1, "Split at byte \(split)")
            XCTAssertEqual(units.first?.bytes, picture)
            XCTAssertEqual(units.first?.presentationMicroseconds, 0)
            XCTAssertEqual(units.first?.keyframe, true)
            XCTAssertEqual(units.first?.configuration, false)
        }
        var parser = DisplayVideoParser(expected: format), count = 0
        for byte in wire { try parser.append(Data([byte])) { _ in count += 1 } }
        try parser.finish()
        XCTAssertEqual(count, 1)
    }

    func testCoalescedFramesAndNonZeroDataIndices() throws {
        let wire = header() + packet(picture, presentation: 1) + packet(picture, flags: 0, presentation: 16668) + end
        let wrapped = Data([0xEE]) + wire + Data([0xEE])
        let sliced = wrapped.dropFirst().dropLast()
        var parser = DisplayVideoParser(expected: format), times: [UInt64] = []
        try parser.append(sliced) { times.append($0.presentationMicroseconds) }
        try parser.finish()
        XCTAssertEqual(times, [1, 16668])
    }

    func testConfigurationDoesNotAdvancePictureTimestamp() throws {
        let configuration = Data([0, 0, 1, 0x67, 0x42])
        let wire = header() + packet(configuration, flags: 2) + packet(picture)
            + packet(configuration, flags: 2) + packet(picture, flags: 0, presentation: 16667) + end
        var parser = DisplayVideoParser(expected: format), configurations = 0, pictures = 0
        try parser.append(wire) { unit in
            if unit.configuration { configurations += 1 } else { pictures += 1 }
        }
        try parser.finish()
        XCTAssertEqual(configurations, 2)
        XCTAssertEqual(pictures, 2)
    }

    func testHeaderMustMatchEveryNegotiatedField() {
        for index in [0, 4, 6, 8, 12, 16, 20, 24, 28] {
            var wrong = header(); wrong[index] ^= 1
            var parser = DisplayVideoParser(expected: format)
            XCTAssertThrowsError(try parser.append(wrong) { _ in }, "Header field at \(index)")
            XCTAssertFalse(parser.acceptedHeader)
        }
        var parser = DisplayVideoParser(expected: DisplayVideoFormat(width: 1280, height: 800, refreshRate: 120))
        XCTAssertThrowsError(try parser.append(header()) { _ in })
    }

    func testRejectsUnknownFlagsOversizeAndMalformedEnd() {
        let invalid = [
            packet(picture, flags: 8),
            packet(picture, length: UInt64(DisplayVideoParser.maximumAccessUnit) + 1),
            packet(Data(), flags: 0),
            packet(Data([0, 0, 1]), flags: 0),
            packet(Data([0]), flags: 4),
            packet(Data(), flags: 5),
            packet(Data(), flags: 6),
            end + Data([0])
        ]
        for (index, bytes) in invalid.enumerated() {
            var parser = DisplayVideoParser(expected: format)
            XCTAssertThrowsError(try parser.append(header() + bytes) { _ in }, "Invalid packet \(index)")
        }
    }

    func testMaximumAccessUnitCanBeFollowedByAnotherFrameInOneCallback() throws {
        let large = Data(repeating: 0x81, count: DisplayVideoParser.maximumAccessUnit)
        var parser = DisplayVideoParser(expected: format), lengths: [Int] = []
        try parser.append(header() + packet(large) + packet(picture, flags: 0, presentation: 16667) + end) {
            lengths.append($0.bytes.count)
        }
        try parser.finish()
        XCTAssertEqual(lengths, [DisplayVideoParser.maximumAccessUnit, picture.count])
    }

    func testPictureTimestampReplayAndOverflowAreRejected() {
        for next in [UInt64(100), UInt64(99), UInt64(Int64.max) + 1] {
            var parser = DisplayVideoParser(expected: format), count = 0
            XCTAssertThrowsError(try parser.append(header() + packet(picture, presentation: 100)
                + packet(picture, presentation: next)) { _ in count += 1 })
            XCTAssertEqual(count, 1)
        }
    }

    func testConfigurationCannotBypassTimestampOverflowLimit() {
        for flags in [UInt64(2), UInt64(3)] {
            var parser = DisplayVideoParser(expected: format), consumed = 0
            XCTAssertThrowsError(try parser.append(header() + packet(picture, flags: flags,
                presentation: UInt64.max)) { _ in consumed += 1 })
            XCTAssertEqual(consumed, 0)
        }
    }

    func testEndMarkerIsRequiredAndNoDataCanFollowIt() throws {
        let unfinished = [Data(), Data(header().prefix(31)), header(),
                          header() + Data(packet(picture).prefix(8)),
                          header() + Data(packet(picture).dropLast()), header() + packet(picture)]
        for bytes in unfinished {
            var parser = DisplayVideoParser(expected: format)
            try parser.append(bytes) { _ in }
            XCTAssertThrowsError(try parser.finish())
        }
        var parser = DisplayVideoParser(expected: format)
        try parser.append(stream()) { _ in }
        XCTAssertTrue(parser.ended)
        try parser.finish()
        XCTAssertThrowsError(try parser.append(packet(picture, presentation: 16667)) { _ in })
    }

    func testSessionRequiresACompleteUUIDAndNormalizesCase() throws {
        let session = UUID().uuidString
        XCTAssertEqual(try DisplayVideoPolicy.session(session), session.lowercased())
        let invalidValues: [Any] = ["", session + " ", "{" + session + "}", session.replacingOccurrences(of: "-", with: ""),
                                    String(repeating: "Z", count: 36), NSNull(), 60, true]
        for invalid in invalidValues {
            XCTAssertThrowsError(try DisplayVideoPolicy.session(invalid))
        }
        XCTAssertThrowsError(try DisplayVideoPolicy.session(nil))
    }

    func testRefreshModeUsesMeasuredCadenceWithoutSilentlyDroppingBelow60() throws {
        XCTAssertEqual(try DisplayVideoPolicy.refresh("60", maximum: 120, current: 30), 60)
        XCTAssertEqual(try DisplayVideoPolicy.refresh("match", maximum: 120), 120)
        XCTAssertEqual(try DisplayVideoPolicy.refresh("match", maximum: 120, current: 60), 60)
        XCTAssertEqual(try DisplayVideoPolicy.refresh("match", maximum: 120, current: 144), 120)
        XCTAssertEqual(try DisplayVideoPolicy.refresh("match", maximum: 360, current: 360), 240)
        XCTAssertThrowsError(try DisplayVideoPolicy.refresh("match", maximum: 120, current: 30))
        XCTAssertThrowsError(try DisplayVideoPolicy.refresh("60", maximum: 59))
        let invalidValues: [Any] = ["120", "Match", "auto", "", 60, true, NSNull()]
        for invalid in invalidValues {
            XCTAssertThrowsError(try DisplayVideoPolicy.refresh(invalid, maximum: 120))
        }
        XCTAssertThrowsError(try DisplayVideoPolicy.refresh(nil, maximum: 120))
    }

    func testBeginResponseKeepsExactLeaseRateAndSupportedResolution() throws {
        for dimensions in [(1280, 800), (1600, 900), (1920, 1080)] {
            var body = validBegin(refresh: 120)
            body["width"] = dimensions.0; body["height"] = dimensions.1
            let result = try DisplayVideoPolicy.begin(body, requestedRefreshRate: 120)
            XCTAssertEqual(result.0, body["lease"] as? String)
            XCTAssertEqual(result.1, DisplayVideoFormat(width: dimensions.0, height: dimensions.1, refreshRate: 120))
            XCTAssertEqual(result.2, 120)
        }
        var body = validBegin(refresh: 60, source: 240)
        XCTAssertEqual(try DisplayVideoPolicy.begin(body, requestedRefreshRate: 60).2, 240)
        body["refreshRate"] = 60
        XCTAssertThrowsError(try DisplayVideoPolicy.begin(body, requestedRefreshRate: 120))
    }

    func testBeginResponseRejectsMalformedAndCoercedValues() {
        let valid = validBegin(refresh: 120)
        let lease = valid["lease"] as? String ?? ""
        let invalid: [(String, Any)] = [
            ("lease", "a" + String(lease.dropFirst())), ("lease", String(lease.dropLast())),
            ("lease", String(repeating: "G", count: 48)), ("lease", NSNull()),
            ("codec", "hevc"), ("codec", "H264"),
            ("width", 1920), ("width", true), ("width", "1280"), ("width", 1280.5),
            ("height", 1200), ("height", -800), ("height", Double.infinity),
            ("refreshRate", 60), ("refreshRate", 241), ("refreshRate", 120.5),
            ("refreshRate", true), ("sourceRefreshRate", 60), ("sourceRefreshRate", 241),
            ("sourceRefreshRate", false), ("sourceRefreshRate", Double.nan)
        ]
        for (key, value) in invalid {
            var body = valid; body[key] = value
            XCTAssertThrowsError(try DisplayVideoPolicy.begin(body, requestedRefreshRate: 120), key)
        }
        for key in valid.keys {
            var body = valid; body.removeValue(forKey: key)
            XCTAssertThrowsError(try DisplayVideoPolicy.begin(body, requestedRefreshRate: 120), "Missing \(key)")
        }
    }

    func testAnnexBParsesBothStartCodesAndPreservesEscapedPayload() throws {
        let sps = Data([0x67, 0x42, 0, 0, 3, 1, 0x80]), pps = Data([0x68, 0xCE, 0x80]), idr = Data([0x65, 0xAA, 0x80])
        let bytes = Data([0, 0, 0, 1]) + sps + Data([0, 0, 1]) + pps + Data([0, 0, 0, 1]) + idr + Data([0, 0])
        XCTAssertEqual(try DisplayH264.nals(bytes), [sps, pps, idr])
        let padded = Data([0xEE]) + bytes + Data([0xEE])
        XCTAssertEqual(try DisplayH264.nals(padded.dropFirst().dropLast()), [sps, pps, idr])
    }

    func testAnnexBRejectsMalformedAndUnboundedPackets() {
        let invalid = [Data(), Data([0, 0, 1]), Data([0, 0, 0, 1]), Data([1, 0, 0, 1, 0x65]),
                       Data([0, 0, 1, 0xE5]), Data([0, 0, 1, 0x60]), Data([0, 0, 1, 0x6D]),
                       Data([0, 0, 1, 0, 0, 1, 0x65]), Data(repeating: 1, count: DisplayVideoParser.maximumAccessUnit + 1)]
        for (index, bytes) in invalid.enumerated() {
            XCTAssertThrowsError(try DisplayH264.nals(bytes), "Invalid NAL packet \(index)")
        }
        var many = Data()
        for _ in 0..<129 { many.append(contentsOf: [0, 0, 1, 0x61, 0x80]) }
        XCTAssertThrowsError(try DisplayH264.nals(many))
        XCTAssertEqual(try DisplayH264.nals(Data(many.prefix(128 * 5))).count, 128)
    }

    func testInboxOverflowKeepsLatestConfigurationThenRequiresFreshIDR() {
        let inbox = DisplayFrameInbox()
        func unit(_ marker: UInt8, keyframe: Bool = false, configuration: Bool = false) -> DisplayVideoUnit {
            DisplayVideoUnit(bytes: Data([marker]), keyframe: keyframe, configuration: configuration,
                             presentationMicroseconds: UInt64(marker))
        }
        inbox.offer(unit(1, configuration: true)); inbox.offer(unit(2, keyframe: true)); inbox.offer(unit(3))
        inbox.offer(unit(4)) // A fourth buffered unit forces a decoder resynchronization.
        inbox.offer(unit(5, configuration: true)); inbox.offer(unit(6, configuration: true))
        inbox.offer(unit(7)); inbox.offer(unit(8, keyframe: true))
        let recovered = inbox.take()
        XCTAssertTrue(recovered.1)
        XCTAssertEqual(recovered.0.map(\.bytes), [Data([6]), Data([8])])
        XCTAssertEqual(recovered.0.first?.configuration, true)
        XCTAssertEqual(recovered.0.last?.keyframe, true)
        XCTAssertGreaterThan(recovered.3, 0)
        inbox.offer(unit(9))
        let continued = inbox.take()
        XCTAssertFalse(continued.1)
        XCTAssertEqual(continued.0.map(\.bytes), [Data([9])])
    }

    func testInboxByteLimitAndStopCannotRetainOrAcceptFrames() {
        let inbox = DisplayFrameInbox()
        let large = Data(repeating: 1, count: DisplayVideoParser.maximumAccessUnit)
        inbox.offer(DisplayVideoUnit(bytes: large, keyframe: true, configuration: false, presentationMicroseconds: 1))
        inbox.offer(DisplayVideoUnit(bytes: large, keyframe: false, configuration: false, presentationMicroseconds: 2))
        inbox.offer(DisplayVideoUnit(bytes: picture, keyframe: false, configuration: false, presentationMicroseconds: 3))
        let overBudget = inbox.take()
        XCTAssertTrue(overBudget.1)
        XCTAssertTrue(overBudget.0.isEmpty)

        let key = DisplayVideoUnit(bytes: picture, keyframe: true, configuration: false, presentationMicroseconds: 4)
        inbox.offer(key)
        inbox.stop()
        inbox.offer(key)
        XCTAssertTrue(inbox.take().0.isEmpty)
    }
}

private final class DisplayTestNavigation: NSObject, WKNavigationDelegate {
    private let loaded: () -> Void
    init(_ loaded: @escaping () -> Void) { self.loaded = loaded }
    func webView(_ webView: WKWebView, didFinish navigation: WKNavigation!) { loaded() }
}
