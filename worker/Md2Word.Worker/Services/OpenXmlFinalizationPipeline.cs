namespace Md2Word.Worker.Services;

internal sealed record OpenXmlFinalizationResult(
    ListFinalizationResult Lists,
    TableHeaderFinalizationResult Tables,
    TableLayoutFinalizationResult TableLayout,
    TableSeparatorFinalizationResult TableSeparators,
    RoleStyleFinalizationResult Roles,
    InlineCodeFinalizationResult InlineCode);

internal sealed class OpenXmlFinalizationPipeline
{
    private readonly OpenXmlListFinalizer listFinalizer = new();

    public OpenXmlFinalizationResult Finalize(
        string docxPath,
        IReadOnlyDictionary<string, ResolvedStyle> resolvedRoleStyles,
        bool bodyOnly,
        bool repeatTableHeaders = true)
    {
        // Word's imported manual-body-item-* styles are the only signal that an
        // unnumbered paragraph is a loose-list continuation. Normalize lists
        // before role markers replace those transient import styles.
        var lists = listFinalizer.Finalize(docxPath, resolvedRoleStyles, bodyOnly);
        var tables = OpenXmlTableHeaderFinalizer.Finalize(docxPath, repeatTableHeaders, bodyOnly);
        var roles = OpenXmlRoleStyleFinalizer.Finalize(docxPath, resolvedRoleStyles, bodyOnly);
        // Role finalization deliberately removes direct paragraph formatting.
        // Apply table-specific compact paragraph geometry afterwards so table
        // cells retain their Word style while no longer inheriting body first-
        // line indents and full justification.
        var tableLayout = OpenXmlTableLayoutFinalizer.Finalize(docxPath, bodyOnly);
        var tableSeparators = OpenXmlTableSeparatorFinalizer.Finalize(
            docxPath,
            resolvedRoleStyles,
            bodyOnly);
        var inlineCode = OpenXmlInlineCodeFinalizer.Finalize(docxPath, resolvedRoleStyles, bodyOnly);
        return new OpenXmlFinalizationResult(
            lists,
            tables,
            tableLayout,
            tableSeparators,
            roles,
            inlineCode);
    }
}
