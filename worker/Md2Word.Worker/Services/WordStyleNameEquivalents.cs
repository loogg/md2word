namespace Md2Word.Worker.Services;

internal static class WordStyleNameEquivalents
{
    private static readonly string[][] Groups =
    [
        ["Normal", "正文"],
        ["List Number", "编号"],
        ["List Paragraph", "列表段落"],
        ["List Bullet", "项目符号"],
        ["Caption", "caption", "题注", "图注"],
        ["CodeBlock", "Code Block", "代码块"],
        ["No Spacing", "无间隔"],
        ["Intense Quote", "明显引用"],
        ["Quote", "引用"],
        ["Heading 1", "heading 1", "标题 1", "标题1"],
        ["Heading 2", "heading 2", "标题 2", "标题2"],
        ["Heading 3", "heading 3", "标题 3", "标题3"],
        ["Heading 4", "heading 4", "标题 4", "标题4"],
        ["Heading 5", "heading 5", "标题 5", "标题5"],
        ["Heading 6", "heading 6", "标题 6", "标题6"],
    ];

    public static IReadOnlyList<string> Alternatives(string name)
    {
        var normalized = name.Trim();
        var group = Groups.FirstOrDefault(candidate =>
            candidate.Contains(normalized, StringComparer.OrdinalIgnoreCase));
        return group is null
            ? []
            : group
                .Where(candidate => !string.Equals(candidate, normalized, StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
    }
}
