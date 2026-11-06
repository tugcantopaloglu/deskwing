import UIKit
import AVFoundation
import QuartzCore

/// The transport may outpace rendering, but cannot build an unbounded main-thread work queue.
/// Dropping dependent H.264 frames requires a decoder flush and a fresh IDR, not the latest P-frame.
final class DisplayFrameInbox {
    private let lock = NSLock()
    private var units: [DisplayVideoUnit] = []
    private var bytes = 0
    private var stopped = false
    private var resync = false
    private var waitingForKeyframe = false
    private var received = 0
    private var dropped = 0
    private var heartbeatPending = false
    func noteHeartbeat() { lock.lock(); if !stopped { heartbeatPending = true }; lock.unlock() }
    func takeHeartbeat() -> Bool { lock.lock(); defer { lock.unlock() }; let value = heartbeatPending; heartbeatPending = false; return value }
    func offer(_ unit: DisplayVideoUnit) {
        let picture = DisplayH264.containsPicture(unit.bytes)
        lock.lock(); defer { lock.unlock() }
        guard !stopped else { return }
        if picture { received += 1 }
        if waitingForKeyframe && !unit.keyframe {
            if unit.configuration { units = [unit]; bytes = unit.bytes.count }
            else { dropped += 1 }
            return
        }
        if units.count >= 3 || bytes + unit.bytes.count > 4 * 1024 * 1024 {
            dropped += units.count; units.removeAll(keepingCapacity: true); bytes = 0; resync = true; waitingForKeyframe = true
            guard unit.keyframe else {
                if unit.configuration { units = [unit]; bytes = unit.bytes.count }
                else { dropped += 1 }
                return
            }
        }
        if unit.keyframe { waitingForKeyframe = false }
        units.append(unit); bytes += unit.bytes.count
    }
    func take() -> ([DisplayVideoUnit], Bool, Int, Int) {
        lock.lock(); defer { lock.unlock() }
        let result = (units, resync, received, dropped)
        units.removeAll(keepingCapacity: true); bytes = 0; resync = false; received = 0; dropped = 0
        return result
    }
    func waitForKeyframe() {
        lock.lock(); dropped += units.count; units.removeAll(keepingCapacity: true); bytes = 0; waitingForKeyframe = true; resync = true; lock.unlock()
    }
    func stop() { lock.lock(); stopped = true; units.removeAll(); bytes = 0; heartbeatPending = false; lock.unlock() }
}

/// A live connection is not proof that its dependent video frames can be decoded. Each initial
/// image/recovery wait has one absolute budget; heartbeats and repeated resets cannot renew it.
struct DisplayKeyframeDeadline {
    private var waitingSince: CFTimeInterval?
    private(set) var receivedKeyframe = false
    var recovering: Bool { receivedKeyframe && waitingSince != nil }
    mutating func begin(at now: CFTimeInterval) {
        if !receivedKeyframe { wait(at: now) }
    }
    mutating func wait(at now: CFTimeInterval) {
        if waitingSince == nil { waitingSince = now }
    }
    mutating func acceptKeyframe(at now: CFTimeInterval) throws {
        try check(at: now)
        receivedKeyframe = true; waitingSince = nil
    }
    func check(at now: CFTimeInterval) throws {
        guard let waitingSince, now - waitingSince >= 5 else { return }
        throw DeskError.message(receivedKeyframe
            ? "The display could not recover after frames were dropped. Start the display again."
            : "The PC did not send the first display image in time. Start the display again.")
    }
}

enum DisplayH264Format {
    static func make(sps: Data, pps: Data, expected: DisplayVideoFormat) throws -> CMVideoFormatDescription {
        guard !sps.isEmpty, !pps.isEmpty, sps.count <= 64 * 1024, pps.count <= 64 * 1024,
              sps[sps.startIndex] & 0x1F == 7, pps[pps.startIndex] & 0x1F == 8 else {
            throw DeskError.message("The H.264 configuration is invalid.")
        }
        var result: CMVideoFormatDescription?
        let status = sps.withUnsafeBytes { spsBytes in
            pps.withUnsafeBytes { ppsBytes -> OSStatus in
                guard let spsPointer = spsBytes.bindMemory(to: UInt8.self).baseAddress,
                      let ppsPointer = ppsBytes.bindMemory(to: UInt8.self).baseAddress else { return -1 }
                var pointers: [UnsafePointer<UInt8>] = [spsPointer, ppsPointer]
                var sizes = [sps.count, pps.count]
                return CMVideoFormatDescriptionCreateFromH264ParameterSets(allocator: kCFAllocatorDefault,
                    parameterSetCount: 2, parameterSetPointers: &pointers, parameterSetSizes: &sizes,
                    nalUnitHeaderLength: 4, formatDescriptionOut: &result)
            }
        }
        guard status == noErr, let result else { throw DeskError.message("The PC's H.264 video format is unsupported.") }
        let dimensions = CMVideoFormatDescriptionGetDimensions(result)
        let presentation = CMVideoFormatDescriptionGetPresentationDimensions(result, usePixelAspectRatio: false, useCleanAperture: true)
        // AVC may encode 900/1080 visible rows as 912/1088 macroblock-aligned rows. Accept only
        // that small padding, with an exact visible aperture; cropping cannot disguise a huge allocation.
        guard (expected.width...((expected.width + 15) / 16 * 16)).contains(Int(dimensions.width)),
              (expected.height...((expected.height + 15) / 16 * 16)).contains(Int(dimensions.height)),
              abs(presentation.width - CGFloat(expected.width)) < 0.01,
              abs(presentation.height - CGFloat(expected.height)) < 0.01 else { throw DeskError.message("The video dimensions changed. Start a fresh display session.") }
        return result
    }
}

