import UIKit

/// An explicit native-mode choice owns one pen lease. There is no input before that choice,
/// no retry/replay, and a slow or failed packet retires that exact lease.
final class DisplayInputController {
    private let videoLease: String
    private let format: DisplayVideoFormat
    private let client: PinnedClient
    private let changed: (DisplayNativeInputMode,String) -> Void
    private var mode = DisplayNativeInputMode.view
    private var intent = 0
    private var stopped = false
    private var retiringClient = false
    private var beginning = false
    private var lease: String?
    private var startedAt: TimeInterval = 0
    private var sequence = 0
    private var pending=DisplayInputQueue()
    private var inFlight: UUID?
    private var task: URLSessionDataTask?
    private var timer: Timer?
    private var sentAt: TimeInterval = 0
    private var batchLimit = DisplayInputPolicy.maximumBatch

    init(pc: PairedPC, videoLease: String, format: DisplayVideoFormat,
         changed: @escaping (DisplayNativeInputMode,String) -> Void) {
        self.videoLease=videoLease; self.format=format; self.changed=changed
        client=PinnedClient(host:pc.host,port:pc.port,pin:pc.fingerprint,token:pc.token,displayInputOnly:true)
    }

    func choose(_ selected: DisplayNativeInputMode) {
        guard !stopped else { return }
        guard !beginning || selected == .view else { changed(.view,"Wait for the current input permission request to finish."); return }
        retireInput(); mode = .view
        guard selected != .view else { changed(.view,"Viewing only · PC input is off"); return }
        intent += 1; let epoch=intent
        beginning=true; startedAt=ProcessInfo.processInfo.systemUptime
        changed(.view,"Requesting PC input permission…")
        do {
            try client.send(path:DisplayInputPolicy.path,method:"POST",body:[
                "operation":"begin","videoLease":videoLease,"session":UUID().uuidString,"mode":selected.rawValue
            ]) { [self] result in
                self.beginning=false
                defer { if self.stopped { self.retireClient() } }
                do {
                    let reply=try result.get()
                    guard let token=DisplayInputPolicy.lease(reply["lease"]),
                          let maximum=DisplayInputPolicy.integer(reply["maxBatch"]), (1...32).contains(maximum),
                          DisplayInputPolicy.integer(reply["expiresInMs"]) == 1200,
                          let target=reply["target"] as? [String:Any],
                          DisplayInputPolicy.integer(target["width"]) == self.format.width,
                          DisplayInputPolicy.integer(target["height"]) == self.format.height else {
                        if let token=DisplayInputPolicy.lease(reply["lease"]) { self.release(token) }
                        throw DeskError.message("The PC returned an invalid input target. Start a fresh display session.")
                    }
                    guard !self.stopped, epoch == self.intent, UIApplication.shared.applicationState == .active else {
                        self.release(token); return
                    }
                    self.lease=token; self.mode=selected; self.sequence=0; self.pending.reset(); self.batchLimit=maximum
                    self.sentAt=ProcessInfo.processInfo.systemUptime
                    let timer=Timer(timeInterval:0.008,repeats:true) { [weak self] _ in self?.flush() }
                    self.timer=timer; RunLoop.main.add(timer,forMode:.common)
                    self.changed(selected,selected == .pen ? "Pen enabled · fingers are ignored" : "Touch enabled · one finger acts as a pen")
                } catch {
                    if !self.stopped, epoch == self.intent { self.fail(error.localizedDescription) }
                }
            }
        } catch { beginning=false; fail(error.localizedDescription) }
    }

    func offer(_ sample: DisplayInputSample) {
        guard !stopped, mode != .view, lease != nil else { return }
        guard pending.offer(sample,coalescingAfter:batchLimit) else { fail("Input fell behind. Choose Pen or Touch again when the connection is ready."); return }
    }

    private func flush() {
        guard !stopped, mode != .view, let token=lease, inFlight == nil else { return }
        let now=ProcessInfo.processInfo.systemUptime
        guard !pending.isEmpty || now-sentAt >= 0.35 else { return }
        let samples=pending.take(batchLimit)
        sequence += 1; let ticket=UUID(), epoch=intent
        inFlight=ticket; sentAt=now
        let body: [String:Any] = ["operation":samples.isEmpty ? "renew" : "samples", "videoLease":videoLease,
            "lease":token,"sequence":sequence,"elapsedMs":max(0,(now-startedAt)*1000),"samples":samples.map(\.json)]
        do {
            task=try client.send(path:DisplayInputPolicy.path,method:"POST",body:body) { [weak self] result in
                guard let self, self.inFlight == ticket, self.intent == epoch else { return }
                self.inFlight=nil; self.task=nil
                if case .failure(let error)=result { self.fail(error.localizedDescription) }
            }
            DispatchQueue.main.asyncAfter(deadline:.now()+0.7) { [weak self] in
                guard let self, self.inFlight == ticket, self.intent == epoch else { return }
                self.fail("Input confirmation timed out. Choose Pen or Touch again; queued input was discarded.")
            }
        } catch { fail(error.localizedDescription) }
    }

