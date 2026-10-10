import ConduitKit
import SwiftUI
import UIKit

/// An image picked in the composer, downscaled and JPEG-encoded for the wire.
struct Attachment: Identifiable, Equatable {
    let id = UUID()
    let thumbnail: UIImage
    let data: Data
    let mimeType: String

    var json: JSONValue { ["mimeType": .string(mimeType), "data": .string(data.base64EncodedString())] }

    /// Downscales to at most 1600 px on the long side (agents don't need more; keeps uploads fast).
    init?(imageData: Data) {
        guard let image = UIImage(data: imageData) else { return nil }
        let maxSide: CGFloat = 1600
        let scale = min(1, maxSide / max(image.size.width, image.size.height))
        let size = CGSize(width: image.size.width * scale, height: image.size.height * scale)
        let renderer = UIGraphicsImageRenderer(size: size)
        let resized = renderer.image { _ in image.draw(in: CGRect(origin: .zero, size: size)) }
        guard let jpeg = resized.jpegData(compressionQuality: 0.82) else { return nil }
        data = jpeg
        mimeType = "image/jpeg"
        thumbnail = resized
    }

    static func == (a: Attachment, b: Attachment) -> Bool { a.id == b.id }
}
