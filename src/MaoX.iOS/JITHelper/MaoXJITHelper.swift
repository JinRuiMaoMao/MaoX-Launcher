// MaoX Launcher built-in JIT helper (app extension, PlugIns/MaoXJITHelper.appex).
//
// A process cannot attach a debugger to itself, so the launcher starts this extension and it enables JIT for the
// launcher with StikJIT 1.9.0 (MPL-2.0, https://github.com/StikDebug/StikJIT, linked unmodified) over LocalDevVPN.
// The launcher side is Native/jit_helper.m: one JSON request arrives in the first NSExtensionItem's userInfo and one
// JSON response goes back the same way when the request completes. Whether JIT is really on is decided by the
// launcher (CS_DEBUGGED), never by this response alone.
import Foundation
import StikJIT

private enum Key {
    static let request = "io.github.jinruimaomao.maox.jit.request"
    static let response = "io.github.jinruimaomao.maox.jit.response"
}

private struct HelperRequest: Decodable {
    /// prepare / enable / resetDDI
    var op: String
    var pid: Int32?
    /// The pairing file's bytes (base64 in JSON). Written to a temporary file for StikJIT and deleted afterwards.
    var pairing: Data?
    /// Base64 JIT script (UniversalJIT26.js); StikJIT's bundled universal script when missing.
    var script: String?
    var forceScript: Bool?
    var address: String?
    var port: UInt16?
    var connectionTimeout: Double?
}

private struct HelperResponse: Encodable {
    var ok: Bool
    /// ready / detached / ddiReset on success; otherwise the failure kind (see HelperWork.code(for:)).
    var code: String
    var message: String
    var stage: String?
    var txm: Bool?
}

/// The last StikJIT stage, reported with failures. Progress callbacks may come from StikJIT's own queues.
private final class Stage: @unchecked Sendable {
    private let lock = NSLock()
    private var current = "start"

    var value: String {
        lock.lock()
        defer { lock.unlock() }
        return current
    }

    func set(_ value: String) {
        lock.lock()
        current = value
        lock.unlock()
    }
}

private enum HelperWork {
    /// StikJIT's calls block (and enable lasts while the debugger is attached): one request at a time, off the
    /// extension's main thread, as StikJIT's INTEGRATION.md asks.
    static let queue = DispatchQueue(label: "io.github.jinruimaomao.maox.jit-helper")

