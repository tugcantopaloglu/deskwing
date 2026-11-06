import Foundation
import WebKit

final class LocalAssetHandler: NSObject, WKURLSchemeHandler {
    private let root: URL
    private let allowed: Set<String>
    init(bundle: Bundle = .main) throws {
        guard let root = bundle.url(forResource: "www", withExtension: nil),
              let bytes = try? Data(contentsOf: root.appendingPathComponent("asset-list.json")),
              let names = try? JSONDecoder().decode([String].self, from: bytes), names.contains("index.html") else {
            throw DeskError.message("Tablet assets are missing. Run ios/sync-assets.py and rebuild DeskWing.")
        }
        self.root = root; allowed = Set(names); super.init()
    }
    func webView(_ webView: WKWebView, start urlSchemeTask: WKURLSchemeTask) {
        guard let url = urlSchemeTask.request.url, url.scheme == "deskwing", url.host == "app",
              url.port == nil, url.user == nil, url.password == nil, url.query == nil,
              urlSchemeTask.request.httpMethod == "GET" else { fail(urlSchemeTask); return }
        let path = String(url.path.dropFirst())
        guard allowed.contains(path), !path.contains(".."), !path.contains("\\"), !path.hasPrefix("/"),
              let data = try? Data(contentsOf: root.appendingPathComponent(path)), data.count <= 12 * 1024 * 1024 else {
            fail(urlSchemeTask); return
        }
        let ext = url.pathExtension.lowercased()
        let mime = ["html":"text/html", "js":"application/javascript", "mjs":"text/javascript", "css":"text/css", "ttf":"font/ttf",
                    "woff2":"font/woff2", "svg":"image/svg+xml", "png":"image/png", "json":"application/json"][ext] ?? "application/octet-stream"
        let headers = ["Content-Type": mime + (["html","js","mjs","css","json","svg"].contains(ext) ? "; charset=utf-8" : ""),
            "Content-Security-Policy": "default-src 'none'; script-src 'self'; style-src 'self'; img-src 'self' data: blob:; font-src 'self'; connect-src 'none'; frame-src 'none'; base-uri 'none'; form-action 'none'",
            "X-Content-Type-Options": "nosniff", "Cache-Control": "no-store"]
        if let response = HTTPURLResponse(url: url, statusCode: 200, httpVersion: "HTTP/1.1", headerFields: headers) {
            urlSchemeTask.didReceive(response); urlSchemeTask.didReceive(data); urlSchemeTask.didFinish()
        } else { fail(urlSchemeTask) }
    }
    func webView(_ webView: WKWebView, stop urlSchemeTask: WKURLSchemeTask) { /* Synchronous bundle reads never leave pending tasks. */ }
    private func fail(_ task: WKURLSchemeTask) { task.didFailWithError(URLError(.resourceUnavailable)) }
}
