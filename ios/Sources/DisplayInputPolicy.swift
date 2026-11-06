import Foundation
import CoreGraphics
import CoreFoundation

enum DisplayNativeInputMode: String, CaseIterable {
    case view, pen, touch
    var title: String { switch self { case .view: return "View"; case .pen: return "Pen"; case .touch: return "Touch" } }
}

struct DisplayInputPoint: Equatable {
    let x: Double
    let y: Double
}

struct DisplayInputSample: Equatable {
    let phase: String
    let point: DisplayInputPoint
    let pressure: Double
    let tiltX: Double
    let tiltY: Double
    var json: [String: Any] {
        ["phase":phase, "x":point.x, "y":point.y, "pressure":pressure,
         "tiltX":tiltX, "tiltY":tiltY, "barrel":false, "eraser":false]
    }
}

enum DisplayInputPolicy {
    static let path = "/api/computer/display/input"
    static let maximumPending = 64
    static let maximumBatch = 32
    static func accepts(mode: DisplayNativeInputMode, pencil: Bool, direct: Bool) -> Bool {
        mode == .pen ? pencil : mode == .touch && direct
    }

    /// Map only the visible image. Touching the black bars never moves a Windows pointer.
    static func point(_ point: CGPoint, viewport: CGSize, image: CGSize) -> DisplayInputPoint? {
        let values = [point.x,point.y,viewport.width,viewport.height,image.width,image.height]
        guard values.allSatisfy({ $0.isFinite }), viewport.width > 0, viewport.height > 0,
              image.width > 0, image.height > 0, image.width <= 8192, image.height <= 8192 else { return nil }
        let scale = min(viewport.width / image.width, viewport.height / image.height)
        let width = image.width * scale, height = image.height * scale
        let left = (viewport.width - width) / 2, top = (viewport.height - height) / 2
        guard point.x >= left, point.x <= left + width, point.y >= top, point.y <= top + height else { return nil }
        return DisplayInputPoint(x:Double((point.x-left)/width), y:Double((point.y-top)/height))
    }

    static func sample(phase: String, point: DisplayInputPoint, force: Double, maximumForce: Double,
                       altitude: Double, azimuth: Double, mode: DisplayNativeInputMode) -> DisplayInputSample? {
        guard ["down","move","up","hover","leave"].contains(phase), mode != .view,
              point.x.isFinite, point.y.isFinite, (0...1).contains(point.x), (0...1).contains(point.y),
              force.isFinite, maximumForce.isFinite, altitude.isFinite, azimuth.isFinite else { return nil }
        let touching = phase == "down" || phase == "move"
        if mode == .touch {
            return DisplayInputSample(phase:phase,point:point,pressure:touching ? 0.5 : 0,tiltX:0,tiltY:0)
        }
        let pressure = touching ? min(1,max(0,maximumForce > 0 ? force/maximumForce : 0.5)) : 0
        let angle = min(Double.pi/2,max(0,altitude)), vertical = max(0.000001,sin(angle))
        let horizontal = cos(angle)
        let tiltX = atan2(cos(azimuth)*horizontal,vertical)*180/Double.pi
        let tiltY = atan2(sin(azimuth)*horizontal,vertical)*180/Double.pi
        return DisplayInputSample(phase:phase,point:point,pressure:pressure,
                                  tiltX:min(90,max(-90,tiltX)),tiltY:min(90,max(-90,tiltY)))
    }

    static func allowedRequest(_ path: String, method: String, body: [String: Any]) -> Bool {
        guard path == Self.path, method == "POST", let operation = body["operation"] as? String else { return false }
        return ["begin","samples","renew","end","cancel"].contains(operation)
    }

    static func integer(_ value: Any?) -> Int? {
        guard let number = value as? NSNumber, CFGetTypeID(number) != CFBooleanGetTypeID(),
              number.doubleValue.isFinite, number.doubleValue == Double(number.intValue) else { return nil }
        return number.intValue
    }

    static func lease(_ value: Any?) -> String? {
        guard let token = value as? String, token.utf8.count == 48,
              token.utf8.allSatisfy({ (48...57).contains($0) || (65...70).contains($0) || (97...102).contains($0) }) else { return nil }
        return token
    }
}

struct DisplayInputQueue {
    private(set) var samples: [DisplayInputSample] = []
    var isEmpty: Bool { samples.isEmpty }
    mutating func offer(_ sample: DisplayInputSample, coalescingAfter: Int = 32) -> Bool {
        if sample.phase == "move", samples.last?.phase == "move", samples.count >= max(1,min(32,coalescingAfter)) {
            samples[samples.count-1]=sample; return true
        }
        guard samples.count < DisplayInputPolicy.maximumPending else { return false }
        samples.append(sample); return true
    }
    mutating func take(_ limit: Int) -> [DisplayInputSample] {
        let result=Array(samples.prefix(max(1,min(32,limit))))
        samples.removeFirst(result.count); return result
    }
    mutating func reset() { samples.removeAll(keepingCapacity:false) }
}