    private func fail(_ reason: String) {
        retireInput(); mode = .view
        changed(.view,"Input stopped · "+String(reason.prefix(240)))
    }
    private func retireInput() {
        intent += 1; timer?.invalidate(); timer=nil; pending.reset(); inFlight=nil; task?.cancel(); task=nil
        if let token=lease { lease=nil; release(token) }
    }
    private func release(_ token: String) {
        try? client.send(path:DisplayInputPolicy.path,method:"POST",body:["operation":"cancel","videoLease":videoLease,"lease":token]) { _ in }
    }
    func stop() {
        guard !stopped else { return }; stopped=true; retireInput(); mode = .view
        if !beginning { retireClient() }
    }
    private func retireClient() {
        guard !retiringClient else { return }; retiringClient=true
        let client=client
        DispatchQueue.main.asyncAfter(deadline:.now()+2) { client.invalidate() }
    }
    deinit { timer?.invalidate(); task?.cancel() }
}

/// Coalesced Pencil touches stay independent of palm contacts. Direct touch is an explicit,
/// single-contact mode; another finger cancels the stroke until all contacts have lifted.
final class DisplayInputSurface: UIView {
    var mode=DisplayNativeInputMode.view { didSet { cancelContact(); blocked=false } }
    let imageSize: CGSize
    var send: ((DisplayInputSample) -> Void)?
    private var active: UITouch?
    private var lastPoint: DisplayInputPoint?
    private var blocked=false
    private var priorSize=CGSize.zero
    init(imageSize: CGSize) {
        self.imageSize=imageSize; super.init(frame:.zero)
        backgroundColor = .clear; isOpaque=false; isMultipleTouchEnabled=true; accessibilityLabel="Shared Windows display input"
    }
    required init?(coder: NSCoder) { return nil }
    override func layoutSubviews() {
        super.layoutSubviews()
        if priorSize != .zero, priorSize != bounds.size {
            let held=active != nil
            cancelContact(); if held { blocked=true }
        }
        priorSize=bounds.size
    }
    private func eligible(_ touch: UITouch) -> Bool {
        DisplayInputPolicy.accepts(mode:mode,pencil:touch.type == .pencil,direct:touch.type == .direct)
    }
    override func touchesBegan(_ touches: Set<UITouch>, with event: UIEvent?) {
        guard mode != .view else { return }
        let matching=touches.filter(eligible)
        if mode == .touch {
            let direct=event?.allTouches?.filter { $0.type == .direct && ![UITouch.Phase.ended,.cancelled].contains($0.phase) } ?? []
            if direct.count > 1 { cancelContact(); blocked=true; return }
        }
        guard !blocked, active == nil, matching.count == 1, let touch=matching.first else { return }
        guard emit(touch,phase:"down") else { blocked=true; return }; active=touch
    }
    override func touchesMoved(_ touches: Set<UITouch>, with event: UIEvent?) {
        guard let active, touches.contains(active), !blocked else { return }
        for touch in event?.coalescedTouches(for:active) ?? [active] {
            if !emit(touch,phase:"move") { cancelContact(); blocked=true; break }
        }
    }
    override func touchesEnded(_ touches: Set<UITouch>, with event: UIEvent?) {
        if let active, touches.contains(active) {
            if !emit(active,phase:"up") { cancelContact() }
            self.active=nil; lastPoint=nil
        }
        unblockIfLifted(event)
    }
    override func touchesCancelled(_ touches: Set<UITouch>, with event: UIEvent?) {
        if let active, touches.contains(active) { cancelContact() }
        unblockIfLifted(event)
    }
    private func unblockIfLifted(_ event: UIEvent?) {
        let held=event?.allTouches?.contains { ![UITouch.Phase.ended,.cancelled].contains($0.phase) && eligible($0) } ?? false
        if !held { blocked=false }
    }
    private func emit(_ touch: UITouch, phase: String) -> Bool {
        guard let point=DisplayInputPolicy.point(touch.location(in:self),viewport:bounds.size,image:imageSize),
              let sample=DisplayInputPolicy.sample(phase:phase,point:point,force:Double(touch.force),maximumForce:Double(touch.maximumPossibleForce),
                  altitude:Double(touch.altitudeAngle),azimuth:Double(touch.azimuthAngle(in:self)),mode:mode) else { return false }
        lastPoint=point; send?(sample); return true
    }
    func cancelContact() {
        if active != nil, let point=lastPoint {
            send?(DisplayInputSample(phase:"up",point:point,pressure:0,tiltX:0,tiltY:0))
            send?(DisplayInputSample(phase:"leave",point:point,pressure:0,tiltX:0,tiltY:0))
        }
        active=nil; lastPoint=nil
    }
}
