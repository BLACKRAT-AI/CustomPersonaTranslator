using System.Collections.Generic;
using System.Text;
using CPT.Core.Models;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace CPT.Core.Filter;

// Pure function: markdown + persona filter config -> speakable text.
// Skipped blocks become brief spoken announcements when AnnounceSkippedBlocks=true.
public static class ContentFilter
{
    private static readonly MarkdownPipeline Pipeline =
        new MarkdownPipelineBuilder().UsePipeTables().UseGridTables().Build();

    public static string ToSpoken(string markdown, Persona persona)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return "";

        var doc = Markdown.Parse(markdown, Pipeline);
        var sb = new StringBuilder();
        var mode = persona.FilterMode;
        var announce = persona.AnnounceSkippedBlocks;

        foreach (var block in doc)
        {
            switch (block)
            {
                case HeadingBlock h:
                    if (mode != ContentFilterMode.StrictProse)
                        Append(sb, InlineText(h.Inline) + ".");
                    else
                        Append(sb, InlineText(h.Inline));
                    break;

                case ParagraphBlock p:
                    Append(sb, InlineText(p.Inline));
                    break;

                case QuoteBlock q:
                    Append(sb, $"Quote: {BlockText(q)}");
                    break;

                case ListBlock l:
                    HandleList(sb, l, mode, announce);
                    break;

                case FencedCodeBlock fc:
                    if (announce)
                    {
                        var lang = string.IsNullOrEmpty(fc.Info) ? "code" : fc.Info;
                        Append(sb, $"{lang} block shown on screen.");
                    }
                    break;

                case CodeBlock:
                    if (announce) Append(sb, "Code block shown on screen.");
                    break;

                case Markdig.Extensions.Tables.Table:
                    if (announce) Append(sb, "Table shown on screen.");
                    break;

                case ThematicBreakBlock:
                    break; // skip

                default:
                    // Unknown block — try to extract any prose.
                    var fallback = BlockText(block);
                    if (!string.IsNullOrWhiteSpace(fallback)) Append(sb, fallback);
                    break;
            }
        }

        var result = sb.ToString().Trim();
        if (result.Length == 0 && announce) return "Response shown on screen.";
        return result;
    }

    private static void HandleList(StringBuilder sb, ListBlock list, ContentFilterMode mode, bool announce)
    {
        var items = new List<string>();
        foreach (var item in list)
        {
            if (item is ListItemBlock li)
            {
                var t = BlockText(li).Trim();
                if (!string.IsNullOrEmpty(t)) items.Add(t);
            }
        }
        if (items.Count == 0) return;

        if (mode == ContentFilterMode.StrictProse)
        {
            if (announce) Append(sb, $"List of {items.Count} items shown on screen.");
            return;
        }

        if (items.Count <= 5)
        {
            foreach (var it in items) Append(sb, it + ".");
        }
        else
        {
            if (announce) Append(sb, $"List of {items.Count} items shown on screen.");
            else
            {
                // Speak first three to give the gist.
                for (int i = 0; i < 3; i++) Append(sb, items[i] + ".");
                Append(sb, $"And {items.Count - 3} more.");
            }
        }
    }

    private static string InlineText(ContainerInline? inline)
    {
        if (inline is null) return "";
        var sb = new StringBuilder();
        foreach (var i in inline)
        {
            switch (i)
            {
                case LiteralInline lit: sb.Append(lit.Content.ToString()); break;
                case EmphasisInline em: sb.Append(InlineText(em)); break;
                case LinkInline link:
                    var label = InlineText(link);
                    if (link.IsImage) sb.Append(string.IsNullOrEmpty(label) ? "Image shown on screen." : label);
                    else sb.Append(label);
                    break;
                case CodeInline code:
                    var c = code.Content;
                    sb.Append(c.Length < 16 ? c : "code shown on screen");
                    break;
                case AutolinkInline:
                    sb.Append("link");
                    break;
                case LineBreakInline: sb.Append(' '); break;
                case HtmlInline: break;
                default:
                    if (i is ContainerInline ci) sb.Append(InlineText(ci));
                    break;
            }
        }
        return sb.ToString();
    }

    private static string BlockText(Block b)
    {
        var sb = new StringBuilder();
        if (b is LeafBlock leaf && leaf.Inline is not null) sb.Append(InlineText(leaf.Inline));
        else if (b is ContainerBlock cb)
            foreach (var child in cb) sb.Append(BlockText(child)).Append(' ');
        return sb.ToString().Trim();
    }

    private static void Append(StringBuilder sb, string s)
    {
        s = s.Trim();
        if (s.Length == 0) return;
        if (sb.Length > 0) sb.Append(' ');
        sb.Append(s);
        if (!s.EndsWith('.') && !s.EndsWith('?') && !s.EndsWith('!') && !s.EndsWith(':'))
            sb.Append('.');
    }
}