private final class DisplaySampleBuilder {
    private let expected: DisplayVideoFormat
    private var sps: Data?
    private var pps: Data?
    private var format: CMVideoFormatDescription?
    private var waitingForKeyframe = true
    private var lastVideoPresentation: UInt64?
    var needsKeyframe: Bool { waitingForKeyframe }
    init(format: DisplayVideoFormat) { expected = format }
    func reset() { waitingForKeyframe = true }
    func sample(_ unit: DisplayVideoUnit) throws -> (CMSampleBuffer, Bool)? {
        let nals = try DisplayH264.nals(unit.bytes)
        var changed = false
        for nal in nals {
            let kind = nal[nal.startIndex] & 0x1F
            if kind == 7 || kind == 8 {
                guard nal.count <= 64 * 1024 else { throw DeskError.message("The H.264 configuration is too large.") }
                if kind == 7, sps != nal { sps = nal; changed = true }
                if kind == 8, pps != nal { pps = nal; changed = true }
            }
        }
        if changed { format = nil; waitingForKeyframe = true }
        let hasVideo = nals.contains { (1...5).contains($0[$0.startIndex] & 0x1F) }
        guard hasVideo else { return nil }
        guard unit.presentationMicroseconds <= UInt64(Int64.max), lastVideoPresentation.map({ unit.presentationMicroseconds > $0 }) ?? true else {
            throw DeskError.message("The PC returned an out-of-order video timestamp.")
        }
        let isIDR = nals.contains { $0[$0.startIndex] & 0x1F == 5 }
        guard isIDR == unit.keyframe else { throw DeskError.message("The PC returned an invalid H.264 keyframe marker.") }
        if waitingForKeyframe && !isIDR { return nil }
        if format == nil {
            guard let sps, let pps else { throw DeskError.message("The PC did not provide H.264 decoder configuration.") }
            format = try DisplayH264Format.make(sps: sps, pps: pps, expected: expected)
        }
        guard let format else { throw DeskError.message("The video decoder has no format.") }
        var payload = Data()
        for nal in nals where ![7, 8].contains(nal[nal.startIndex] & 0x1F) {
            var length = UInt32(nal.count).bigEndian
            withUnsafeBytes(of: &length) { payload.append(contentsOf: $0) }; payload.append(nal)
        }
        guard payload.count <= DisplayVideoParser.maximumAccessUnit else { throw DeskError.message("The decoded packet is too large.") }
        var block: CMBlockBuffer?
        let allocation = CMBlockBufferCreateWithMemoryBlock(allocator: kCFAllocatorDefault, memoryBlock: nil,
            blockLength: payload.count, blockAllocator: kCFAllocatorDefault, customBlockSource: nil,
            offsetToData: 0, dataLength: payload.count, flags: 0, blockBufferOut: &block)
        guard allocation == kCMBlockBufferNoErr, let block else { throw DeskError.message("The video buffer could not be allocated.") }
        let copied = payload.withUnsafeBytes { bytes -> OSStatus in
            guard let pointer = bytes.baseAddress else { return -1 }
            return CMBlockBufferReplaceDataBytes(with: pointer, blockBuffer: block, offsetIntoDestination: 0, dataLength: payload.count)
        }
        guard copied == kCMBlockBufferNoErr else { throw DeskError.message("The video buffer could not be filled.") }
        var timing = CMSampleTimingInfo(duration: CMTime(value: 1, timescale: CMTimeScale(expected.refreshRate)),
            presentationTimeStamp: CMTime(value: Int64(unit.presentationMicroseconds), timescale: 1_000_000), decodeTimeStamp: .invalid)
        var size = payload.count, sample: CMSampleBuffer?
        let created = CMSampleBufferCreateReady(allocator: kCFAllocatorDefault, dataBuffer: block, formatDescription: format,
            sampleCount: 1, sampleTimingEntryCount: 1, sampleTimingArray: &timing,
            sampleSizeEntryCount: 1, sampleSizeArray: &size, sampleBufferOut: &sample)
        guard created == noErr, let sample else { throw DeskError.message("The video sample could not be created.") }
        if let attachments = CMSampleBufferGetSampleAttachmentsArray(sample, createIfNecessary: true), CFArrayGetCount(attachments) > 0 {
            let dictionary = unsafeBitCast(CFArrayGetValueAtIndex(attachments, 0), to: CFMutableDictionary.self)
            CFDictionarySetValue(dictionary, Unmanaged.passUnretained(kCMSampleAttachmentKey_DisplayImmediately).toOpaque(),
                                 Unmanaged.passUnretained(kCFBooleanTrue).toOpaque())
        }
        waitingForKeyframe = false
        lastVideoPresentation = unit.presentationMicroseconds
        return (sample, changed)
    }
}

