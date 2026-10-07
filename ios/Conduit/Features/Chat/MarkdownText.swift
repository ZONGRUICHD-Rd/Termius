import SwiftUI

/// Small, fast Markdown renderer for agent replies: headings, lists, quotes, fenced code, inline styles.
/// Parsing is a single pass over lines, cheap enough to rerun on every streamed delta.
struct MarkdownText: View {
    let text: String
    var streaming = false

    var body: some View {
        let blocks = MarkdownBlock.parse(text)
        VStack(alignment: .leading, spacing: 10) {
            ForEach(Array(blocks.enumerated()), id: \.offset) { _, block in
                view(for: block)
            }
        }
        .textSelection(.enabled)
    }

    @ViewBuilder
    private func view(for block: MarkdownBlock) -> some View {
        switch block {
        case .paragraph(let s):
            Text(inline(s)).font(.body).foregroundStyle(Brand.ink).lineSpacing(3)
        case .heading(let level, let s):
            Text(inline(s))
                .font(.serif(level == 1 ? .title2 : level == 2 ? .title3 : .headline, weight: .semibold))
                .foregroundStyle(Brand.ink)
                .padding(.top, 4)
        case .bullet(let items, let ordered):
            VStack(alignment: .leading, spacing: 6) {
                ForEach(Array(items.enumerated()), id: \.offset) { i, item in
                    HStack(alignment: .firstTextBaseline, spacing: 8) {
                        Text(ordered ? "\(i + 1)." : "•").foregroundStyle(Brand.clay).font(.body.weight(.semibold))
                        Text(inline(item)).foregroundStyle(Brand.ink)
                    }
                }
            }
        case .quote(let s):
            HStack(spacing: 10) {
                RoundedRectangle(cornerRadius: 2).fill(Brand.sand).frame(width: 3)
                Text(inline(s)).italic().foregroundStyle(Brand.inkSoft)
            }
        case .code(let lang, let code):
            CodeBlock(language: lang, code: code)
        case .rule:
            Rectangle().fill(Brand.hairline).frame(height: 1)
        }
    }

    private func inline(_ s: String) -> AttributedString {
        (try? AttributedString(markdown: s, options: .init(interpretedSyntax: .inlineOnlyPreservingWhitespace))) ?? AttributedString(s)
    }
}

struct CodeBlock: View {
    let language: String
    let code: String
    @State private var copied = false

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            HStack {
                Text(language.isEmpty ? "code" : language).font(.caption2.weight(.semibold).monospaced()).foregroundStyle(.secondary)
                Spacer()
                Button {
                    UIPasteboard.general.string = code
                    copied = true
                    Task {
                        try? await Task.sleep(nanoseconds: 1_200_000_000)
                        copied = false
                    }
                } label: {
                    Image(systemName: copied ? "checkmark" : "doc.on.doc").font(.caption)
                        .contentTransition(.symbolEffect(.replace))
                }
                .buttonStyle(.plain)
                .foregroundStyle(.secondary)
                .sensoryFeedback(.success, trigger: copied)
            }
            .padding(.horizontal, 12)
            .padding(.vertical, 7)
            ScrollView(.horizontal, showsIndicators: false) {
                Text(code)
                    .font(.system(.footnote, design: .monospaced))
                    .foregroundStyle(Brand.ink)
                    .padding(12)
            }
        }
        .background(Brand.paperDeep, in: .rect(cornerRadius: 14))
        .overlay(RoundedRectangle(cornerRadius: 14).strokeBorder(Brand.hairline))
    }
}

enum MarkdownBlock: Equatable {
    case paragraph(String)
    case heading(Int, String)
    case bullet([String], ordered: Bool)
    case quote(String)
    case code(String, String)
    case rule

    static func parse(_ text: String) -> [MarkdownBlock] {
        var blocks: [MarkdownBlock] = []
        var para: [String] = []
        var list: [String] = []
        var listOrdered = false
        var code: [String]?
        var codeLang = ""

        func flushPara() {
            if !para.isEmpty { blocks.append(.paragraph(para.joined(separator: "\n"))) }
            para = []
        }
        func flushList() {
            if !list.isEmpty { blocks.append(.bullet(list, ordered: listOrdered)) }
            list = []
        }

        for raw in text.components(separatedBy: "\n") {
            let line = raw.trimmingCharacters(in: .whitespaces)
            if code != nil {
                if line.hasPrefix("```") {
                    blocks.append(.code(codeLang, code!.joined(separator: "\n")))
                    code = nil
                } else {
                    code!.append(raw)
                }
                continue
            }
            if line.hasPrefix("```") {
                flushPara(); flushList()
                codeLang = String(line.dropFirst(3)).trimmingCharacters(in: .whitespaces)
                code = []
            } else if line.isEmpty {
                flushPara(); flushList()
            } else if line.hasPrefix("#") {
                flushPara(); flushList()
                let level = line.prefix { $0 == "#" }.count
                blocks.append(.heading(min(level, 3), String(line.dropFirst(level)).trimmingCharacters(in: .whitespaces)))
            } else if line == "---" || line == "***" {
                flushPara(); flushList()
                blocks.append(.rule)
            } else if line.hasPrefix("> ") {
                flushPara(); flushList()
                blocks.append(.quote(String(line.dropFirst(2))))
            } else if line.hasPrefix("- ") || line.hasPrefix("* ") || line.hasPrefix("+ ") {
                flushPara()
                if !list.isEmpty && listOrdered { flushList() }
                listOrdered = false
                list.append(String(line.dropFirst(2)))
            } else if let dot = line.firstIndex(of: "."), line[..<dot].allSatisfy(\.isNumber), !line[..<dot].isEmpty,
                      line[line.index(after: dot)...].hasPrefix(" ") {
                flushPara()
                if !list.isEmpty && !listOrdered { flushList() }
                listOrdered = true
                list.append(String(line[line.index(dot, offsetBy: 2)...]))
            } else {
                flushList()
                para.append(line)
            }
        }
        if let code { blocks.append(.code(codeLang, code.joined(separator: "\n"))) } // unterminated while streaming
        flushPara()
        flushList()
        return blocks
    }
}
