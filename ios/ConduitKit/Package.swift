// swift-tools-version:5.9
import PackageDescription

let package = Package(
    name: "ConduitKit",
    platforms: [.iOS(.v17), .macOS(.v14)],
    products: [
        .library(name: "ConduitKit", targets: ["ConduitKit"]),
    ],
    targets: [
        .target(name: "ConduitKit"),
        .testTarget(name: "ConduitKitTests", dependencies: ["ConduitKit"]),
    ]
)
