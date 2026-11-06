import XCTest
@testable import DeskWing

final class NotebookPolicyTests: XCTestCase {
    private let page = "/api/notebooks/" + String(repeating: "a", count: 32) + "/pages/" + String(repeating: "B", count: 32)
    private let revision = String(repeating: "f", count: 64)

    func testCanonicalNotebookReadsAndWrites() {
        for path in ["/api/notebooks", page, page + "?revision=" + revision, page + "/history"] {
            XCTAssertTrue(HostPolicy.allowed(path, method: "GET"), path)
            XCTAssertEqual(HostPolicy.maximumBridgeBytes(path, method: "GET"), 16384)
            XCTAssertEqual(HostPolicy.maximumResponseBytes(path), 8 * 1024 * 1024)
        }
        for path in ["/api/notebooks", page, page + "/history/restore"] {
            XCTAssertTrue(HostPolicy.allowed(path, method: "POST"), path)
            XCTAssertFalse(HostPolicy.allowed(path, method: "DELETE"), path)
            XCTAssertFalse(HostPolicy.allowed(path, method: "post"), path)
        }
        XCTAssertFalse(HostPolicy.allowed(page + "?revision=" + revision, method: "POST"))
        XCTAssertFalse(HostPolicy.allowed(page + "/history", method: "POST"))
        XCTAssertFalse(HostPolicy.allowed(page + "/history/restore", method: "GET"))
    }

    func testNotebookRouteVariantsDoNotExpandPrivileges() {
        let invalid = [
            "/api/notebooks/", "/api/notebooks?x=1", "/api/notebooks?revision=" + revision,
            page + "/", page + "?revision=", page + "?Revision=" + revision,
            page + "?revision=" + String(repeating: "a", count: 63),
            page + "?revision=" + String(repeating: "a", count: 65),
            page + "?revision=" + String(repeating: "g", count: 64),
            page + "?revision=" + revision + "&extra=1",
            page + "?revision=" + revision + "&revision=" + revision,
            page + "?revision=" + revision + "#fragment",
            page + "?revision=%41" + String(repeating: "a", count: 63),
            page + "?revision=" + revision + "?",
            page + "/history?revision=" + revision, page + "/history/restore/",
            page.replacingOccurrences(of: "/pages/", with: "//pages/"),
            page.replacingOccurrences(of: "/pages/", with: "/../pages/"),
            page.replacingOccurrences(of: "/pages/", with: "/%70ages/"),
            page.replacingOccurrences(of: "/", with: "\\"),
            page + "#fragment", page + "\n", page + "\r", page + " ",
            "/api/notebooks/" + String(repeating: "a", count: 31) + "/pages/" + String(repeating: "a", count: 32),
            "/api/notebooks/" + String(repeating: "a", count: 32) + "/pages/" + String(repeating: "a", count: 33),
            "https://app" + page, "/api/admin/config"
        ]
        for path in invalid {
            XCTAssertFalse(HostPolicy.allowed(path, method: "GET"), path)
            XCTAssertFalse(HostPolicy.allowed(path, method: "POST"), path)
            XCTAssertEqual(HostPolicy.maximumBodyBytes(path), 16384, path)
            XCTAssertEqual(HostPolicy.maximumBridgeBytes(path), 16384, path)
            XCTAssertNil(HostPolicy.notebookRevision(path), path)
            XCTAssertEqual(HostPolicy.maximumResponseBytes(path), 6 * 1024 * 1024, path)
        }
    }

    func testNotebookLimitsDoNotExpandNativeControls() {
        for path in ["/api/notebooks", page] {
            XCTAssertEqual(HostPolicy.maximumBodyBytes(path), 4 * 1024 * 1024)
            XCTAssertEqual(HostPolicy.maximumBridgeBytes(path, method: "POST"), 4 * 1024 * 1024 + 16384)
        }
        for path in [page + "/history/restore", "/local/notebook/export-pdf/write", "/api/computer/display/input"] {
            XCTAssertEqual(HostPolicy.maximumBodyBytes(path), 16384)
            XCTAssertEqual(HostPolicy.maximumBridgeBytes(path, method: "POST"), 16384)
        }
        XCTAssertFalse(HostPolicy.allowedInBackground(page, body: [:]))
        XCTAssertTrue(HostPolicy.needsDispatchDeadline(page, method: "POST", body: [:]))
        XCTAssertEqual(HostPolicy.notebookRevision(page + "?revision=" + revision), revision)
    }

