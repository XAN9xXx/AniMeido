namespace AniMeido.Plugin.Player.Sources.EasyBangumi;

internal static class EasyBangumiPrelude
{
    public const string Script = """
function ArrayList() { this.items = []; }
ArrayList.prototype.add = function(v) { this.items.push(v); return true; };
ArrayList.prototype.get = function(i) { return this.items[i]; };
ArrayList.prototype.size = function() { return this.items.length; };
ArrayList.prototype.iterator = function() {
    var items = this.items, index = 0;
    return { hasNext: function(){ return index < items.length; },
             next: function(){ return items[index++]; } };
};

function HashMap() { this.map = Object.create(null); }
HashMap.prototype.put = function(k,v) { this.map[String(k)] = v; return v; };
HashMap.prototype.get = function(k) { return this.map[String(k)]; };
HashMap.prototype.containsKey = function(k) {
    return Object.prototype.hasOwnProperty.call(this.map, String(k));
};
HashMap.prototype.entrySet = function() {
    var entries = Object.keys(this.map).map(function(k) {
        return { getKey: function(){ return k; },
                 getValue: function(){ return this.map[k]; }.bind(this) };
    }.bind(this));
    return { iterator: function() {
        var i = 0;
        return { hasNext: function(){ return i < entries.length; },
                 next: function(){ return entries[i++]; } };
    }};
};
HashMap.prototype.toObject = function() { return this.map; };

function Pair(first, second) { this.first = first; this.second = second; }
function Episode(id, label, order) {
    this.id = String(id); this.label = String(label); this.order = order || 0;
}
function PlayLine(id, label, episode) {
    this.id = String(id); this.label = String(label); this.episode = episode;
}
function PlayerInfo(type, url) {
    this.type = type; this.url = String(url); this.header = new HashMap();
}
PlayerInfo.DECODE_TYPE_OTHER = 0;
PlayerInfo.DECODE_TYPE_HLS = 1;
function JsVideoStrategy(url, userAgent, headers, cookies, timeout, legacy) {
    this.url = String(url); this.userAgent = String(userAgent || "");
    this.headers = headers || new HashMap(); this.cookies = cookies;
    this.timeout = Number(timeout || 30000); this.legacy = !!legacy;
}
function ParserException(message) { this.name = "ParserException"; this.message = message; }
ParserException.prototype = Object.create(Error.prototype);
function JSONObject(value) { this.value = JSON.parse(String(value)); }
JSONObject.prototype.getJSONArray = function(key) {
    var values = this.value[String(key)] || [];
    return {
        length: function(){ return values.length; },
        getJSONObject: function(i){ var result = new JSONObject("{}"); result.value = values[i]; return result; }
    };
};
JSONObject.prototype.getString = function(key) {
    var value = this.value[String(key)];
    return value == null ? "" : String(value);
};
function Cartoon() {}
Cartoon.STATUS_UNKNOWN = 0;
Cartoon.UPDATE_STRATEGY_ALWAYS = 0;
var SourcePreference = { Edit: function(group, key, value) {
    this.group = group; this.key = key; this.value = value;
}};
function makeCartoonCover(v) { return v; }
function makeCartoon(v) { return v; }
function MainTab(label, type) { this.label = label; this.type = type; }
MainTab.MAIN_TAB_WITH_COVER = 1; MainTab.MAIN_TAB_GROUP = 2;
function SubTab(label, active, ext) {
    this.label = label; this.active = active; this.ext = ext;
}

var source = { key: __sourceId };
var Log = { i: function(){}, d: function(){}, e: function(){} };
var JSLogUtils = Log;
var System = { currentTimeMillis: function(){ return Date.now(); } };
var Long = { parseLong: function(v){ return Number(v); } };
var URLEncoder = { encode: function(v){ return encodeURIComponent(String(v)); } };
var URLDecoder = { decode: function(v){ return decodeURIComponent(String(v)); } };
var URI = { create: function(v) {
    var u = new URL(String(v));
    return { getRawQuery: function(){ return u.search.substring(1); },
             getScheme: function(){ return u.protocol.replace(":",""); },
             getHost: function(){ return u.host; },
             getPath: function(){ return u.pathname; } };
}};
var SourceUtils = { urlParser: function(baseUrl, value) {
    return __host.ResolveUrl(String(baseUrl), String(value));
}};
var JSSourceUtils = SourceUtils;
var XPathUtils = __xpath;
var Jsoup = { parse: function(html, baseUrl) {
    return __host.ParseHtml(String(html), baseUrl == null ? null : String(baseUrl));
}};

function __headersJson(headers) {
    if (headers == null) return "{}";
    if (headers.toObject) return JSON.stringify(headers.toObject());
    return JSON.stringify(headers);
}
function __call(req) {
    return { execute: function() {
        return __host.Execute(
            String(req.url), String(req.method || "GET"),
            __headersJson(req.headers), req.body == null ? null : String(req.body));
    }};
}
var OkhttpUtils = {
    get: function(url, headers) {
        return { url: String(url), method: "GET", headers: headers || new HashMap() };
    },
    postFromBody: function(url, body, headers) {
        var pairs = [];
        if (body && body.toObject) {
            var object = body.toObject();
            Object.keys(object).forEach(function(k) {
                pairs.push(encodeURIComponent(k) + "=" + encodeURIComponent(object[k]));
            });
        }
        return { url: String(url), method: "POST", headers: headers || new HashMap(),
                 body: pairs.join("&") };
    }
};
var Inject_NetworkHelper = { defaultLinuxUA: __host.DefaultUserAgent };
var Inject_PreferenceHelper = { get: function(k,d) {
    return __host.GetPreference(String(k), String(d));
}};
var __httpClient = { newCall: __call };
var Inject_OkhttpHelper = {
    client: __httpClient,
    cloudflareWebViewClient: __httpClient
};
var Inject_RenderHelper = { renderVideoFromJs: function(strategy) {
    return __host.ResolveVideo(
        strategy.url, strategy.userAgent,
        __headersJson(strategy.headers), strategy.timeout,
        strategy.actionJs == null ? null : String(strategy.actionJs),
        !!(strategy.useLegacyParser || strategy.legacy));
}};
var Inject_WebViewHelperV2 = Inject_RenderHelper;
var Inject_WebProxyProvider = { getWebProxy: function() {
    var currentUrl = "", content = "";
    return {
        loadUrl: function(url) { currentUrl = String(url); content = __host.LoadPage(currentUrl, false); },
        waitingForPageLoaded: function(){},
        getContentWithIframe: function(){ return content; },
        needUserCheck: function(){ content = __host.LoadPage(currentUrl, true); },
        close: function(){}
    };
}};
var Inject_CaptchaHelper = {};

function __animeido_search(keyword) {
    var pair = SearchComponent_search(0, String(keyword));
    var values = pair == null || pair.second == null
        ? [] : (pair.second.items || pair.second);
    return JSON.stringify(values.map(function(v) {
        return { id: String(v.id), title: String(v.title || ""),
                 url: String(v.url || ""), source: String(v.source || __sourceId),
                 cover: String(v.cover || "") };
    }));
}
function __animeido_episodes(summaryJson) {
    var summary = JSON.parse(summaryJson);
    var pair = DetailedComponent_getDetailed(summary);
    var lines = pair == null || pair.second == null
        ? [] : (pair.second.items || pair.second);
    var result = [];
    lines.forEach(function(line) {
        var episodes = line.episode == null ? [] : (line.episode.items || line.episode);
        episodes.forEach(function(ep) {
            result.push({ playLineId: String(line.id), route: String(line.label || ""),
                          episodeId: String(ep.id), title: String(ep.label || ep.id) });
        });
    });
    return JSON.stringify(result);
}
function __animeido_resolve(summaryJson, playLineId, route, episodeId, title) {
    var summary = JSON.parse(summaryJson);
    var line = new PlayLine(playLineId, route, new ArrayList());
    var episode = new Episode(episodeId, title, 0);
    var info = PlayComponent_getPlayInfo(summary, line, episode);
    var headers = info.header && info.header.toObject ? info.header.toObject() : {};
    return JSON.stringify({ url: String(info.url || ""), headers: headers });
}
""";
}
