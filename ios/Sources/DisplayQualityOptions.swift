import Foundation
import CoreFoundation

struct DisplayQualityOptions: Equatable {
    let preset: String
    let adaptive: Bool
    static func parse(_ body: [String:Any]) throws -> DisplayQualityOptions {
        guard Set(body.keys).isSubset(of:["session","refreshMode","qualityPreset","adaptiveQuality"]),
              body["session"] != nil, body["refreshMode"] != nil else {
            throw DeskError.message("Choose a display session, refresh rate and supported quality options.")
        }
        let preset=body["qualityPreset"] as? String ?? "balanced"
        guard body["qualityPreset"] == nil || body["qualityPreset"] is String,
              ["balanced","quality","battery-saver"].contains(preset) else { throw DeskError.message("Choose Balanced, Quality or Battery saver.") }
        var adaptive=true
        if let value=body["adaptiveQuality"] {
            guard let number=value as? NSNumber, CFGetTypeID(number) == CFBooleanGetTypeID() else {
                throw DeskError.message("Choose whether adaptive quality is enabled.")
            }
            adaptive=number.boolValue
        }
        return DisplayQualityOptions(preset:preset,adaptive:adaptive)
    }
}