    /// The helper's own container; the Developer Disk Image is cached here.
    static var root: URL {
        FileManager.default.urls(for: .libraryDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("StikJIT", isDirectory: true)
    }

    static func run(_ data: Data?) -> HelperResponse {
        guard let data, let request = try? JSONDecoder().decode(HelperRequest.self, from: data) else {
            return HelperResponse(ok: false, code: "badRequest", message: "The JIT helper received no valid request.")
        }
        let stage = Stage()
        do {
            return try handle(request, stage: stage)
        } catch {
            return HelperResponse(ok: false, code: code(for: error), message: error.localizedDescription,
                                  stage: stage.value, txm: StikJIT.isTXMPresent)
        }
    }

    private static func handle(_ request: HelperRequest, stage: Stage) throws -> HelperResponse {
        let manager = FileManager.default
        try manager.createDirectory(at: root, withIntermediateDirectories: true)
        let paths = DDIPaths.default(in: root)

        switch request.op {
        case "resetDDI":
            try StikJIT.resetCachedDDI(at: paths)
            return HelperResponse(ok: true, code: "ddiReset", message: "The cached Developer Disk Image was removed.",
                                  txm: StikJIT.isTXMPresent)
        case "prepare", "enable":
            break
        default:
            return HelperResponse(ok: false, code: "badRequest", message: "Unknown operation \(request.op).")
        }

        guard let pairing = request.pairing, !pairing.isEmpty else {
            return HelperResponse(ok: false, code: "pairingFile", message: "The pairing file was not provided.")
        }
        let pairingURL = root.appendingPathComponent("pairing-\(UUID().uuidString).plist")
        try pairing.write(to: pairingURL, options: [.atomic, .completeFileProtectionUntilFirstUserAuthentication])
        defer { try? manager.removeItem(at: pairingURL) }

        var configuration = StikJIT.Configuration()
        if let address = request.address, !address.isEmpty {
            configuration.deviceAddress = address
        }
        if let port = request.port, port > 0 {
            configuration.rsdPort = port
        }
        if let timeout = request.connectionTimeout, timeout > 0 {
            configuration.connectionTimeout = timeout
        }

        // Prepared separately first so "LocalDevVPN not reachable" and "DDI failed" stay distinguishable;
        // enableJIT prepares again, which is quick once the image is mounted.
        let readiness = StikJIT.prepareDevice(pairingFile: pairingURL, paths: paths, configuration: configuration,
                                              progress: { stage.set(describe($0)) })
        let txm: Bool?
        switch readiness {
        case .ready(let security):
            txm = security.isTXMPresent
        case .unreachable(let reason):
            return HelperResponse(ok: false, code: "unreachable", message: reason, stage: stage.value,
                                  txm: StikJIT.isTXMPresent)
        case .preparationFailed(let reason):
            return HelperResponse(ok: false, code: "preparationFailed", message: reason, stage: stage.value,
                                  txm: StikJIT.isTXMPresent)
        @unknown default:
            return HelperResponse(ok: false, code: "stikjit", message: "StikJIT returned an unknown preparation state.",
                                  stage: stage.value, txm: StikJIT.isTXMPresent)
        }
        if request.op == "prepare" {
            return HelperResponse(ok: true, code: "ready",
                                  message: "LocalDevVPN is reachable and the Developer Disk Image is mounted.",
                                  stage: stage.value, txm: txm)
        }

        guard let pid = request.pid, pid > 0 else {
            return HelperResponse(ok: false, code: "badRequest", message: "No target process was given.", txm: txm)
        }
        let script: StikJIT.Script
        if let base64 = request.script, !base64.isEmpty {
            script = .customBase64(base64)
        } else {
            script = .universal
        }
        stage.set("attaching")
        try StikJIT.enableJIT(targetPID: pid,
                              pairingFile: pairingURL,
                              ddiPaths: paths,
                              configuration: configuration,
                              script: script,
                              forceScript: request.forceScript ?? false,
                              preparationProgress: { stage.set(describe($0)) },
                              progress: { message in
                                  stage.set("attached")
                                  NSLog("[MaoXJIT] %@", message)
                              })
        return HelperResponse(ok: true, code: "detached", message: "StikJIT finished and the debugger detached.",
                              stage: stage.value, txm: txm)
    }

    private static func describe(_ stage: StikJIT.PreparationStage) -> String {
        switch stage {
        case .checkingReachability:
            return "checkingReachability"
        case .checkingDDI:
            return "checkingDDI"
        case .downloadingDDI(let fraction, _):
            return "downloadingDDI \(Int(fraction * 100))%"
        case .mountingDDI(let fraction):
            return "mountingDDI \(Int(fraction * 100))%"
        case .verifyingDDI:
            return "verifyingDDI"
        case .ready:
            return "ready"
        @unknown default:
            return "unknown"
        }
    }

    private static func code(for error: Error) -> String {
        guard let error = error as? StikJITError else { return "io" }
        switch error {
        case .pairingFile:
            return "pairingFile"
        case .scriptUnavailable:
            return "scriptUnavailable"
        case .debugProxyUnavailable:
            return "debugProxyUnavailable"
        case .device:
            return "device"
        case .ddiDownload:
            return "ddiDownload"
        case .ddiFilesMissing:
            return "ddiFilesMissing"
        case .ddiNotMounted:
            return "ddiNotMounted"
        case .deviceNotReady:
            return "deviceNotReady"
        case .txmDetectionUnavailable:
            return "txmDetectionUnavailable"
        case .customScript:
            return "customScript"
        case .scriptExecution:
            return "scriptExecution"
        @unknown default:
            return "stikjit"
        }
    }
}

/// NSExtensionPrincipalClass (Info.plist). The request completes only when StikJIT returns, so for a script that
/// stays attached (TXM devices) this process lives until the launcher's game detaches it.
@objc(MaoXJITHelperHandler)
final class MaoXJITHelperHandler: NSObject, NSExtensionRequestHandling {
    func beginRequest(with context: NSExtensionContext) {
        let data = (context.inputItems.first as? NSExtensionItem)?.userInfo?[Key.request] as? Data
        HelperWork.queue.async {
            let response = HelperWork.run(data)
            NSLog("[MaoXJIT] finished: ok=%d code=%@ stage=%@", response.ok ? 1 : 0, response.code,
                  response.stage ?? "-")
            let item = NSExtensionItem()
            item.userInfo = [Key.response: (try? JSONEncoder().encode(response)) ?? Data()]
            context.completeRequest(returningItems: [item], completionHandler: nil)
        }
    }
}
