using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using HtmlAgilityPack;

namespace AniMeido.Plugin.Player.Sources.EasyBangumi;

internal sealed class EasyHtmlDocument
{
    private readonly IDocument _document;
    internal EasyHtmlQueryCache Queries { get; } = new();

    public EasyHtmlDocument(string html, string? baseUrl)
    {
        Html = html;
        BaseUrl = baseUrl;
        _document = new HtmlParser().ParseDocument(html);
    }

    internal string Html { get; }

    internal string? BaseUrl { get; }

    internal HtmlNode XPathRoot => Queries.GetXPathRoot(this);

    public EasyElementList select(string selector)
        => new(_document.QuerySelectorAll(selector)
            .Select(element => new EasyHtmlElement(element, BaseUrl, Queries)));

    public string title() => _document.Title ?? string.Empty;
}

internal sealed class EasyHtmlElement
{
    private readonly IElement? _element;
    private readonly HtmlNode? _node;
    internal EasyHtmlQueryCache Queries { get; }

    public EasyHtmlElement(IElement element, string? baseUrl, EasyHtmlQueryCache queries)
    {
        _element = element;
        BaseUrl = baseUrl;
        Queries = queries;
    }

    public EasyHtmlElement(HtmlNode node, string? baseUrl, EasyHtmlQueryCache queries)
    {
        _node = node;
        BaseUrl = baseUrl;
        Queries = queries;
    }

    internal string? BaseUrl { get; }

    internal string Html => _element?.OuterHtml ?? _node?.OuterHtml ?? string.Empty;

    // 保留片段重新作为文档查询的既有范围；直接查询原节点会改变自身和祖先的可见性。
    internal IDocument SelectionDocument => Queries.GetSelectionDocument(this);

    internal HtmlNode XPathRoot => Queries.GetXPathRoot(this);

    public EasyElementList select(string selector)
        => new(SelectionDocument.QuerySelectorAll(selector)
            .Select(element => new EasyHtmlElement(element, BaseUrl, Queries)));

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
                ? new EasyHtmlElement(_element.Children[index], BaseUrl, Queries)
                : null;
        }

        var children = _node?.ChildNodes
            .Where(node => node.NodeType == HtmlNodeType.Element)
            .ToArray() ?? [];
        return index >= 0 && index < children.Length
            ? new EasyHtmlElement(children[index], BaseUrl, Queries)
            : null;
    }

    public EasyElementList children()
    {
        if (_element is not null)
        {
            return new EasyElementList(_element.Children
                .Select(child => new EasyHtmlElement(child, BaseUrl, Queries)));
        }

        var children = _node?.ChildNodes
            .Where(node => node.NodeType == HtmlNodeType.Element)
            ?? Enumerable.Empty<HtmlNode>();
        return new EasyElementList(children.Select(
            node => new EasyHtmlElement(node, BaseUrl, Queries)));
    }
}

/// <summary>一份脚本文档只复用当前片段，避免逐行查询时保留每行的完整解析树。</summary>
internal sealed class EasyHtmlQueryCache
{
    private EasyHtmlElement? _selectionOwner;
    private IDocument? _selectionDocument;
    private object? _xpathOwner;
    private HtmlNode? _xpathRoot;

    internal IDocument GetSelectionDocument(EasyHtmlElement owner)
    {
        if (!ReferenceEquals(_selectionOwner, owner))
        {
            var document = new HtmlParser().ParseDocument(owner.Html);
            _selectionDocument = document;
            _selectionOwner = owner;
        }
        return _selectionDocument!;
    }

    internal HtmlNode GetXPathRoot(object owner)
    {
        if (!ReferenceEquals(_xpathOwner, owner))
        {
            var html = owner switch
            {
                EasyHtmlDocument document => document.Html,
                EasyHtmlElement element => element.Html,
                _ => string.Empty,
            };
            var documentRoot = EasyXPathFacade.ParseHtml(html);
            _xpathRoot = documentRoot;
            _xpathOwner = owner;
        }
        return _xpathRoot!;
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
        var (root, baseUrl, queries) = GetRoot(value);
        var nodes = root.SelectNodes(xpath);
        return new EasyElementList(
            (nodes?.AsEnumerable() ?? Enumerable.Empty<HtmlNode>())
            .Select(node => new EasyHtmlElement(node, baseUrl, queries)));
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

    internal static HtmlNode ParseHtml(string html)
    {
        var document = new HtmlDocument();
        document.LoadHtml(html);
        return document.DocumentNode;
    }

    private static (HtmlNode Root, string? BaseUrl, EasyHtmlQueryCache Queries) GetRoot(object value)
        => value switch
        {
            EasyHtmlDocument document => (document.XPathRoot, document.BaseUrl, document.Queries),
            EasyHtmlElement element => (element.XPathRoot, element.BaseUrl, element.Queries),
            _ => (ParseHtml(string.Empty), null, new EasyHtmlQueryCache()),
        };
}
