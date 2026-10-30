namespace AniMeido.Plugin.Player.Sources.Web;

internal static class WebMediaCaptureScripts
{
    internal const string Install =
        """
        (() => {
            document.__animeidoPolledCandidates = new Map();
            if (window.__animeidoCaptureInstalled) return;
            window.__animeidoCaptureInstalled = true;
            window.__animeidoMediaCandidates = [];
            const push = (value, confirmed) => {
                if (typeof value !== "string" || !value) return;
                const candidate = {
                    url: value,
                    confirmed: !!confirmed
                };
                window.__animeidoMediaCandidates.push(candidate);
                try {
                    window.chrome?.webview?.postMessage(candidate);
                } catch {}
            };
            const inspectText = (text, responseUrl) => {
                if (typeof text !== "string") return;
                if (text.trimStart().startsWith("#EXTM3U")) {
                    push(responseUrl, true);
                }
                const matches = text.match(
                    /https?:\/\/[^\s"'<>\\]+?(?:\.m3u8|\.mp4)(?:\?[^\s"'<>\\]*)?/gi);
                if (matches) matches.forEach(url => push(url, false));
            };
            const scanDom = root => {
                if (!root || !root.querySelectorAll) return;
                root.querySelectorAll("video, source").forEach(node => {
                    push(node.currentSrc || node.src || node.getAttribute("src"), false);
                });
            };
            const startObserver = () => {
                scanDom(document);
                if (!document.documentElement) return;
                new MutationObserver(records => {
                    for (const record of records) {
                        if (record.type === "attributes") {
                            const node = record.target;
                            push(node.currentSrc || node.src || node.getAttribute?.("src"), false);
                        }
                        record.addedNodes.forEach(node => {
                            if (node.nodeType === Node.ELEMENT_NODE) {
                                if (node.matches?.("video, source")) {
                                    push(
                                        node.currentSrc || node.src || node.getAttribute("src"),
                                        false);
                                }
                                scanDom(node);
                            }
                        });
                    }
                }).observe(document.documentElement, {
                    subtree: true,
                    childList: true,
                    attributes: true,
                    attributeFilter: ["src"]
                });
            };
            if (document.readyState === "loading") {
                document.addEventListener("DOMContentLoaded", startObserver, { once: true });
            } else {
                startObserver();
            }
            const originalFetch = window.fetch;
            if (originalFetch) {
                window.fetch = async (...args) => {
                    const response = await originalFetch.apply(window, args);
                    try {
                        const type = response.headers.get("content-type") || "";
                        if (/mpegurl|text\/plain|octet-stream/i.test(type)
                            || /\.m3u8(?:\?|$)/i.test(response.url)) {
                            const clone = response.clone();
                            clone.text()
                                .then(text => inspectText(text, clone.url))
                                .catch(() => {});
                        }
                    } catch {}
                    return response;
                };
            }
            const originalOpen = XMLHttpRequest.prototype.open;
            const originalSend = XMLHttpRequest.prototype.send;
            XMLHttpRequest.prototype.open = function(method, url, ...rest) {
                this.__animeidoUrl = String(url);
                return originalOpen.call(this, method, url, ...rest);
            };
            XMLHttpRequest.prototype.send = function(...args) {
                this.addEventListener("loadend", () => {
                    try {
                        if (!this.responseType || this.responseType === "text") {
                            inspectText(
                                this.responseText,
                                this.responseURL || this.__animeidoUrl);
                        }
                    } catch {}
                }, { once: true });
                return originalSend.apply(this, args);
            };
        })();
        """;

    internal const string Poll =
        """
        (() => {
            const seen = document.__animeidoPolledCandidates
                ?? (document.__animeidoPolledCandidates = new Map());
            const candidates = [];
            const append = candidate => {
                const url = candidate.url;
                const confirmed = !!candidate.confirmed;
                if (typeof url !== "string" || !url) return;
                if (seen.has(url) && (seen.get(url) || !confirmed)) return;
                seen.set(url, confirmed);
                candidates.push({ url, confirmed });
            };
            const queued =
                Array.isArray(window.__animeidoMediaCandidates)
                    ? window.__animeidoMediaCandidates.splice(0)
                    : [];
            queued.forEach(append);
            document.querySelectorAll("video, source").forEach(node => {
                const value = node.currentSrc || node.src
                    || node.getAttribute("src");
                if (value) append({
                    url: value,
                    confirmed: false
                });
            });
            performance.getEntriesByType("resource").forEach(entry => {
                if (entry.name) append({
                    url: entry.name,
                    confirmed: false
                });
            });
            return candidates;
        })()
        """;
}
