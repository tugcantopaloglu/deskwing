import UIKit
import PDFKit
import UniformTypeIdentifiers
import ImageIO

/// Local document policy. Filenames are display metadata; all private paths use generated UUIDs.
enum NotebookDocumentPolicy {
    static let maxImportBytes = 20 * 1024 * 1024
    static let maxExportBytes = 32 * 1024 * 1024
    static let maxPNGBytes = 1024 * 1024
    static let maxPages = 20
    static let maxChunkBytes = 8192
    static let paths: Set<String> = [
        "/local/notebook/import-pdf", "/local/notebook/render-pdf", "/local/notebook/read-pdf-image",
        "/local/notebook/close-pdf", "/local/notebook/export-pdf/start", "/local/notebook/export-pdf/write",
        "/local/notebook/export-pdf/finish", "/local/notebook/export-pdf/cancel"
    ]
    static func exact(_ body: [String: Any], _ fields: Set<String>) throws {
        guard Set(body.keys) == fields else { throw DeskError.message("The notebook document request has invalid fields.") }
    }
    static func integer(_ value: Any?, minimum: Int, maximum: Int) throws -> Int {
        guard let number = value as? NSNumber, CFGetTypeID(number) != CFBooleanGetTypeID(),
              number.doubleValue.isFinite, number.doubleValue == Double(number.intValue),
              (minimum...maximum).contains(number.intValue) else { throw DeskError.message("The notebook document size or position is invalid.") }
        return number.intValue
    }
    static func handle(_ value: Any?) throws -> String {
        guard let text = value as? String, text.utf8.count == 32,
              text.utf8.allSatisfy({ (48...57).contains($0) || (97...102).contains($0) }) else {
            throw DeskError.message("This notebook document session is no longer available.")
        }
        return text
    }
    static func name(_ value: Any?) -> String {
        let supplied = value as? String ?? ""
        let base = supplied.lowercased().hasSuffix(".pdf") ? String(supplied.dropLast(4)) : supplied
        let filtered = base.unicodeScalars.map { scalar -> String in
            let value = scalar.value
            return (48...57).contains(value) || (65...90).contains(value) || (97...122).contains(value) || [32,45,95].contains(value) ? String(scalar) : "_"
        }.joined().trimmingCharacters(in: .whitespaces)
        return (filtered.isEmpty ? "DeskWing-notebook" : String(filtered.prefix(80))) + ".pdf"
    }
    static func chunk(_ value: Any?) throws -> Data {
        guard let encoded = value as? String, !encoded.isEmpty, encoded.utf8.count <= ((maxChunkBytes + 2) / 3) * 4,
              let bytes = Data(base64Encoded: encoded), !bytes.isEmpty, bytes.count <= maxChunkBytes else {
            throw DeskError.message("Send notebook export chunks of at most 8 KB.")
        }
        return bytes
    }
    static func pdfHeader(_ data: Data) -> Bool { data.count >= 5 && data.prefix(5) == Data("%PDF-".utf8) }
    static func rasterSize(_ bounds: CGRect, rotation: Int) throws -> CGSize {
        var width = bounds.width, height = bounds.height
        guard width.isFinite, height.isFinite, width > 0, height > 0, width <= 1_000_000, height <= 1_000_000 else {
            throw DeskError.message("This PDF page has unsupported dimensions.")
        }
        if abs(rotation % 180) == 90 { swap(&width, &height) }
        let scale = min(2048 / width, min(2048 / height, sqrt(4_000_000 / (width * height))))
        return CGSize(width: max(1, floor(width * scale)), height: max(1, floor(height * scale)))
    }
}