    func testDisplayStatusReadDoesNotAuthorizeInputOrStreamControl() {
        for path in ["/api/computer/display/input", "/api/computer/display/stream/status"] {
            XCTAssertTrue(HostPolicy.allowed(path, method: "GET"))
            XCTAssertFalse(HostPolicy.allowed(path, method: "POST"))
            XCTAssertFalse(HostPolicy.allowed(path + "?other=1", method: "GET"))
            XCTAssertFalse(HostPolicy.allowed(path + "/", method: "GET"))
        }
        XCTAssertFalse(HostPolicy.allowed("/api/computer/display/stream/start", method: "POST"))
        XCTAssertFalse(HostPolicy.allowed("/api/computer/display/stream/stop", method: "POST"))
    }

    func testOldNotebookCannotUploadAfterPCChangedBeforeJavaScriptEvent() throws {
        let pcA = "11111111-2222-4333-8444-555555555555", pcB = "AAAAAAAA-BBBB-4CCC-8DDD-EEEEEEEEEEEE"
        let captured = try NotebookPCRequestScope(path: page, expectedPairingID: pcA, currentPairingID: pcA)
        var encodedBodies = 0, uploads = 0
        XCTAssertThrowsError(try captured.perform(currentPairingID: pcB) {
            encodedBodies += 1
            uploads += 1
        })
        XCTAssertEqual(encodedBodies, 0)
        XCTAssertEqual(uploads, 0)
        XCTAssertThrowsError(try NotebookPCRequestScope(path: page, expectedPairingID: pcA, currentPairingID: pcB))
        XCTAssertThrowsError(try captured.requireCurrent(nil))
    }

    func testNotebookContextSurvivesPickerPauseButNotPairForgetOrTransport() throws {
        let pcA = "11111111-2222-4333-8444-555555555555", replaced = "AAAAAAAA-BBBB-4CCC-8DDD-EEEEEEEEEEEE"
        let captured = try NotebookPCRequestScope(path: "/api/notebooks", expectedPairingID: pcA, currentPairingID: pcA)
        XCTAssertNoThrow(try captured.requireCurrent(pcA)) // Background/SAF does not rotate pairing ID.
        XCTAssertEqual(try captured.perform(currentPairingID: pcA) { "original-PC-only" }, "original-PC-only")
        XCTAssertThrowsError(try captured.requireCurrent(replaced))
        XCTAssertThrowsError(try captured.requireCurrent(nil))
        XCTAssertThrowsError(try captured.perform(currentPairingID: replaced) { XCTFail("Stale response may not commit") })
    }

    func testNotebookContextIsRequiredAndCannotBeRefreshedFromTheBody() throws {
        let current = "11111111-2222-4333-8444-555555555555"
        let invalidValues: [Any] = [NSNull(), "", "1-2-3-4-5", current + " ", current + "\n", 42, ["expectedPairingId":current]]
        for invalid in invalidValues {
            XCTAssertThrowsError(try NotebookPCRequestScope(path: page, expectedPairingID: invalid, currentPairingID: current))
        }
        XCTAssertThrowsError(try NotebookPCRequestScope(path: page, expectedPairingID: nil, currentPairingID: current))
        XCTAssertThrowsError(try NotebookPCRequestScope(path: page, expectedPairingID: current, currentPairingID: nil))
        let unscopedControl = try NotebookPCRequestScope(path: "/api/action", expectedPairingID: nil, currentPairingID: nil)
        XCTAssertNil(unscopedControl.pairingID)
    }

    func testLegacyScratchpadCannotReachReplacementPCBeforeCAS() throws {
        let pcA = "11111111-2222-4333-8444-555555555555", pcB = "AAAAAAAA-BBBB-4CCC-8DDD-EEEEEEEEEEEE"
        for path in ["/api/ink", "/api/ink?revision=" + revision] {
            XCTAssertTrue(HostPolicy.notebookNeedsContext(path))
            XCTAssertThrowsError(try NotebookPCRequestScope(path: path, expectedPairingID: nil, currentPairingID: pcA))
            let original = try NotebookPCRequestScope(path: path, expectedPairingID: pcA, currentPairingID: pcA)
            var requests = 0
            XCTAssertThrowsError(try original.perform(currentPairingID: pcB) { requests += 1 })
            XCTAssertEqual(requests, 0)
            XCTAssertNoThrow(try original.perform(currentPairingID: pcA) { requests += 1 })
            XCTAssertEqual(requests, 1)
            XCTAssertEqual(HostPolicy.maximumBridgeBytes(path, method: "GET"), 16384)
        }
        XCTAssertEqual(HostPolicy.maximumBodyBytes("/api/ink"), 4 * 1024 * 1024)
        XCTAssertEqual(HostPolicy.maximumBridgeBytes("/api/ink", method: "POST"), 4 * 1024 * 1024 + 16384)
        XCTAssertFalse(HostPolicy.allowed("/api/ink?extra=1", method: "GET"))
        XCTAssertFalse(HostPolicy.allowed("/api/ink?revision=" + revision, method: "POST"))
    }
}
