import UIKit
import ImageIO

final class PenInputController: UIViewController, UITextViewDelegate, UIAdaptivePresentationControllerDelegate {
    private let original: String
    private let completion: (String, Bool) -> Void
    private let field = UITextView()
    private var completed = false
    init(text: String, completion: @escaping (String, Bool) -> Void) {
        original = text; self.completion = completion; super.init(nibName: nil, bundle: nil)
        title = "Handwriting"; preferredContentSize = CGSize(width: 640, height: 440)
    }
    required init?(coder: NSCoder) { fatalError("Use init(text:completion:)") }
    override func viewDidLoad() {
        super.viewDidLoad(); view.backgroundColor = .systemBackground
        navigationItem.leftBarButtonItem = UIBarButtonItem(barButtonSystemItem: .cancel, target: self, action: #selector(cancel))
        navigationItem.rightBarButtonItem = UIBarButtonItem(title: "Use text", style: .done, target: self, action: #selector(save))
        let help = UILabel(); help.text = "Write in the field with Apple Pencil and Scribble enabled, or type with the keyboard. Recognition and supported languages are provided by iPadOS."
        help.numberOfLines = 0; help.font = .preferredFont(forTextStyle: .subheadline); help.textColor = .secondaryLabel
        field.text = original; field.font = .preferredFont(forTextStyle: .body); field.adjustsFontForContentSizeCategory = true
        field.accessibilityLabel = "Handwriting text"; field.delegate = self
        field.backgroundColor = .secondarySystemBackground; field.layer.cornerRadius = 12
        field.textContainerInset = UIEdgeInsets(top: 14, left: 10, bottom: 14, right: 10)
        let stack = UIStackView(arrangedSubviews: [help, field]); stack.axis = .vertical; stack.spacing = 16; stack.translatesAutoresizingMaskIntoConstraints = false
        view.addSubview(stack)
        NSLayoutConstraint.activate([
            stack.leadingAnchor.constraint(equalTo: view.safeAreaLayoutGuide.leadingAnchor, constant: 20),
            stack.trailingAnchor.constraint(equalTo: view.safeAreaLayoutGuide.trailingAnchor, constant: -20),
            stack.topAnchor.constraint(equalTo: view.safeAreaLayoutGuide.topAnchor, constant: 20),
            stack.bottomAnchor.constraint(equalTo: view.keyboardLayoutGuide.topAnchor, constant: -20),
            field.heightAnchor.constraint(greaterThanOrEqualToConstant: 120)
        ])
    }
    override func viewDidAppear(_ animated: Bool) {
        super.viewDidAppear(animated); navigationController?.presentationController?.delegate = self
    }
    func textView(_ textView: UITextView, shouldChangeTextIn range: NSRange, replacementText text: String) -> Bool {
        guard let swiftRange = Range(range, in: textView.text) else { return false }
        return textView.text.replacingCharacters(in: swiftRange, with: text).utf16.count <= 8000
    }
    @objc private func save() { finish(field.text, cancelled: false) }
    @objc func cancel() { finish(original, cancelled: true) }
    func presentationControllerDidDismiss(_ presentationController: UIPresentationController) { finish(original, cancelled: true, dismiss: false) }
    private func finish(_ text: String, cancelled: Bool, dismiss: Bool = true) {
        guard !completed else { return }; completed = true; completion(text, cancelled)
        if dismiss { navigationController?.dismiss(animated: true) }
    }
}

enum PNGExport {
    static func decode(_ value: String) throws -> Data {
        let prefix = "data:image/png;base64,", maxBytes = 8 * 1024 * 1024
        guard value.hasPrefix(prefix), value.utf8.count <= prefix.count + ((maxBytes + 2) / 3) * 4,
              let data = Data(base64Encoded: String(value.dropFirst(prefix.count))), data.count <= maxBytes,
              data.count >= 33, Array(data.prefix(8)) == [137,80,78,71,13,10,26,10],
              let source = CGImageSourceCreateWithData(data as CFData, nil), CGImageSourceGetCount(source) == 1,
              let type = CGImageSourceGetType(source), type as String == "public.png",
              let properties = CGImageSourceCopyPropertiesAtIndex(source, 0, nil) as? [CFString: Any],
              let width = properties[kCGImagePropertyPixelWidth] as? Int, let height = properties[kCGImagePropertyPixelHeight] as? Int,
              (1...4096).contains(width), (1...4096).contains(height) else {
            throw DeskError.message("Export a valid PNG smaller than 8 MB and no larger than 4096 × 4096 pixels.")
        }
        return data
    }
    static func safeName(_ value: String) -> String {
        if value.range(of: "^[A-Za-z0-9 _-]{1,80}\\.png$", options: .regularExpression) != nil { return value }
        return "DeskWing-sketch.png"
    }
}