/// PDFKit is confined to one serial worker. Cancellation only changes a locked epoch on the UI thread.
final class NotebookPDFWorker: @unchecked Sendable {
    private let queue = DispatchQueue(label: "DeskWing notebook documents", qos: .userInitiated)
    private let lock = NSLock()
    private var acceptedEpoch: UInt64 = 0
    private var busy = false, cleanupQueued = false
    private var imported: Imported?
    private var exporting: Exporting?
    private struct Imported {
        let id: String, name: String, folder: URL, document: PDFDocument
        var imageID: String?, image = Data()
    }
    private struct Exporting {
        let id: String, folder: URL, url: URL, expected: Int
        var handle: FileHandle?, written = 0, prefix = Data()
    }
    private func check(_ epoch: UInt64) throws {
        lock.lock(); let matches = acceptedEpoch == epoch; lock.unlock()
        guard matches else { throw DeskError.message("The notebook document operation was cancelled.") }
    }
    func invalidate(_ epoch: UInt64) {
        lock.lock(); acceptedEpoch = epoch
        if !cleanupQueued {
            cleanupQueued = true
            queue.async {
                self.clearImport(); self.clearExport()
                self.lock.lock(); self.cleanupQueued = false; self.lock.unlock()
            }
        }
        lock.unlock()
    }
    func run(_ epoch: UInt64, work: @escaping (NotebookPDFWorker) throws -> [String: Any], completion: @escaping (Result<[String: Any], Error>) -> Void) {
        lock.lock()
        guard acceptedEpoch == epoch, !busy, !cleanupQueued else {
            let message = acceptedEpoch == epoch ? "The previous notebook document operation is still closing. Try again shortly." : "The notebook document operation was cancelled."
            lock.unlock(); DispatchQueue.main.async { completion(.failure(DeskError.message(message))) }; return
        }
        busy = true; lock.unlock()
        queue.async {
            let result: Result<[String: Any], Error>
            do { try self.check(epoch); let value = try work(self); try self.check(epoch); result = .success(value) }
            catch { result = .failure(error) }
            self.lock.lock(); self.busy = false; self.lock.unlock()
            DispatchQueue.main.async { completion(result) }
        }
    }
    private func folder() throws -> URL {
        let value = FileManager.default.temporaryDirectory.appendingPathComponent("DeskWing-notebook-" + UUID().uuidString, isDirectory: true)
        try FileManager.default.createDirectory(at: value, withIntermediateDirectories: false, attributes: [.protectionKey: FileProtectionType.complete])
        return value
    }
    private func clearImport() {
        if let value = imported { imported = nil; try? FileManager.default.removeItem(at: value.folder) }
    }
    private func clearExport() {
        if let value = exporting { exporting = nil; try? value.handle?.close(); try? FileManager.default.removeItem(at: value.folder) }
    }
    func open(_ source: URL, epoch: UInt64) throws -> [String: Any] {
        guard imported == nil else { throw DeskError.message("Close the current PDF import before opening another.") }
        guard source.isFileURL else { throw DeskError.message("Choose a local PDF document.") }
        let access = source.startAccessingSecurityScopedResource()
        defer { if access { source.stopAccessingSecurityScopedResource() } }
        let owned = try folder(), destination = owned.appendingPathComponent("source.pdf")
        var retained = false
        defer { if !retained { try? FileManager.default.removeItem(at: owned) } }
        guard FileManager.default.createFile(atPath: destination.path, contents: nil, attributes: [.protectionKey: FileProtectionType.complete]) else { throw DeskError.message("The PDF could not be copied into private storage.") }
        let input = try FileHandle(forReadingFrom: source), output = try FileHandle(forWritingTo: destination)
        defer { try? input.close(); try? output.close() }
        var size = 0, prefix = Data()
        while let part = try input.read(upToCount: 65536), !part.isEmpty {
            try check(epoch); size += part.count
            guard size <= NotebookDocumentPolicy.maxImportBytes else { throw DeskError.message("Import a PDF smaller than 20 MB.") }
            if prefix.count < 5 { prefix.append(part.prefix(5 - prefix.count)) }
            try output.write(contentsOf: part)
        }
        try output.synchronize()
        guard NotebookDocumentPolicy.pdfHeader(prefix), let document = PDFDocument(url: destination), !document.isLocked,
              (1...NotebookDocumentPolicy.maxPages).contains(document.pageCount) else {
            throw DeskError.message("Choose an unlocked PDF with between 1 and 20 pages.")
        }
        try check(epoch)
        let id = UUID().uuidString.replacingOccurrences(of: "-", with: "").lowercased(), name = NotebookDocumentPolicy.name(source.lastPathComponent)
        imported = Imported(id: id, name: name, folder: owned, document: document); retained = true
        return ["id":id, "name":name, "pageCount":document.pageCount, "nativeRaster":true]
    }
    func render(_ id: String, page: Int, epoch: UInt64) throws -> [String: Any] {
        guard var value = imported, value.id == id, page >= 0, page < value.document.pageCount,
              let pdfPage = value.document.page(at: page) else { throw DeskError.message("Choose a page from the open PDF import.") }
        // A new render invalidates the old PNG handle before doing any expensive work.
        value.imageID = nil; value.image = Data(); imported = value
        guard let nativePage = pdfPage.pageRef else { throw DeskError.message("The selected PDF page could not be read.") }
        let bounds = nativePage.getBoxRect(.mediaBox), size = try NotebookDocumentPolicy.rasterSize(bounds, rotation: Int(nativePage.rotationAngle))
        let width = Int(size.width), height = Int(size.height)
        guard width <= 2048, height <= 2048, width * height <= 4_000_000 else { throw DeskError.message("This PDF page exceeds the notebook image pixel limit.") }
        guard let context = CGContext(data: nil, width: width, height: height, bitsPerComponent: 8, bytesPerRow: width * 4,
                                      space: CGColorSpaceCreateDeviceRGB(), bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue) else {
            throw DeskError.message("The PDF page image could not be created.")
        }
        // Unlike a UIKit thumbnail, this bitmap has an explicit pixel budget independent of screen scale.
        let destination = CGRect(x: 0, y: 0, width: CGFloat(width), height: CGFloat(height))
        context.setFillColor(CGColor(gray: 1, alpha: 1)); context.fill(destination)
        // Core Graphics applies the page's /Rotate and box origin once. Static annotation
        // appearances use the same page-space transform; no annotation action is activated.
        context.concatenate(nativePage.getDrawingTransform(.mediaBox, rect: destination, rotate: 0, preserveAspectRatio: true))
        context.drawPDFPage(nativePage)
        for annotation in pdfPage.annotations where annotation.shouldDisplay { annotation.draw(with: .mediaBox, in: context) }
        guard var raster = context.makeImage() else { throw DeskError.message("The PDF page could not be rendered.") }
        var bytes = try png(raster)
        while bytes.count > NotebookDocumentPolicy.maxPNGBytes {
            try check(epoch)
            let longest = max(raster.width, raster.height)
            guard longest > 256 else { throw DeskError.message("This PDF page cannot fit the notebook image limit.") }
            let nextSide = max(256, Int(Double(longest) * 0.7)), scale = Double(nextSide) / Double(longest)
            let width = max(1, Int(Double(raster.width) * scale)), height = max(1, Int(Double(raster.height) * scale))
            guard let context = CGContext(data: nil, width: width, height: height, bitsPerComponent: 8, bytesPerRow: width * 4,
                                          space: CGColorSpaceCreateDeviceRGB(), bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue) else { throw DeskError.message("The PDF page image could not be resized.") }
            context.setFillColor(CGColor(gray: 1, alpha: 1)); context.fill(CGRect(x: 0, y: 0, width: width, height: height)); context.interpolationQuality = .high
            context.draw(raster, in: CGRect(x: 0, y: 0, width: width, height: height))
            guard let resized = context.makeImage() else { throw DeskError.message("The PDF page image could not be resized.") }
            raster = resized; bytes = try png(raster)
        }
        try check(epoch)
        let imageID = UUID().uuidString.replacingOccurrences(of: "-", with: "").lowercased()
        value.imageID = imageID; value.image = bytes; imported = value
        return ["imageId":imageID, "width":raster.width, "height":raster.height, "sourcePage":page + 1, "size":bytes.count, "mime":"image/png"]
    }
    private func png(_ image: CGImage) throws -> Data {
        let output = NSMutableData()
        guard let destination = CGImageDestinationCreateWithData(output as CFMutableData, UTType.png.identifier as CFString, 1, nil) else { throw DeskError.message("The PDF image encoder is unavailable.") }
        CGImageDestinationAddImage(destination, image, nil)
        guard CGImageDestinationFinalize(destination) else { throw DeskError.message("The PDF image could not be encoded.") }
        return output as Data
    }
    func readImage(_ id: String, offset: Int, length: Int) throws -> [String: Any] {
        guard let value = imported, value.imageID == id, offset <= value.image.count else { throw DeskError.message("Render this PDF page again before reading its image.") }
        let end = min(value.image.count, offset + length)
        return ["dataBase64":value.image.subdata(in: offset..<end).base64EncodedString(), "eof":end == value.image.count]
    }
    func closeImport(_ id: String) throws -> [String: Any] {
        guard imported?.id == id else { throw DeskError.message("This PDF import is already closed.") }
        clearImport(); return ["closed":true]
    }
    func beginExport(name: String, size: Int) throws -> [String: Any] {
        guard exporting == nil else { throw DeskError.message("Finish or cancel the current notebook export first.") }
        let owned = try folder(), url = owned.appendingPathComponent(name)
        var retained = false
        defer { if !retained { try? FileManager.default.removeItem(at: owned) } }
        guard FileManager.default.createFile(atPath: url.path, contents: nil, attributes: [.protectionKey: FileProtectionType.complete]) else { throw DeskError.message("Private notebook export could not be created.") }
        let handle = try FileHandle(forWritingTo: url), id = UUID().uuidString.replacingOccurrences(of: "-", with: "").lowercased()
        exporting = Exporting(id: id, folder: owned, url: url, expected: size, handle: handle); retained = true
        return ["id":id]
    }
    func writeExport(_ id: String, offset: Int, bytes: Data) throws -> [String: Any] {
        guard var value = exporting, value.id == id, let handle = value.handle, value.written == offset,
              bytes.count <= value.expected - value.written else { throw DeskError.message("Notebook export chunks must arrive once, in order, within the declared size.") }
        if value.prefix.count < 5 { value.prefix.append(bytes.prefix(5 - value.prefix.count)) }
        if value.prefix.count == 5 && !NotebookDocumentPolicy.pdfHeader(value.prefix) { clearExport(); throw DeskError.message("Only a PDF document can be exported.") }
        do { try handle.write(contentsOf: bytes) }
        catch { clearExport(); throw DeskError.message("The PDF export could not be written. Start a new export.") }
        value.written += bytes.count; exporting = value
        return ["written":value.written]
    }
    func finishExport(_ id: String) throws -> [String: Any] {
        guard var value = exporting, value.id == id, let handle = value.handle, value.written == value.expected,
              NotebookDocumentPolicy.pdfHeader(value.prefix) else { throw DeskError.message("Complete every PDF export chunk before choosing a destination.") }
        do {
            try handle.synchronize(); try handle.close()
            let attributes = try FileManager.default.attributesOfItem(atPath: value.url.path)
            guard (attributes[.size] as? NSNumber)?.intValue == value.expected else { throw DeskError.message("The PDF export length did not match its declared size.") }
        } catch { clearExport(); throw DeskError.message("The PDF export could not be completed. Start a new export.") }
        value.handle = nil; exporting = value
        // The URL stays inside this native helper and is consumed by the picker callback.
        return ["ready":true, "url":value.url]
    }
    func cancelExport(_ id: String) throws -> [String: Any] {
        guard exporting?.id == id else { throw DeskError.message("This notebook export is already closed.") }
        clearExport(); return ["cancelled":true]
    }
}

