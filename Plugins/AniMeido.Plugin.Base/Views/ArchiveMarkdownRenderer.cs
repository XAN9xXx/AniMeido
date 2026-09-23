using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;

namespace AniMeido.Plugin.Base.Views;

// Render into native text elements. Links and images remain text, so stored notes
// cannot inject HTML, run scripts, or fetch arbitrary resources in a WebView.
internal static class ArchiveMarkdownRenderer
{
    private static readonly MarkdownPipeline Pipeline =
        new MarkdownPipelineBuilder().DisableHtml().Build();

    public static void Render(RichTextBlock target, string? source)
    {
        target.Blocks.Clear();
        if (string.IsNullOrWhiteSpace(source))
        {
            target.Blocks.Add(new Paragraph
            {
                Inlines = { new Run { Text = "还没有作品笔记。" } },
            });
            return;
        }

        foreach (var block in Markdown.Parse(source, Pipeline))
        {
            AddBlock(target, block);
        }
    }

    private static void AddBlock(
        RichTextBlock target,
        Markdig.Syntax.Block block,
        string prefix = "")
    {
        if (block is ListBlock list)
        {
            var index = int.TryParse(list.OrderedStart, out var start)
                ? start : 1;
            foreach (var child in list)
            {
                AddBlock(target, child,
                    list.IsOrdered ? $"{index++}. " : "• ");
            }
            return;
        }

        if (block is QuoteBlock quote)
        {
            foreach (var child in quote)
            {
                AddBlock(target, child, "│ ");
            }
            return;
        }

        if (block is ThematicBreakBlock)
        {
            target.Blocks.Add(new Paragraph
            {
                Inlines = { new Run { Text = "────────" } },
            });
            return;
        }

        if (block is ContainerBlock container)
        {
            foreach (var child in container)
            {
                AddBlock(target, child, prefix);
            }
            return;
        }

        if (block is not LeafBlock leaf)
        {
            return;
        }

        var paragraph = new Paragraph
        {
            Margin = new Microsoft.UI.Xaml.Thickness(0, 0, 0, 10),
        };
        if (prefix.Length > 0)
        {
            paragraph.Inlines.Add(new Run { Text = prefix });
        }

        if (block is HeadingBlock heading)
        {
            paragraph.FontSize = heading.Level switch
            {
                1 => 25,
                2 => 21,
                _ => 18,
            };
            paragraph.FontWeight = Microsoft.UI.Text.FontWeights.Bold;
            paragraph.Margin = new Microsoft.UI.Xaml.Thickness(0, 14, 0, 10);
        }

        if (block is CodeBlock)
        {
            paragraph.FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas");
            paragraph.FontSize = 14;
            paragraph.Margin = new Microsoft.UI.Xaml.Thickness(0, 8, 0, 14);
            paragraph.Inlines.Add(new Run { Text = leaf.Lines.ToString() });
        }
        else if (leaf.Inline is { } inline)
        {
            AddInline(paragraph.Inlines, inline);
        }
        else if (leaf.Lines.Count > 0)
        {
            paragraph.Inlines.Add(new Run { Text = leaf.Lines.ToString() });
        }

        target.Blocks.Add(paragraph);
    }

    private static void AddInline(InlineCollection output, ContainerInline parent)
    {
        for (var child = parent.FirstChild; child is not null;
             child = child.NextSibling)
        {
            switch (child)
            {
                case LiteralInline literal:
                    output.Add(new Run { Text = literal.Content.ToString() });
                    break;
                case CodeInline code:
                    output.Add(new Run
                    {
                        Text = code.Content,
                        FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
                    });
                    break;
                case LineBreakInline:
                    output.Add(new LineBreak());
                    break;
                case HtmlEntityInline entity:
                    // “&amp;”这类实体按解码后的字符显示，不能丢掉。
                    output.Add(new Run { Text = entity.Transcoded.ToString() });
                    break;
                case AutolinkInline autolink:
                    // <https://…> 只显示链接文字，不可点击。
                    output.Add(new Run { Text = autolink.Url });
                    break;
                case EmphasisInline emphasis:
                    var span = new Span();
                    if (emphasis.DelimiterCount >= 2)
                    {
                        span.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
                    }
                    else
                    {
                        span.FontStyle = Windows.UI.Text.FontStyle.Italic;
                    }
                    AddInline(span.Inlines, emphasis);
                    output.Add(span);
                    break;
                case LinkInline link:
                    // Do not navigate or load images from untrusted note text.
                    if (link.IsImage)
                    {
                        output.Add(new Run { Text = "[图片] " });
                    }
                    AddInline(output, link);
                    break;
                case ContainerInline nested:
                    AddInline(output, nested);
                    break;
            }
        }
    }
}
