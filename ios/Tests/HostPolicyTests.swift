import XCTest
@testable import DeskWing

final class HostPolicyTests: XCTestCase {
    func testRendererRestartCannotCompleteReusedRequest() throws {
        var requests = BridgeRequests()
        let beforeReload = try XCTUnwrap(requests.begin(1))
        XCTAssertNil(requests.begin(1))
        requests.reset()
        let afterReload = try XCTUnwrap(requests.begin(1))
        XCTAssertFalse(requests.finish(1, ticket: beforeReload))
        XCTAssertTrue(requests.contains(1))
        XCTAssertTrue(requests.finish(1, ticket: afterReload))
        XCTAssertFalse(requests.finish(1, ticket: afterReload))
        XCTAssertEqual(requests.count, 0)
    }
    func testPrivateIPv4Boundary() {
        for host in ["10.0.0.4", "192.168.1.42", "172.16.0.1", "172.31.255.254", "169.254.1.4", "100.64.0.1"] {
            XCTAssertTrue(HostPolicy.validHost(host), host)
        }
        for host in ["", "localhost", "127.0.0.1", "8.8.8.8", "172.15.1.1", "172.32.0.1", "100.128.0.1", "192.168.1.256", "192.168.01.1", "192.168.1.1:8743", "192.168.1.1/path", "192.168.1.1@evil.example", "::1"] {
            XCTAssertFalse(HostPolicy.validHost(host), host)
        }
    }
    func testCertificatePinFormatAndPrefix() {
        let fingerprint = String(repeating: "A1", count: 32)
        XCTAssertEqual(HostPolicy.normalizedPin("a1a1-a1a1 a1a1:a1a1 a1a1 a1a1"), String(repeating: "A1", count: 12))
        XCTAssertTrue(HostPolicy.pinMatches(String(fingerprint.prefix(24)), fingerprint))
        XCTAssertTrue(HostPolicy.pinMatches(fingerprint, fingerprint))
        XCTAssertFalse(HostPolicy.pinMatches(String(repeating: "B1", count: 12), fingerprint))
        XCTAssertNil(HostPolicy.normalizedPin("https://192.168.1.1"))
        XCTAssertNil(HostPolicy.normalizedPin("1234"))
    }
    func testBridgeBoundary() {
        XCTAssertTrue(HostPolicy.allowed("/api/action", method: "POST"))
        XCTAssertTrue(HostPolicy.allowed("/api/computer/follow", method: "POST"))
        XCTAssertTrue(HostPolicy.allowed("/api/ink", method: "GET"))
        XCTAssertTrue(HostPolicy.allowed("/api/ink", method: "POST"))
        let revisionPath = "/api/ink?revision=" + String(repeating: "aB01", count: 16)
        XCTAssertTrue(HostPolicy.allowed(revisionPath, method: "GET"))
        XCTAssertFalse(HostPolicy.allowed(revisionPath, method: "POST"))
        XCTAssertFalse(HostPolicy.allowed(revisionPath + "&other=1", method: "GET"))
        XCTAssertFalse(HostPolicy.allowed("/api/ink?revision=" + String(repeating: "z", count: 64), method: "GET"))
        XCTAssertFalse(HostPolicy.allowed("/api/ink?revision=%41" + String(repeating: "A", count: 63), method: "GET"))
        XCTAssertTrue(HostPolicy.allowed("/api/pen", method: "GET"))
        XCTAssertTrue(HostPolicy.allowed("/api/pen", method: "POST"))
        for path in ["/api/admin/config", "/api/action?admin=1", "/api/../admin/config", "https://192.168.1.1/api/action", "/api/pair"] {
            XCTAssertFalse(HostPolicy.allowed(path, method: "POST"), path)
        }
        XCTAssertFalse(HostPolicy.allowed("/api/action", method: "GET"))
        XCTAssertFalse(HostPolicy.allowed("/api/board", method: "DELETE"))
        XCTAssertTrue(HostPolicy.trustedPage(URL(string: "deskwing://app/index.html")))
        for url in ["https://app/index.html", "deskwing://evil/index.html", "deskwing://app/desktop.html", "deskwing://app/index.html?route=admin", "deskwing://name@app/index.html"] {
            XCTAssertFalse(HostPolicy.trustedPage(URL(string: url)), url)
        }
    }
    func testBackgroundOnlyAllowsSafetyCommands() {
        XCTAssertTrue(HostPolicy.allowedInBackground("/api/hold", body: ["operation":"release"]))
        XCTAssertTrue(HostPolicy.allowedInBackground("/api/cancel", body: [:]))
        XCTAssertFalse(HostPolicy.allowedInBackground("/api/hold", body: ["operation":"begin"]))
        XCTAssertFalse(HostPolicy.allowedInBackground("/api/soundpad/play", body: [:]))
        XCTAssertFalse(HostPolicy.allowedInBackground("/api/computer/follow", body: ["automatic":true]))
        XCTAssertTrue(HostPolicy.allowedInBackground("/api/pen", body: ["operation":"end"]))
        XCTAssertTrue(HostPolicy.allowedInBackground("/api/pen", body: ["operation":"cancel"]))
        XCTAssertFalse(HostPolicy.allowedInBackground("/api/pen", body: ["operation":"begin"]))
        XCTAssertFalse(HostPolicy.allowedInBackground("/api/pen", body: ["operation":"samples"]))
        XCTAssertFalse(HostPolicy.allowedInBackground("/api/pen", body: ["operation":"renew"]))
        XCTAssertFalse(HostPolicy.allowedInBackground("/api/ink", body: [:]))
        XCTAssertTrue(HostPolicy.needsDispatchDeadline("/api/action", method: "POST", body: [:]))
        XCTAssertTrue(HostPolicy.needsDispatchDeadline("/api/soundpad/play", method: "POST", body: [:]))
        XCTAssertFalse(HostPolicy.needsDispatchDeadline("/api/hold", method: "POST", body: ["operation":"release"]))
        XCTAssertFalse(HostPolicy.needsDispatchDeadline("/api/monitor/frame", method: "POST", body: [:]))
        XCTAssertFalse(HostPolicy.needsDispatchDeadline("/api/board", method: "GET", body: [:]))
        XCTAssertFalse(HostPolicy.needsDispatchDeadline("/api/pen", method: "POST", body: ["operation":"cancel"]))
        XCTAssertTrue(HostPolicy.needsDispatchDeadline("/api/pen", method: "POST", body: ["operation":"samples"]))
    }
    func testInkExceptionDoesNotExpandOtherRequests() {
        XCTAssertEqual(HostPolicy.maximumBodyBytes("/api/ink"), 4 * 1024 * 1024)
        XCTAssertEqual(HostPolicy.maximumBridgeBytes("/api/ink"), 4 * 1024 * 1024 + 16384)
        XCTAssertEqual(HostPolicy.maximumBodyBytes("/api/pen"), 16384)
        XCTAssertEqual(HostPolicy.maximumBodyBytes("/api/ink?x=1"), 16384)
        XCTAssertEqual(HostPolicy.maximumBridgeBytes("/api/admin/config"), 16384)
        XCTAssertEqual(HostPolicy.dispatchDeadline("/api/pen"), 0.5)
        XCTAssertEqual(HostPolicy.dispatchDeadline("/api/action"), 2)
    }
    func testPNGAndFilenameValidation() {
        XCTAssertEqual(PNGExport.safeName("../private.png"), "DeskWing-sketch.png")
        XCTAssertEqual(PNGExport.safeName("My note-2.png"), "My note-2.png")
        XCTAssertThrowsError(try PNGExport.decode("data:image/jpeg;base64,AAAA"))
        XCTAssertThrowsError(try PNGExport.decode("data:image/png;base64,AAAA"))
    }
    func testDamagedCertificateFailsClosed() {
        XCTAssertFalse(CertificateDates.valid(Data()))
        XCTAssertFalse(CertificateDates.valid(Data([0x30,0x84,0xFF,0xFF,0xFF,0xFF])))
        XCTAssertFalse(CertificateDates.valid(Data(repeating: 0, count: 500)))
    }
}