/// User-initiated native PDF tools. The host routes only these exact POST paths and owns bridge tickets.
@MainActor
final class NotebookPdfImporter: NSObject, UIDocumentPickerDelegate, UIAdaptivePresentationControllerDelegate {
    typealias Reply = ([String: Any]) -> Void
    private weak var host: UIViewController?
    private let scope: () -> Int
    private let foreground: () -> Bool
    private let worker = NotebookPDFWorker()
    private var epoch: UInt64 = 0
    private var boundScope: Int
    private var closed = false
    private var workID: UUID?
    private var workReply: Reply?
    private var pending: Picker?
    private weak var picker: UIDocumentPickerViewController?
    private struct Picker {
        let token: UUID, epoch: UInt64, scope: Int, exportID: String?, reply: Reply
    }
    init(host: UIViewController, scope: @escaping () -> Int, foreground: @escaping () -> Bool) {
        self.host = host; self.scope = scope; self.foreground = foreground; boundScope = scope(); super.init()
    }
    deinit { worker.invalidate(UInt64.max) }
    nonisolated static func allowed(_ path: String) -> Bool { NotebookDocumentPolicy.paths.contains(path) }
    var isOpen: Bool { pending != nil }
    func request(path: String, body: [String: Any], completion: @escaping Reply) {
        if boundScope != scope() { reset(reason: "The tablet workspace changed.") }
        do {
            guard !closed, foreground(), let host, !host.isBeingDismissed else { throw DeskError.message("Open DeskWing before using notebook files.") }
            guard Self.allowed(path) else { throw DeskError.message("This notebook file request is not supported.") }
            guard pending == nil, workID == nil else { throw DeskError.message("Wait for the current notebook file operation.") }
            switch path {
            case "/local/notebook/import-pdf":
                try NotebookDocumentPolicy.exact(body, [])
                try presentImport(host, reply: completion)
            case "/local/notebook/render-pdf":
                try NotebookDocumentPolicy.exact(body, ["id","page"])
                let id = try NotebookDocumentPolicy.handle(body["id"]), page = try NotebookDocumentPolicy.integer(body["page"], minimum: 0, maximum: 19)
                perform(reply: completion) { try $0.render(id, page: page, epoch: $1) }
            case "/local/notebook/read-pdf-image":
                try NotebookDocumentPolicy.exact(body, ["id","offset","length"])
                let id = try NotebookDocumentPolicy.handle(body["id"]), offset = try NotebookDocumentPolicy.integer(body["offset"], minimum: 0, maximum: NotebookDocumentPolicy.maxPNGBytes)
                let length = try NotebookDocumentPolicy.integer(body["length"], minimum: 1, maximum: NotebookDocumentPolicy.maxChunkBytes)
                perform(reply: completion) { worker, _ in try worker.readImage(id, offset: offset, length: length) }
            case "/local/notebook/close-pdf":
                try NotebookDocumentPolicy.exact(body, ["id"]); let id = try NotebookDocumentPolicy.handle(body["id"])
                perform(reply: completion) { worker, _ in try worker.closeImport(id) }
            case "/local/notebook/export-pdf/start":
                try NotebookDocumentPolicy.exact(body, ["name","size"])
                let size = try NotebookDocumentPolicy.integer(body["size"], minimum: 5, maximum: NotebookDocumentPolicy.maxExportBytes), name = NotebookDocumentPolicy.name(body["name"])
                perform(reply: completion) { worker, _ in try worker.beginExport(name: name, size: size) }
            case "/local/notebook/export-pdf/write":
                try NotebookDocumentPolicy.exact(body, ["id","offset","dataBase64"])
                let id = try NotebookDocumentPolicy.handle(body["id"]), offset = try NotebookDocumentPolicy.integer(body["offset"], minimum: 0, maximum: NotebookDocumentPolicy.maxExportBytes)
                let bytes = try NotebookDocumentPolicy.chunk(body["dataBase64"])
                perform(reply: completion) { worker, _ in try worker.writeExport(id, offset: offset, bytes: bytes) }
            case "/local/notebook/export-pdf/finish":
                try NotebookDocumentPolicy.exact(body, ["id"]); let id = try NotebookDocumentPolicy.handle(body["id"])
                guard host.presentedViewController == nil else { throw DeskError.message("Close the open sheet before exporting your notebook.") }
                perform(reply: completion, finish: { [weak self] _, value, done in
                    guard let self else { return }
                    guard let url = value["url"] as? URL else { done(["error":"The notebook export could not be opened."]); return }
                    self.showExportPicker(id, url: url, host: host, reply: done)
                }) { worker, _ in try worker.finishExport(id) }
            case "/local/notebook/export-pdf/cancel":
                try NotebookDocumentPolicy.exact(body, ["id"]); let id = try NotebookDocumentPolicy.handle(body["id"])
                perform(reply: completion) { worker, _ in try worker.cancelExport(id) }
            default: throw DeskError.message("This notebook file request is not supported.")
            }
        } catch { completion(["error":error.localizedDescription]) }
    }
    private func perform(reply: @escaping Reply, finish: ((NotebookPDFWorker, [String: Any], @escaping Reply) -> Void)? = nil,
                         work: @escaping (NotebookPDFWorker, UInt64) throws -> [String: Any]) {
        let token = UUID(), submittedEpoch = epoch, submittedScope = scope(); workID = token; workReply = reply
        worker.run(submittedEpoch, work: { worker in try work(worker, submittedEpoch) }) { [weak self] result in
            guard let self, self.workID == token else { return }
            self.workID = nil; self.workReply = nil
            guard !self.closed, self.foreground(), submittedEpoch == self.epoch, submittedScope == self.scope() else {
                self.reset(reason: "The notebook file operation stopped when the workspace changed.")
                reply(["error":"The notebook file operation stopped when the workspace changed."]); return
            }
            switch result {
            case .success(let value): if let finish { finish(self.worker, value, reply) } else { reply(value) }
            case .failure(let error): reply(["error":error.localizedDescription])
            }
        }
    }
    private func presentImport(_ host: UIViewController, reply: @escaping Reply) throws {
        guard host.presentedViewController == nil else { throw DeskError.message("Close the open sheet before importing a PDF.") }
        let sheet = UIDocumentPickerViewController(forOpeningContentTypes: [.pdf], asCopy: false)
        sheet.allowsMultipleSelection = false; sheet.delegate = self
        pending = Picker(token: UUID(), epoch: epoch, scope: scope(), exportID: nil, reply: reply); picker = sheet
        host.present(sheet, animated: true) { [weak self, weak sheet] in sheet?.presentationController?.delegate = self }
    }
    private func showExportPicker(_ id: String, url: URL, host: UIViewController, reply: @escaping Reply) {
        guard host.presentedViewController == nil else { reply(["error":"Close the open sheet before exporting your notebook."]); return }
        let sheet = UIDocumentPickerViewController(forExporting: [url], asCopy: true); sheet.delegate = self
        pending = Picker(token: UUID(), epoch: epoch, scope: scope(), exportID: id, reply: reply); picker = sheet
        host.present(sheet, animated: true) { [weak self, weak sheet] in sheet?.presentationController?.delegate = self }
    }
    func documentPicker(_ controller: UIDocumentPickerViewController, didPickDocumentsAt urls: [URL]) {
        guard controller === picker, let current = pending else { return }
        pending = nil; picker = nil
        guard !closed, current.epoch == epoch, current.scope == scope(), foreground() else {
            reset(reason: "The notebook file picker changed.")
            current.reply(["error":"The notebook file picker changed.","cancelled":true]); return
        }
        if let id = current.exportID {
            perform(reply: { _ in current.reply(["saved":true,"cancelled":false]) }) { worker, _ in try worker.cancelExport(id) }
            return
        }
        guard urls.count == 1, let source = urls.first else { current.reply(["error":"Choose one PDF document."]); return }
        perform(reply: current.reply) { try $0.open(source, epoch: $1) }
    }
    func documentPickerWasCancelled(_ controller: UIDocumentPickerViewController) { cancelPicker(controller) }
    func presentationControllerDidDismiss(_ presentationController: UIPresentationController) {
        if let sheet = presentationController.presentedViewController as? UIDocumentPickerViewController { cancelPicker(sheet) }
    }
    private func cancelPicker(_ controller: UIDocumentPickerViewController) {
        guard controller === picker, let current = pending else { return }; pending = nil; picker = nil
        if let id = current.exportID { perform(reply: { _ in current.reply(["saved":false,"cancelled":true]) }) { worker, _ in try worker.cancelExport(id) } }
        else { current.reply(["cancelled":true]) }
    }
    func reset(reason: String = "The notebook file operation was cancelled.") {
        epoch &+= 1; boundScope = scope(); workID = nil
        let activeReply = workReply; workReply = nil
        let current = pending, sheet = picker; pending = nil; picker = nil
        sheet?.dismiss(animated: false); worker.invalidate(epoch)
        current?.reply(["error":reason,"cancelled":true])
        activeReply?(["error":reason,"cancelled":true])
    }
    func pause() { reset(reason: "Notebook files closed while DeskWing was in the background.") }
    func close() { closed = true; reset(reason: "The notebook workspace closed.") }
}
