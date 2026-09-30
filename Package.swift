// swift-tools-version:6.0
import PackageDescription

let package = Package(
    name: "SaveIt",
    platforms: [.macOS(.v14)],
    targets: [
        .executableTarget(
            name: "SaveIt",
            path: "Sources/SaveIt"
        )
    ],
    swiftLanguageModes: [.v5]
)
