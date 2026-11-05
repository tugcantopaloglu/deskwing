import UIKit

/// One user intent, one server lease and one video connection. Ending an old intent cannot affect
/// a replacement; a late start reply is released using that reply's exact original lease and PC.
final class NativeDisplaySession {
    let id: String
    private let pc: PairedPC
    private let control: PinnedClient
    private let requestedRefresh: Int
    private let measuredRefresh: Int
    private let closed: (String, String) -> Void
    private var lease: String?
    private var viewer: DisplayVideoController?
    private var connection: DisplayVideoConnection?
    private var stopped = false
    private var beginning = false
    private var retiringControl = false

    init(id: String, pc: PairedPC, refresh: Int, measuredRefresh: Int, closed: @escaping (String, String) -> Void) {
        self.id = id; self.pc = pc; requestedRefresh = refresh; self.measuredRefresh = measuredRefresh; self.closed = closed
        control = PinnedClient(host: pc.host, port: pc.port, pin: pc.fingerprint, token: pc.token, displayControlOnly: true)
    }
    func start(presenter: UIViewController, ready: @escaping (Result<[String: Any], Error>) -> Void) {
        guard !stopped, !beginning, lease == nil else { ready(.failure(DeskError.message("This display session has already been used."))); return }
        beginning = true
        do {
            try control.send(path: "/api/computer/display/stream/start", method: "POST", body: ["refreshRate":requestedRefresh]) { [self, weak presenter] result in
                beginning = false
                defer { if stopped { retireControl() } }
                do {
                    let response = try result.get()
                    let (newLease, format, source) = try DisplayVideoPolicy.begin(response, requestedRefreshRate: requestedRefresh)
                    lease = newLease
                    guard !stopped, let presenter, presenter.presentedViewController == nil,
                          presenter.viewIfLoaded?.window != nil, UIApplication.shared.applicationState == .active else {
                        releaseLease(); ready(.failure(DeskError.message("Display start stopped when the app or session changed.")))
                        stop(reason: "Display start stopped when the app or session changed."); return
                    }
                    let screen = DisplayVideoController(format: format, sourceRefresh: source) { [weak self] reason in self?.stop(reason: reason) }
                    viewer = screen
                    let inbox = screen.inbox
                    let stream = DisplayVideoConnection(pc: pc, lease: newLease, format: format,
                        receive: { unit in inbox.offer(unit) }, finish: { [weak self] reason in self?.stop(reason: reason) })
                    connection = stream
                    presenter.present(screen, animated: false)
                    try stream.start()
                    ready(.success(["started":true, "targetRefreshRate":format.refreshRate,
                                    "currentRefreshRate":measuredRefresh, "session":id]))
                } catch {
                    ready(.failure(error))
                    stop(reason: error.localizedDescription)
                }
            }
        } catch { beginning = false; ready(.failure(error)); stop(reason: error.localizedDescription) }
    }
    func stop(reason: String) {
        guard !stopped else { return }; stopped = true
        connection?.stop(); connection = nil
        viewer?.stop(); viewer?.dismiss(animated: false); viewer = nil
        releaseLease()
        closed(id, reason)
        if !beginning { retireControl() }
    }
    private func releaseLease() {
        guard let lease else { return }; self.lease = nil
        try? control.send(path: "/api/computer/display/stream/stop", method: "POST", body: ["lease":lease]) { _ in }
    }
    private func retireControl() {
        guard !retiringControl else { return }; retiringControl = true
        let control = control
        DispatchQueue.main.asyncAfter(deadline: .now() + 3) { control.invalidate() }
    }
}
