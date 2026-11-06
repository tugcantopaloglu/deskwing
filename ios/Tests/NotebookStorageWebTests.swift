import XCTest
import UIKit
import WebKit
@testable import DeskWing

/// These use a registered deskwing://app origin and a fresh ephemeral data store, never app/user storage.
final class NotebookStorageWebTests: XCTestCase {
    @MainActor
    func testRegisteredDeskWingOriginIsSecureAndSupportsIndexedDBRoundTrip() async throws {
        let fixture = try await openFixture(includeStore: false)
        defer { fixture.close() }
        let name = "DeskWingNotebookFixture-" + UUID().uuidString
        let result = try await evaluate(fixture.web, body: #"""
        let database = null, expired = false;
        let timer;
        const cleanup = async () => {
            if (database) { database.close(); database = null; }
            await new Promise(resolve => {
                try {
                    const deletion = indexedDB.deleteDatabase(databaseName);
                    deletion.onsuccess = deletion.onerror = deletion.onblocked = () => resolve();
                } catch { resolve(); }
                setTimeout(resolve, 250);
            });
        };
        try {
            const work = new Promise((resolve, reject) => {
                let opening;
                try { opening = indexedDB.open(databaseName, 1); }
                catch (error) { reject(error); return; }
                opening.onupgradeneeded = () => opening.result.createObjectStore('items');
                opening.onerror = () => reject(opening.error || Error('IndexedDB open failed'));
                opening.onblocked = () => reject(Error('Isolated IndexedDB open was blocked'));
                opening.onsuccess = () => {
                    database = opening.result;
                    if (expired) { database.close(); database = null; return; }
                    const write = database.transaction('items', 'readwrite');
                    write.objectStore('items').put({text:'paired notebook fixture',revision:1}, 'page');
                    write.onerror = write.onabort = () => reject(write.error || Error('IndexedDB write failed'));
                    write.oncomplete = () => {
                        const read = database.transaction('items', 'readonly');
                        let value;
                        const request = read.objectStore('items').get('page');
                        request.onsuccess = () => { value = request.result; };
                        read.onerror = read.onabort = () => reject(read.error || Error('IndexedDB read failed'));
                        read.oncomplete = () => resolve({secure:window.isSecureContext, protocol:location.protocol, host:location.hostname, origin:location.origin, value});
                    };
                };
            });
            return await Promise.race([work, new Promise((_, reject) => { timer = setTimeout(() => { expired = true; reject(Error('IndexedDB fixture exceeded eight seconds')); }, 8000); })]);
        } finally { clearTimeout(timer); await cleanup(); }
        """#, arguments: ["databaseName":name])
        XCTAssertEqual(result["protocol"] as? String, "deskwing:")
        XCTAssertEqual(result["host"] as? String, "app")
        XCTAssertEqual(result["secure"] as? Bool, true, "Registered local assets must run in a secure context.")
        let value = try XCTUnwrap(result["value"] as? [String:Any])
        XCTAssertEqual(value["text"] as? String, "paired notebook fixture")
        XCTAssertEqual(value["revision"] as? Int, 1)
    }

    @MainActor
    func testBundledNotebookStoreKeepsWholePageBundlesAndRejectsStaleRevisionsOnDeskWingOrigin() async throws {
        let fixture = try await openFixture(includeStore: true)
        defer { fixture.close() }
        let ids = (0..<3).map { _ in UUID().uuidString.replacingOccurrences(of:"-",with:"").lowercased() }
        let key = "deskwing.notebooks.page.v1:" + ids.joined(separator:":")
        let result = try await evaluate(fixture.web, body: #"""
        const store = window.DeskNotebookStore;
        if (!store) throw Error('Bundled notebook store was not loaded');
        let timer;
        const cleanup = () => new Promise(resolve => {
            try {
                // This is the production module's database inside this test's ephemeral data store.
                // Its onversionchange handler closes the connection before deletion completes.
                const request = indexedDB.deleteDatabase('DeskWingNotebooks');
                request.onsuccess = request.onerror = request.onblocked = () => resolve();
            } catch { resolve(); }
            setTimeout(resolve, 250);
        });
        try {
            const work = async () => {
                const mode = await store.ready();
                if (mode !== 'indexeddb') throw Error('Notebook storage fell back to limited local storage on deskwing://app');
                const before = await store.getPage(pageKey);
                const document = {version:1,strokes:[],notes:'native custom-origin notebook',penOnly:true,updatedAt:'2026-11-06T00:00:00.000Z'};
                const saved = await store.putPage(pageKey,{document,sync:{revision:'fixture'},recovery:{notes:'kept recovery'},background:null},before.localRevision);
                let staleRejected = false;
                try { await store.putPage(pageKey,{document:{...document,notes:'must not replace'}},before.localRevision); }
                catch { staleRejected = true; }
                const after = await store.getPage(pageKey);
                return {secure:window.isSecureContext,mode,atomic:store.atomicPageWrites,initial:before.localRevision,saved:saved.localRevision,after,staleRejected};
            };
            return await Promise.race([work(),new Promise((_,reject) => { timer=setTimeout(() => reject(Error('Bundled notebook fixture exceeded nine seconds')),9000); })]);
        } finally { clearTimeout(timer); await cleanup(); }
        """#, arguments: ["pageKey":key])
        XCTAssertEqual(result["secure"] as? Bool,true)
        XCTAssertEqual(result["mode"] as? String,"indexeddb")
        XCTAssertEqual(result["atomic"] as? Bool,true)
        XCTAssertEqual(result["initial"] as? Int,0)
        XCTAssertEqual(result["saved"] as? Int,1)
        XCTAssertEqual(result["staleRejected"] as? Bool,true)
        let after = try XCTUnwrap(result["after"] as? [String:Any])
        XCTAssertEqual(after["localRevision"] as? Int,1)
        XCTAssertEqual((after["document"] as? [String:Any])?["notes"] as? String,"native custom-origin notebook")
        XCTAssertEqual((after["recovery"] as? [String:Any])?["notes"] as? String,"kept recovery")
        XCTAssertEqual((after["sync"] as? [String:Any])?["revision"] as? String,"fixture")
    }

    /// The optional-completion overload returns Void; explicitly await its real Promise result.
    @MainActor
    private func evaluate(_ web:WKWebView, body:String, arguments:[String:Any]) async throws -> [String:Any] {
        try await withCheckedThrowingContinuation { (continuation:CheckedContinuation<[String:Any],Error>) in
            var completed=false
            let deadline=DispatchWorkItem {
                guard !completed else { return }; completed=true
                continuation.resume(throwing:DeskError.message("The isolated notebook JavaScript operation exceeded ten seconds."))
            }
            DispatchQueue.main.asyncAfter(deadline:.now()+10,execute:deadline)
            web.callAsyncJavaScript(body,arguments:arguments,in:nil,in:.page,completionHandler:{ response in
                guard !completed else { return }; completed=true; deadline.cancel()
                switch response {
                case .failure(let error): continuation.resume(throwing:error)
                case .success(let value):
                    guard let result=value as? [String:Any] else {
                        continuation.resume(throwing:DeskError.message("The notebook JavaScript fixture returned \(String(reflecting:type(of:value))) instead of its result object.")); return
                    }
                    continuation.resume(returning:result)
                }
            })
        }
    }

    @MainActor
    private func openFixture(includeStore:Bool) async throws -> NotebookStorageFixture {
        let previous = UIApplication.shared.windows.first(where: \.isKeyWindow)
        let configuration = WKWebViewConfiguration()
        configuration.websiteDataStore = .nonPersistent()
        configuration.preferences.javaScriptCanOpenWindowsAutomatically = false
        let handler = try NotebookStorageSchemeHandler(includeStore:includeStore)
        configuration.setURLSchemeHandler(handler, forURLScheme:"deskwing")
        let web = WKWebView(frame:UIScreen.main.bounds,configuration:configuration)
        let window = UIWindow(frame:UIScreen.main.bounds), host=UIViewController()
        window.rootViewController=host; host.view.addSubview(web); window.makeKeyAndVisible()
        let loaded=expectation(description:"Isolated deskwing notebook document loaded")
        var navigationError:Error?, navigationCompleted=false
        let navigation=NotebookStorageNavigation { error in navigationError=error; navigationCompleted=true; loaded.fulfill() }
        web.navigationDelegate=navigation
        let fixture=NotebookStorageFixture(web:web,window:window,previous:previous,navigation:navigation,handler:handler)
        web.load(URLRequest(url:try XCTUnwrap(URL(string:"deskwing://app/notebook-storage-test.html"))))
        await fulfillment(of:[loaded],timeout:10)
        guard navigationCompleted else { fixture.close(); throw DeskError.message("The isolated notebook web document did not load in time.") }
        if let navigationError { fixture.close(); throw navigationError }
        return fixture
    }
}

@MainActor
private final class NotebookStorageFixture {
    let web:WKWebView
    private let window:UIWindow
    private weak var previous:UIWindow?
    private let navigation:NotebookStorageNavigation
    private let handler:NotebookStorageSchemeHandler
    init(web:WKWebView,window:UIWindow,previous:UIWindow?,navigation:NotebookStorageNavigation,handler:NotebookStorageSchemeHandler) {
        self.web=web; self.window=window; self.previous=previous; self.navigation=navigation; self.handler=handler
    }
    func close() {
        web.stopLoading(); web.navigationDelegate=nil; web.removeFromSuperview(); window.isHidden=true; window.rootViewController=nil; previous?.makeKeyAndVisible()
    }
}

private final class NotebookStorageSchemeHandler:NSObject,WKURLSchemeHandler {
    private let html:Data
    private let store:Data?
    init(includeStore:Bool) throws {
        html=Data(("<!doctype html><html><head><meta charset=\"utf-8\">" + (includeStore ? "<script src=\"notebook-store.js\"></script>" : "") + "</head><body>Isolated notebook storage fixture</body></html>").utf8)
        if includeStore {
            guard let root=Bundle.main.url(forResource:"www",withExtension:nil) else { throw DeskError.message("Bundled tablet assets are missing from the iOS test host.") }
            let bytes=try Data(contentsOf:root.appendingPathComponent("notebook-store.js"))
            guard !bytes.isEmpty,bytes.count<256*1024 else { throw DeskError.message("The bundled notebook store module is missing or oversized.") }
            store=bytes
        } else { store=nil }
        super.init()
    }
    func webView(_ webView:WKWebView,start urlSchemeTask:WKURLSchemeTask) {
        guard let url=urlSchemeTask.request.url,url.scheme=="deskwing",url.host=="app",url.port==nil,url.user==nil,url.password==nil,url.query==nil,url.fragment==nil,urlSchemeTask.request.httpMethod=="GET" else { urlSchemeTask.didFailWithError(URLError(.unsupportedURL));return }
        let data:Data,mime:String
        if url.path=="/notebook-storage-test.html" { data=html;mime="text/html" }
        else if url.path=="/notebook-store.js",let store { data=store;mime="application/javascript" }
        else { urlSchemeTask.didFailWithError(URLError(.resourceUnavailable));return }
        let headers=["Content-Type":mime+"; charset=utf-8","Content-Security-Policy":"default-src 'none'; script-src 'self'; connect-src 'none'; object-src 'none'; base-uri 'none'","X-Content-Type-Options":"nosniff","Cache-Control":"no-store"]
        guard let response=HTTPURLResponse(url:url,statusCode:200,httpVersion:"HTTP/1.1",headerFields:headers) else { urlSchemeTask.didFailWithError(URLError(.badServerResponse));return }
        urlSchemeTask.didReceive(response);urlSchemeTask.didReceive(data);urlSchemeTask.didFinish()
    }
    func webView(_ webView:WKWebView,stop urlSchemeTask:WKURLSchemeTask) { /* Fixed bundle responses complete synchronously. */ }
}

@MainActor
private final class NotebookStorageNavigation:NSObject,WKNavigationDelegate {
    private var finished=false
    private let completion:(Error?)->Void
    init(_ completion:@escaping(Error?)->Void) { self.completion=completion }
    private func finish(_ error:Error?) { guard !finished else{return};finished=true;completion(error) }
    func webView(_ webView:WKWebView,didFinish navigation:WKNavigation!) { finish(nil) }
    func webView(_ webView:WKWebView,didFail navigation:WKNavigation!,withError error:Error) { finish(error) }
    func webView(_ webView:WKWebView,didFailProvisionalNavigation navigation:WKNavigation!,withError error:Error) { finish(error) }
}