final class DisplayVideoController: UIViewController {
    let inbox = DisplayFrameInbox()
    private let format: DisplayVideoFormat
    private let sourceRefresh: Int
    private let close: (String) -> Void
    private let displayLayer = AVSampleBufferDisplayLayer()
    private let status = UILabel()
    private let builder: DisplaySampleBuilder
    private var displayLink: CADisplayLink?
    private var ended = false
    private var received = 0
    private var dropped = 0
    private var reportedAt: CFTimeInterval = 0
    private var lastVideoAt: CFTimeInterval = 0
    private var lastHeartbeatAt: CFTimeInterval = 0
    private var keyframeDeadline = DisplayKeyframeDeadline()
    private(set) var currentRefreshRate = 0

    init(format: DisplayVideoFormat, sourceRefresh: Int, close: @escaping (String) -> Void) {
        self.format = format; self.sourceRefresh = sourceRefresh; self.close = close; self.builder = DisplaySampleBuilder(format: format)
        super.init(nibName: nil, bundle: nil)
        // Keep WKWebView attached: removing it can emit visibilitychange and cancel this very session.
        modalPresentationStyle = .overFullScreen; modalPresentationCapturesStatusBarAppearance = true; isModalInPresentation = true
    }
    required init?(coder: NSCoder) { return nil }
    override var prefersStatusBarHidden: Bool { true }
    override var prefersHomeIndicatorAutoHidden: Bool { true }
    override func viewDidLoad() {
        super.viewDidLoad(); view.backgroundColor = .black
        displayLayer.videoGravity = .resizeAspect; displayLayer.preventsCapture = true; view.layer.addSublayer(displayLayer)
        let exit = UIButton(type: .system); exit.setTitle("Exit display", for: .normal)
        exit.backgroundColor = UIColor(white: 0.12, alpha: 0.95); exit.tintColor = .white; exit.layer.cornerRadius = 12
        exit.titleLabel?.font = .systemFont(ofSize: 15, weight: .semibold); exit.translatesAutoresizingMaskIntoConstraints = false
        exit.addTarget(self, action: #selector(exitPressed), for: .touchUpInside)
        status.text = "Connecting video · target \(format.refreshRate) Hz"; status.textColor = .white
        status.font = .monospacedDigitSystemFont(ofSize: 12, weight: .medium); status.numberOfLines = 3
        status.backgroundColor = UIColor(white: 0.08, alpha: 0.85); status.layer.cornerRadius = 8; status.clipsToBounds = true
        status.translatesAutoresizingMaskIntoConstraints = false; view.addSubview(status); view.addSubview(exit)
        NSLayoutConstraint.activate([
            exit.topAnchor.constraint(equalTo: view.safeAreaLayoutGuide.topAnchor, constant: 12),
            exit.trailingAnchor.constraint(equalTo: view.safeAreaLayoutGuide.trailingAnchor, constant: -12),
            exit.widthAnchor.constraint(greaterThanOrEqualToConstant: 116), exit.heightAnchor.constraint(equalToConstant: 44),
            status.leadingAnchor.constraint(equalTo: view.safeAreaLayoutGuide.leadingAnchor, constant: 12),
            status.topAnchor.constraint(equalTo: exit.bottomAnchor, constant: 8),
            status.trailingAnchor.constraint(lessThanOrEqualTo: view.safeAreaLayoutGuide.trailingAnchor, constant: -12)
        ])
        let link = CADisplayLink(target: self, selector: #selector(tick(_:)))
        let target = Float(format.refreshRate)
        link.preferredFrameRateRange = CAFrameRateRange(minimum: target, maximum: target, preferred: target)
        link.add(to: .main, forMode: .common); displayLink = link
    }
    override func viewDidLayoutSubviews() { super.viewDidLayoutSubviews(); displayLayer.frame = view.bounds }
    @objc private func exitPressed() { close("closed") }
    @objc private func tick(_ link: CADisplayLink) {
        guard !ended else { return }
        keyframeDeadline.begin(at: link.timestamp)
        let interval = link.targetTimestamp - link.timestamp
        if interval > 0 { currentRefreshRate = Int((1 / interval).rounded()) }
        let (units, resync, count, lost) = inbox.take(); received += count; dropped += lost
        if inbox.takeHeartbeat() { lastHeartbeatAt = link.timestamp }
        if resync { beginRecovery(at: link.timestamp) }
        do {
            try keyframeDeadline.check(at: link.timestamp)
            for unit in units {
                let ready: Bool
                if #available(iOS 17, *) { ready = displayLayer.sampleBufferRenderer.isReadyForMoreMediaData }
                else { ready = displayLayer.isReadyForMoreMediaData }
                guard ready else { beginRecovery(at: link.timestamp); inbox.waitForKeyframe(); dropped += 1; break }
                if let (sample, changed) = try builder.sample(unit) {
                    lastVideoAt = link.timestamp
                    if changed { displayLayer.flush() }
                    if #available(iOS 17, *) { displayLayer.sampleBufferRenderer.enqueue(sample) }
                    else { displayLayer.enqueue(sample) }
                    if unit.keyframe { try keyframeDeadline.acceptKeyframe(at: link.timestamp) }
                } else if builder.needsKeyframe {
                    keyframeDeadline.wait(at: link.timestamp)
                }
            }
            if displayLayer.status == .failed { throw DeskError.message("The tablet could not decode this video. Start the display again.") }
            try keyframeDeadline.check(at: link.timestamp)
        } catch { close(error.localizedDescription); return }
        if reportedAt == 0 { reportedAt = link.timestamp }
        let elapsed = link.timestamp - reportedAt
        if elapsed >= 1 {
            let rate = Int((Double(received) / elapsed).rounded())
            let imageReady: Bool
            if #available(iOS 17.4, *) { imageReady = displayLayer.isReadyForDisplay }
            else { imageReady = false } // Older systems report received fps without claiming a visible still image.
            let still = keyframeDeadline.receivedKeyframe && !keyframeDeadline.recovering && imageReady && lastVideoAt > 0 &&
                link.timestamp - lastVideoAt >= 1 && lastHeartbeatAt > 0 && link.timestamp - lastHeartbeatAt <= 1.5
            let activity = keyframeDeadline.recovering ? "Recovering video" : still ? "Still image" : "\(rate) fps received"
            status.text = " \(activity) · \(currentRefreshRate) Hz screen\n Target \(format.refreshRate) Hz · source \(sourceRefresh) Hz" + (dropped > 0 ? " · \(dropped) frames dropped " : " ")
            received = 0; dropped = 0; reportedAt = link.timestamp
        }
    }
    private func beginRecovery(at now: CFTimeInterval) {
        // Preserve the last image during this bounded same-session recovery. A flush still
        // requires a fresh IDR; stop/revocation removes the retained image immediately below.
        displayLayer.flush(); builder.reset(); keyframeDeadline.wait(at: now)
    }
    func stop() {
        guard !ended else { return }; ended = true; inbox.stop(); displayLink?.invalidate(); displayLink = nil
        displayLayer.flushAndRemoveImage(); builder.reset()
    }
    deinit { displayLink?.invalidate() }
}

/// Sample actual display-link cadence after asking the OS for its available high-refresh mode.
/// The result is a measurement, not a promise that power/thermal policy will keep that rate.
final class DisplayRefreshProbe: NSObject {
    private var link: CADisplayLink?
    private var samples: [Double] = []
    private var completion: ((Int) -> Void)?
    private let maximum: Int
    init(maximum: Int) { self.maximum = maximum }
    func start(_ completion: @escaping (Int) -> Void) {
        self.completion = completion
        guard maximum >= 60 else { finish(); return }
        let link = CADisplayLink(target: self, selector: #selector(tick(_:)))
        link.preferredFrameRateRange = CAFrameRateRange(minimum: 60, maximum: Float(maximum), preferred: Float(maximum))
        self.link = link; link.add(to: .main, forMode: .common)
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.5) { [weak self] in self?.finish() }
    }
    @objc private func tick(_ link: CADisplayLink) {
        let interval = link.targetTimestamp - link.timestamp
        if interval > 0 { samples.append(1 / interval) }
        if samples.count >= 8 { finish() }
    }
    private func finish() {
        guard let completion else { return }; self.completion = nil; link?.invalidate(); link = nil
        let sorted = samples.sorted(), value = sorted.isEmpty ? 0 : Int(sorted[sorted.count / 2].rounded())
        // Zero means no cadence was observed. Match must not silently substitute the panel maximum.
        completion(max(0, min(maximum, value)))
    }
    func cancel() { completion = nil; link?.invalidate(); link = nil }
    deinit { link?.invalidate() }
}
