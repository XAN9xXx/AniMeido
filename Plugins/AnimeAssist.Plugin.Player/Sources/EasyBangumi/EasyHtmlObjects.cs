using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using HtmlAgilityPack;

namespace AniMeido.Plugin.Player.Sources.EasyBangumi;

internal sealed class EasyHtmlDocument
{
    private readonly IDocument _document;

    public EasyHtmlDocument(string html, string? baseUrl)
    {
        Html = html;
        BaseUrl = baseUrl;
        _document = new HtmlParser().ParseDocument(html);
    }

    internal string Html { get; }

    internal string? BaseUrl { get; }

    public EasyElementList select(string selector)
        => new(_document.QuerySelectorAll(selector)
            .Select(element => new EasyHtmlElement(element, BaseUrl)));

    public string title() => _document.Title ?? string.Empty;
}

internal sealed class EasyHtmlElement
{
    private readonly IElement? _element;
    private readonly HtmlNode? _node;

    public EasyHtmlElement(IElement element, string? baseUrl)
    {
        _element = element;
        BaseUrl = baseUrl;
    }

    public EasyHtmlElement(HtmlNode node, string? baseUrl)
    {
        _node = node;
        BaseUrl = baseUrl;
    }

    internal string? BaseUrl { get; }

    internal string Html => _element?.OuterHtml ?? _node?.OuterHtml ?? string.Empty;

    public EasyElementList select(string selector)
    {
        var document = new HtmlParser().ParseDocument(Html);
        return new EasyElementList(document.QuerySelectorAll(selector)
            .Select(element => new EasyHtmlElement(element, BaseUrl)));
    }

    public string text()
        => (_element?.TextContent ?? _node?.InnerText ?? string.Empty).Trim();

    public string attr(string name)
        => _element?.GetAttribute(name)
            ?? _node?.GetAttributeValue(name, string.Empty)
            ?? string.Empty;

    public EasyHtmlElement? first() => this;

    public EasyHtmlElement? child(int index)
    {
        if (_element is not null)
        {
            return index >= 0 && index < _element.Children.Length
                ? new EasyHtmlElement(_element.Children[index], BaseUrl)
                : null;
        }

        var children = _node?.ChildNodes
            .Where(node => node.NodeType == HtmlNodeType.Element)
            .ToArray() ?? [];
        return index >= 0 && index < children.Length
            ? new EasyHtmlElement(children[index], BaseUrl)
            : null;
    }

    public EasyElementList children()
    {
        if (_element is not null)
        {
            return new EasyElementList(_element.Children
                .Select(child => new EasyHtmlElement(child, BaseUrl)));
        }

        var children = _node?.ChildNodes
            .Where(node => node.NodeType == HtmlNodeType.Element)
            ?? Enumerable.Empty<HtmlNode>();
        return new EasyElementList(children.Select(
            node => new EasyHtmlElement(node, BaseUrl)));
    }
}

internal sealed class EasyElementList
{
    private readonly IReadOnlyList<EasyHtmlElement> _items;

    public EasyElementList(IEnumerable<EasyHtmlElement> items)
    {
        _items = items.ToArray();
    }

    public int size() => _items.Count;

    public bool isEmpty() => _items.Count == 0;

    public EasyHtmlElement? get(int index)
        => index >= 0 && index < _items.Count ? _items[index] : null;

    public EasyHtmlElement? first() => get(0);

    public string text()
        => string.Join(" ", _items.Select(item => item.text()));

    public EasyIterator iterator() => new(_items);
}

internal sealed class EasyIterator
{
    private readonly IReadOnlyList<EasyHtmlElement> _items;
    private int _index;

    public EasyIterator(IReadOnlyList<EasyHtmlElement> items)
    {
        _items = items;
    }

    public bool hasNext() => _index < _items.Count;

    public EasyHtmlElement? next()
        => hasNext() ? _items[_index++] : null;
}

internal sealed class EasyXPathFacade
{
    public EasyElementList nodes(object value, string xpath)
    {
        var (html, baseUrl) = GetHtml(value);
        var document = new HtmlDocument();
        document.LoadHtml(html);
        var nodes = document.DocumentNode.SelectNodes(xpath);
        return new EasyElementList(
            (nodes?.AsEnumerable() ?? Enumerable.Empty<HtmlNode>())
            .Select(node => new EasyHtmlElement(node, baseUrl)));
    }

    public string text(object value, string xpath)
        => nodes(value, xpath).first()?.text() ?? string.Empty;

    public string textSelf(object value)
        => value is EasyHtmlElement element ? element.text() : string.Empty;

    public string attr(object value, string xpath, string name)
        => nodes(value, xpath).first()?.attr(name) ?? string.Empty;

    public string attrSelf(object value, string name)
        => value is EasyHtmlElement element
            ? element.attr(name)
            : string.Empty;

    public string firstImage(object value)
        => value switch
        {
            EasyHtmlDocument document =>
                document.select("img").first()?.attr("src") ?? string.Empty,
            EasyHtmlElement element =>
                element.select("img").first()?.attr("src") ?? string.Empty,
            _ => string.Empty,
        };

    public string title(object value)
        => value is EasyHtmlDocument document
            ? document.title()
            : string.Empty;

    private static (string Html, string? BaseUrl) GetHtml(object value)
        => value switch
        {
            EasyHtmlDocument document => (document.Html, document.BaseUrl),
            EasyHtmlElement element => (element.Html, element.BaseUrl),
            _ => (string.Empty, null),
        };
}
