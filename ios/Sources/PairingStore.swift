import Foundation
import Security

struct PairedPC: Codable {
    var host: String
    var port: Int
    var controlPort: Int
    var fingerprint: String
    var token: String
    var pairingID: String
    var valid: Bool {
        HostPolicy.validHost(host) && (1...65535).contains(port) && (1...65535).contains(controlPort) &&
            fingerprint.count == 64 && HostPolicy.normalizedPin(fingerprint) == fingerprint &&
            token.count == 64 && token.utf8.allSatisfy({ (48...57).contains($0) || (65...70).contains($0) }) &&
            UUID(uuidString: pairingID) != nil
    }
}

enum PairingStore {
    private static let service = "app.deskwing.tablet.pairing"
    private static let account = "paired-pc-v1"
    private static var query: [String: Any] { [kSecClass as String: kSecClassGenericPassword, kSecAttrService as String: service, kSecAttrAccount as String: account] }
    static func load() throws -> PairedPC? {
        var request = query; request[kSecReturnData as String] = true; request[kSecMatchLimit as String] = kSecMatchLimitOne
        var result: CFTypeRef?
        let status = SecItemCopyMatching(request as CFDictionary, &result)
        if status == errSecItemNotFound { return nil }
        guard status == errSecSuccess, let data = result as? Data else { throw DeskError.message("Unlock your device to read the saved PC pairing.") }
        guard data.count <= 4096, let saved = try? JSONDecoder().decode(PairedPC.self, from: data), saved.valid else {
            throw DeskError.message("Saved pairing could not be read. Forget this PC, then pair again.")
        }
        return saved
    }
    static func save(_ pc: PairedPC) throws {
        guard pc.valid else { throw DeskError.message("The PC returned invalid pairing details.") }
        let data = try JSONEncoder().encode(pc)
        let attributes: [String: Any] = [kSecValueData as String: data, kSecAttrAccessible as String: kSecAttrAccessibleWhenUnlockedThisDeviceOnly]
        var status = SecItemUpdate(query as CFDictionary, attributes as CFDictionary)
        if status == errSecItemNotFound {
            var create = query; attributes.forEach { create[$0.key] = $0.value }
            status = SecItemAdd(create as CFDictionary, nil)
        }
        guard status == errSecSuccess else { throw DeskError.message("The secure pairing could not be saved. Unlock your device and try again.") }
    }
    static func forget() throws {
        let status = SecItemDelete(query as CFDictionary)
        guard status == errSecSuccess || status == errSecItemNotFound else { throw DeskError.message("The saved pairing could not be removed. Unlock your device and try again.") }
    }
}
