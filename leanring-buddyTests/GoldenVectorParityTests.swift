import XCTest
@testable import Clicky

/// T-008: runs the shared golden vectors (`shared/golden/*.json`) against the Swift implementation,
/// proving the Windows port's C# core reproduces the same behavior as this original code. These
/// vectors also run against `Clicky.Core` on Windows/Linux in CI, so all three platforms execute
/// the identical fixtures.
@MainActor
final class GoldenVectorParityTests: XCTestCase {

    // MARK: - Shared fixture loading

    /// The repository root (this file lives in `leanring-buddyTests/`, one level below it).
    private static let repositoryRoot = URL(fileURLWithPath: #filePath)
        .deletingLastPathComponent()
        .deletingLastPathComponent()

    private static func loadVectorDocument(_ seamName: String) throws -> [String: Any] {
        let vectorURL = repositoryRoot
            .appendingPathComponent("shared")
            .appendingPathComponent("golden")
            .appendingPathComponent("\(seamName).json")
        let vectorData = try Data(contentsOf: vectorURL)
        return try XCTUnwrap(JSONSerialization.jsonObject(with: vectorData) as? [String: Any])
    }

    private static func loadVectorCases(_ seamName: String) throws -> [[String: Any]] {
        let document = try loadVectorDocument(seamName)
        return try XCTUnwrap(document["cases"] as? [[String: Any]])
    }

    // MARK: - POINT tag parsing (seam 2)

    func testPointTagParsingMatchesGoldenVector() throws {
        let cases = try Self.loadVectorCases("point-tag")
        XCTAssertFalse(cases.isEmpty, "point-tag.json must carry cases")

        for vectorCase in cases {
            let caseID = try XCTUnwrap(vectorCase["id"] as? String)
            let input = try XCTUnwrap(vectorCase["input"] as? [String: Any])
            let expected = try XCTUnwrap(vectorCase["expected"] as? [String: Any])
            let responseText = try XCTUnwrap(input["text"] as? String)

            let result = CompanionManager.parsePointingCoordinates(from: responseText)

            XCTAssertEqual(result.spokenText, expected["spokenText"] as? String, "case \(caseID): spokenText")
            if let expectedX = expected["x"] as? Double, let expectedY = expected["y"] as? Double {
                let coordinate = try XCTUnwrap(result.coordinate, "case \(caseID): expected a coordinate")
                XCTAssertEqual(coordinate.x, expectedX, accuracy: 0.0001, "case \(caseID): x")
                XCTAssertEqual(coordinate.y, expectedY, accuracy: 0.0001, "case \(caseID): y")
            } else {
                XCTAssertNil(result.coordinate, "case \(caseID): expected no coordinate")
            }
            XCTAssertEqual(result.elementLabel, expected["label"] as? String?, "case \(caseID): label")
            let expectedScreen = expected["screen"] as? Int
            XCTAssertEqual(result.screenNumber, expectedScreen, "case \(caseID): screen")
        }
    }

    // MARK: - Push-to-talk detector (seam 7)

    /// The Swift core reads the baked-in `currentShortcutOption` (`.controlOption`, the shipped
    /// default), so only the controlOption cases can run here. The other shortcut options exist in
    /// the vectors and run against the Windows detector, which is option-parameterized (T-007
    /// hot-swap); parameterizing the Swift core is a recorded parity finding, not this test's job.
    func testPushToTalkDetectorMatchesGoldenVector() throws {
        let cases = try Self.loadVectorCases("push-to-talk")
        var ranCaseCount = 0

        for vectorCase in cases {
            let caseID = try XCTUnwrap(vectorCase["id"] as? String)
            let input = try XCTUnwrap(vectorCase["input"] as? [String: Any])

            guard let shortcutOption = input["option"] as? String, shortcutOption == "controlOption" else {
                continue // options beyond the shipped default: Windows-parameterized only (see doc comment)
            }
            guard input["rawEvents"] == nil else {
                continue // rawEvents cases feed the Windows low-level-keyboard translator, which has no Swift counterpart
            }

            let events = try XCTUnwrap(input["events"] as? [[String: Any]])
            let expectedTransitions = try XCTUnwrap(vectorCase["expected"] as? [String: Any])
            let expectedTransitionNames = try XCTUnwrap(expectedTransitions["transitions"] as? [String])
            XCTAssertEqual(events.count, expectedTransitionNames.count, "case \(caseID): event/expectation count")

            var wasShortcutPreviouslyPressed = false
            for (eventIndex, event) in events.enumerated() {
                let transition = BuddyPushToTalkShortcut.shortcutTransition(
                    for: try Self.cgEventType(for: try XCTUnwrap(event["type"] as? String)),
                    keyCode: (event["key"] as? String) == "space" ? BuddyPushToTalkShortcut.pushToTalkKeyCode : 0,
                    modifierFlagsRawValue: try Self.modifierFlags(for: event).rawValue,
                    wasShortcutPreviouslyPressed: wasShortcutPreviouslyPressed)

                XCTAssertEqual(Self.transitionName(transition), expectedTransitionNames[eventIndex],
                               "case \(caseID): transition at index \(eventIndex)")

                // Mirror the manager's state threading: the caller feeds each transition's outcome forward.
                if transition == .pressed { wasShortcutPreviouslyPressed = true }
                if transition == .released { wasShortcutPreviouslyPressed = false }
            }
            ranCaseCount += 1
        }

        XCTAssertGreaterThan(ranCaseCount, 0, "no controlOption neutral cases ran — the vector set changed shape")
    }

    private static func cgEventType(for eventName: String) throws -> CGEventType {
        switch eventName {
        case "flagsChanged": return .flagsChanged
        case "keyDown": return .keyDown
        case "keyUp": return .keyUp
        case let unknownEventName: throw XCTSkip("unknown event type \(unknownEventName)")
        }
    }

    private static func modifierFlags(for event: [String: Any]) throws -> NSEvent.ModifierFlags {
        var flags: NSEvent.ModifierFlags = []
        for modifierName in event["modifiers"] as? [String] ?? [] {
            switch modifierName {
            case "ctrl": flags.insert(.control)
            case "alt": flags.insert(.option)
            case "shift": flags.insert(.shift)
            case let unknownModifierName: throw XCTSkip("unknown modifier \(unknownModifierName)")
            }
        }
        return flags
    }

    private static func transitionName(_ transition: BuddyPushToTalkShortcut.ShortcutTransition) -> String {
        switch transition {
        case .none: return "none"
        case .pressed: return "pressed"
        case .released: return "released"
        }
    }

    // MARK: - Prompt bundle parity (T-009)

    /// T-009 moved both system prompts out of Swift string literals into `shared/prompts/` loaded as
    /// bundle resources. The bundle copy and the repository file must stay byte-identical, which is
    /// what keeps the Windows port (which reads the files directly) and this app on one prompt.
    func testPromptsInBundleMatchSharedPromptFiles() throws {
        for promptFileName in ["companion-voice-response", "onboarding-demo"] {
            let bundlePromptURL = try XCTUnwrap(Bundle.main.url(forResource: promptFileName, withExtension: "txt"),
                                                "prompt \(promptFileName).txt missing from the app bundle")
            let bundlePromptBytes = try Data(contentsOf: bundlePromptURL)

            let sharedPromptURL = Self.repositoryRoot
                .appendingPathComponent("shared")
                .appendingPathComponent("prompts")
                .appendingPathComponent("\(promptFileName).txt")
            let sharedPromptBytes = try Data(contentsOf: sharedPromptURL)

            XCTAssertEqual(bundlePromptBytes, sharedPromptBytes,
                           "\(promptFileName).txt: app bundle and shared/prompts have drifted")
        }
    }
}
