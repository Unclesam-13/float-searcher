// swift-tools-version:5.9
import PackageDescription

let package = Package(
    name: "FloatSearcher",
    platforms: [.macOS(.v13)],
    targets: [
        .executableTarget(
            name: "FloatSearcher",
            path: "Sources/FloatSearcher"
        )
    ]
)
