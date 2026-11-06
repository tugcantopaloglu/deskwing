import XCTest
import UIKit
import PDFKit
@testable import DeskWing

final class NotebookDocumentPolicyTests: XCTestCase {
    func testLocalPathsAndBodyFieldsStayExact() throws {
        XCTAssertEqual(NotebookDocumentPolicy.paths.count, 8)
        XCTAssertTrue(NotebookDocumentPolicy.paths.contains("/local/notebook/read-pdf-image"))
        XCTAssertFalse(NotebookDocumentPolicy.paths.contains("/local/notebook/../../export"))
        try NotebookDocumentPolicy.exact(["id":"x"], ["id"])
        XCTAssertThrowsError(try NotebookDocumentPolicy.exact(["id":"x", "path":"/tmp/private"], ["id"]))
    }
    func testNumericPositionsRejectBooleansFractionsAndOverflow() throws {
        XCTAssertEqual(try NotebookDocumentPolicy.integer(NSNumber(value: 8192), minimum: 1, maximum: 8192), 8192)
        for value in [NSNumber(value: true), NSNumber(value: -1), NSNumber(value: 0.5), NSNumber(value: 8193), NSNumber(value: Double.infinity)] {
            XCTAssertThrowsError(try NotebookDocumentPolicy.integer(value, minimum: 0, maximum: 8192))
        }
    }
    func testHandlesAreOpaqueAndNamesCannotSelectPaths() throws {
        XCTAssertEqual(try NotebookDocumentPolicy.handle(String(repeating: "a1", count: 16)), String(repeating: "a1", count: 16))
        for value in ["../../private", "", String(repeating:"A", count:32), String(repeating:"a", count:33)] {
            XCTAssertThrowsError(try NotebookDocumentPolicy.handle(value))
        }
        let name = NotebookDocumentPolicy.name("../Secret:\\notes\n.pdf")
        XCTAssertFalse(name.contains("/")); XCTAssertFalse(name.contains("\\")); XCTAssertFalse(name.contains(":")); XCTAssertFalse(name.contains("\n"))
        XCTAssertTrue(name.hasSuffix(".pdf")); XCTAssertLessThanOrEqual(NotebookDocumentPolicy.name(String(repeating:"x",count:1000)).count,84)
    }
    func testChunksStayUnderTheGenericBridgeEnvelope() throws {
        let largest = Data(repeating: 7, count:8192)
        XCTAssertEqual(try NotebookDocumentPolicy.chunk(largest.base64EncodedString()), largest)
        XCTAssertLessThan(largest.base64EncodedString().utf8.count + 2048,16384)
        XCTAssertThrowsError(try NotebookDocumentPolicy.chunk(Data(repeating: 7, count:8193).base64EncodedString()))
        XCTAssertThrowsError(try NotebookDocumentPolicy.chunk("a!b!")); XCTAssertThrowsError(try NotebookDocumentPolicy.chunk(""))
        XCTAssertTrue(NotebookDocumentPolicy.pdfHeader(Data("%PDF-1.7".utf8)))
        XCTAssertFalse(NotebookDocumentPolicy.pdfHeader(Data("<html>".utf8)))
    }
    func testRasterDimensionsAreBoundedAndRotationAware() throws {
        let normal = try NotebookDocumentPolicy.rasterSize(CGRect(x:0,y:0,width:600,height:800),rotation:0)
        let rotated = try NotebookDocumentPolicy.rasterSize(CGRect(x:0,y:0,width:600,height:800),rotation:90)
        XCTAssertEqual(normal.height,2048); XCTAssertEqual(normal.width,1536)
        XCTAssertEqual(rotated.width,2048); XCTAssertEqual(rotated.height,1536)
        XCTAssertLessThanOrEqual(normal.width * normal.height,4_000_000)
        let square=try NotebookDocumentPolicy.rasterSize(CGRect(x:0,y:0,width:4096,height:4096),rotation:0)
        XCTAssertEqual(square.width,2000); XCTAssertEqual(square.height,2000)
        XCTAssertLessThanOrEqual(square.width * square.height,4_000_000)
        // Core Graphics standardizes a rectangle's negative stored size when reading width/height.
        // It describes the same bounded geometry, rather than an invalid PDF page dimension.
        let reversed=CGRect(x:0,y:0,width:-600,height:800)
        XCTAssertEqual(try NotebookDocumentPolicy.rasterSize(reversed,rotation:0),normal)
        XCTAssertEqual(try NotebookDocumentPolicy.rasterSize(reversed.standardized,rotation:0),normal)
        for bounds in [CGRect.zero, CGRect(x:0,y:0,width:0,height:4),CGRect(x:0,y:0,width:1_000_001,height:1),CGRect(x:0,y:0,width:CGFloat.infinity,height:1)] {
            XCTAssertThrowsError(try NotebookDocumentPolicy.rasterSize(bounds,rotation:0))
        }
    }
    @MainActor
    func testPrivateImportRendersBoundedChunkedImagesAndRevokesOldImageHandles() async throws {
        let source = try fixturePDF(pages:2), original = try Data(contentsOf:source), worker = NotebookPDFWorker()
        defer { worker.invalidate(1); try? FileManager.default.removeItem(at:source) }
        let opened = try await run(worker) { try $0.open(source,epoch:0) }
        XCTAssertEqual(opened["pageCount"] as? Int,2); XCTAssertEqual(opened["nativeRaster"] as? Bool,true)
        let id = try XCTUnwrap(opened["id"] as? String)
        let first = try await run(worker) { try $0.render(id,page:0,epoch:0) }
        let imageID = try XCTUnwrap(first["imageId"] as? String), size = try XCTUnwrap(first["size"] as? Int)
        XCTAssertGreaterThan(size,8); XCTAssertLessThanOrEqual(size,NotebookDocumentPolicy.maxPNGBytes)
        XCTAssertLessThanOrEqual(first["width"] as? Int ?? Int.max,2048); XCTAssertLessThanOrEqual(first["height"] as? Int ?? Int.max,2048)
        XCTAssertNil(first["backgroundPng"],"A full image must never enter one bridge response.")
        let chunk = try await run(worker) { try $0.readImage(imageID,offset:0,length:8192) }
        let bytes = try XCTUnwrap(Data(base64Encoded:try XCTUnwrap(chunk["dataBase64"] as? String)))
        XCTAssertEqual(Array(bytes.prefix(8)),[137,80,78,71,13,10,26,10]); XCTAssertLessThanOrEqual(bytes.count,8192)
        _ = try await run(worker) { try $0.render(id,page:1,epoch:0) }
        do { _ = try await run(worker) { try $0.readImage(imageID,offset:0,length:8192) }; XCTFail("Old image handle survived another render.") } catch {}
        _ = try await run(worker) { try $0.closeImport(id) }
        do { _ = try await run(worker) { try $0.render(id,page:0,epoch:0) }; XCTFail("Closed PDF handle remained usable.") } catch {}
        XCTAssertEqual(try Data(contentsOf:source),original,"Import must not modify the selected PDF.")
    }
    @MainActor
    func testRotatedPDFRendersInsideThePixelBudgetWithoutADeviceScaleMultiplier() async throws {
        let source = try fixturePDF(pages:1), worker = NotebookPDFWorker()
        defer { worker.invalidate(1); try? FileManager.default.removeItem(at:source) }
        let document = try XCTUnwrap(PDFDocument(url:source)), page = try XCTUnwrap(document.page(at:0)); page.rotation = 90
        XCTAssertTrue(document.write(to:source))
        let opened = try await run(worker) { try $0.open(source,epoch:0) }, id = try XCTUnwrap(opened["id"] as? String)
        let rendered = try await run(worker) { try $0.render(id,page:0,epoch:0) }
        let imageID = try XCTUnwrap(rendered["imageId"] as? String), count = try XCTUnwrap(rendered["size"] as? Int)
        var bytes=Data(), offset=0
        while offset<count {
            let start=offset, chunk=try await run(worker) { try $0.readImage(imageID,offset:start,length:8192) }
            let part=try XCTUnwrap(Data(base64Encoded:try XCTUnwrap(chunk["dataBase64"] as? String))); XCTAssertFalse(part.isEmpty)
            bytes.append(part); offset += part.count
        }
        let image=try XCTUnwrap(UIImage(data:bytes)?.cgImage)
        XCTAssertGreaterThan(image.width,image.height); XCTAssertLessThanOrEqual(image.width,2048); XCTAssertLessThanOrEqual(image.height,2048)
        var pixels=[UInt8](repeating:0,count:64*64*4)
        let hasContent=try pixels.withUnsafeMutableBytes { raw -> Bool in
            let context=try XCTUnwrap(CGContext(data:raw.baseAddress,width:64,height:64,bitsPerComponent:8,bytesPerRow:64*4,space:CGColorSpaceCreateDeviceRGB(),bitmapInfo:CGImageAlphaInfo.premultipliedLast.rawValue))
            context.draw(image,in:CGRect(x:0,y:0,width:64,height:64))
            return stride(from:0,to:raw.count,by:4).contains { raw[$0+3]>200 && (raw[$0]<230 || raw[$0+1]<230 || raw[$0+2]<230) }
        }
        XCTAssertTrue(hasContent,"The rotated page must contain the fixture's drawn content.")
    }
    @MainActor
    func testExportRequiresOrderedChunksAndExactDeclaredSize() async throws {
        let source = try fixturePDF(pages:1), data = try Data(contentsOf:source), worker = NotebookPDFWorker()
        defer { worker.invalidate(1); try? FileManager.default.removeItem(at:source) }
        let opened = try await run(worker) { try $0.beginExport(name:"Safe notebook.pdf",size:data.count) }
        let id = try XCTUnwrap(opened["id"] as? String)
        do { _ = try await run(worker) { try $0.writeExport(id,offset:1,bytes:data.prefix(5)) }; XCTFail("Out-of-order export was accepted.") } catch {}
        do { _ = try await run(worker) { try $0.finishExport(id) }; XCTFail("Incomplete export was accepted.") } catch {}
        var offset = 0
        while offset < data.count {
            let end = min(data.count,offset+8192), part = data.subdata(in:offset..<end), start = offset
            _ = try await run(worker) { try $0.writeExport(id,offset:start,bytes:part) }; offset = end
        }
        do { _ = try await run(worker) { try $0.writeExport(id,offset:0,bytes:Data("%PDF-".utf8)) }; XCTFail("A repeated chunk was accepted.") } catch {}
        let ready = try await run(worker) { try $0.finishExport(id) }, url = try XCTUnwrap(ready["url"] as? URL)
        XCTAssertEqual(try Data(contentsOf:url),data)
        _ = try await run(worker) { try $0.cancelExport(id) }
        XCTAssertFalse(FileManager.default.fileExists(atPath:url.path))
    }
    @MainActor
    func testCancelledWorkerEpochCannotImportIntoTheNewSession() async throws {
        let source = try fixturePDF(pages:1), worker = NotebookPDFWorker()
        defer { worker.invalidate(2); try? FileManager.default.removeItem(at:source) }
        worker.invalidate(1)
        do { _ = try await run(worker,epoch:0) { try $0.open(source,epoch:0) }; XCTFail("An old document epoch opened a PDF.") } catch {}
        let current = try await runEventually(worker,epoch:1) { try $0.open(source,epoch:1) }
        XCTAssertNotNil(current["id"])
    }
    @MainActor
    func testBlockedWorkerKeepsAdmissionBoundedAcrossRepeatedCancellation() async throws {
        let worker=NotebookPDFWorker(), entered=expectation(description:"Worker entered"), finished=expectation(description:"Worker finished")
        let gate=DispatchSemaphore(value:0)
        defer { gate.signal(); worker.invalidate(2) }
        worker.run(0,work:{ _ in entered.fulfill(); guard gate.wait(timeout:.now()+5) == .success else { throw DeskError.message("Fixture release timed out.") }; return ["done":true] }) { _ in finished.fulfill() }
        await fulfillment(of:[entered],timeout:1)
        for _ in 0..<1000 { worker.invalidate(1) }
        do { _ = try await run(worker,epoch:1) { _ in ["unexpected":true] }; XCTFail("A second operation was admitted while the first was blocked.") } catch {}
        gate.signal(); await fulfillment(of:[finished],timeout:2)
        let next=try await runEventually(worker,epoch:1) { _ in ["ready":true] }
        XCTAssertEqual(next["ready"] as? Bool,true)
    }
    @MainActor
    func testImportRejectsMoreThanTwentyPagesWithoutChangingSource() async throws {
        let source = try fixturePDF(pages:21), bytes = try Data(contentsOf:source), worker = NotebookPDFWorker()
        defer { worker.invalidate(1); try? FileManager.default.removeItem(at:source) }
        do { _ = try await run(worker) { try $0.open(source,epoch:0) }; XCTFail("Too many PDF pages were accepted.") } catch {}
        XCTAssertEqual(try Data(contentsOf:source),bytes)
    }
    @MainActor
    private func fixturePDF(pages:Int) throws -> URL {
        let renderer = UIGraphicsPDFRenderer(bounds:CGRect(x:0,y:0,width:300,height:400))
        let data = renderer.pdfData { context in
            for page in 0..<pages { context.beginPage(); UIColor.white.setFill(); UIRectFill(CGRect(x:0,y:0,width:300,height:400)); UIColor.red.setFill(); UIRectFill(CGRect(x:20,y:80,width:80,height:40)); ("Fixture page \(page+1)" as NSString).draw(at:CGPoint(x:20,y:20),withAttributes:[.font:UIFont.systemFont(ofSize:18)]) }
        }
        let url=FileManager.default.temporaryDirectory.appendingPathComponent("notebook-policy-"+UUID().uuidString+".pdf")
        try data.write(to:url,options:.atomic); return url
    }
    private func run(_ worker:NotebookPDFWorker,epoch:UInt64=0,_ work:@escaping (NotebookPDFWorker)throws->[String:Any]) async throws -> [String:Any] {
        try await withCheckedThrowingContinuation { continuation in worker.run(epoch,work:work) { continuation.resume(with:$0) } }
    }
    private func runEventually(_ worker:NotebookPDFWorker,epoch:UInt64,_ work:@escaping (NotebookPDFWorker)throws->[String:Any]) async throws -> [String:Any] {
        for attempt in 0..<40 {
            do { return try await run(worker,epoch:epoch,work) }
            catch { if attempt==39 { throw error }; try await Task.sleep(nanoseconds:5_000_000) }
        }
        throw DeskError.message("Notebook worker did not become ready.")
    }
}
