/**
 * Yomic Extension: MikoRoku (mikoroku)
 * Ported from Keiyoushi id extensions-source (src/id/mikoroku)
 * Pure JavaScript compatible with Jint 4.5.0 + AngleSharp 1.10.1
 */

var source = {
    name: "MikoRoku",
    baseUrl: "https://mikoroku.com",
    apiUrl: "https://raw.githubusercontent.com/moemaomao/mymangadata/main/all-manga.json",
    language: "id",
    version: "1.1.0",
    description: "Baca Manga Bahasa Indonesia di MikoRoku",
    author: "DesktopKomik",
    iconUrl: "https://raw.githubusercontent.com/keiyoushi/extensions-source/main/src/id/mikoroku/res/mipmap-xxhdpi/ic_launcher.png",
    iconBackground: "#0f172a",
    iconForeground: "#38bdf8",
    isNsfw: false,
    isHasMorePages: false,
    pageSize: 20,
    genres: [],
    formats: [],

    requestUrl: function(url, headers) {
        let reqHeaders = headers || {};
        if (!reqHeaders["User-Agent"]) {
            reqHeaders["User-Agent"] = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36";
        }
        if (!reqHeaders["Referer"]) {
            reqHeaders["Referer"] = this.baseUrl + "/";
        }
        try {
            return fetch(url, { headers: reqHeaders });
        } catch (e) {
            return null;
        }
    },

    cachedCatalog: null,

    fetchCatalog: function() {
        if (this.cachedCatalog) return this.cachedCatalog;
        try {
            let res = this.requestUrl(this.apiUrl);
            if (!res || res.status !== 200 || !res.body) return [];
            let parsed = JSON.parse(res.body);
            this.cachedCatalog = Array.isArray(parsed) ? parsed : [];
            return this.cachedCatalog;
        } catch (e) {
            return [];
        }
    },

    getPopularManga: function(page) {
        if ((page || 1) > 1) return { items: [], totalPages: 1 };
        let list = this.fetchCatalog();
        let sorted = list.slice().sort((a, b) => (parseFloat(b.rating) || 0) - (parseFloat(a.rating) || 0));
        let items = sorted.map(item => ({
            title: item.title,
            url: "/detail.html?slug=" + item.slug,
            thumbnailUrl: item.img,
            status: 0
        }));
        return { items: items, totalPages: 1 };
    },

    getLatestUpdates: function(page) {
        if ((page || 1) > 1) return { items: [], totalPages: 1 };
        let list = this.fetchCatalog();
        let items = list.map(item => ({
            title: item.title,
            url: "/detail.html?slug=" + item.slug,
            thumbnailUrl: item.img,
            status: 0
        }));
        return { items: items, totalPages: 1 };
    },

    getSearchManga: function(query, page) {
        if ((page || 1) > 1) return { items: [], totalPages: 1 };
        let list = this.fetchCatalog();
        let q = (query || "").toLowerCase();
        let filtered = list.filter(item => {
            let t = (item.title || "").toLowerCase();
            let alt = (item.altTitle || "").toLowerCase();
            return t.includes(q) || alt.includes(q);
        });
        let items = filtered.map(item => ({
            title: item.title,
            url: "/detail.html?slug=" + item.slug,
            thumbnailUrl: item.img,
            status: 0
        }));
        return { items: items, totalPages: 1 };
    },

        getMangaList: function(page, status, genre, type, sort) {
        if (sort === "latest") {
            return this.getLatestUpdates(page);
        }
        return this.getPopularManga(page);
    },

    getMangaDetails: function(url) {
        let slugMatch = url.match(/slug=([^&]+)/);
        let slug = slugMatch ? slugMatch[1] : url.replace(/^\/+|\/+$/g, "");
        let list = this.fetchCatalog();
        let item = list.find(m => m.slug === slug);
        if (!item) {
            return { title: "", url: url, thumbnailUrl: "", author: "", status: 0, description: "", genre: [] };
        }

        let status = 0;
        let st = (item.status || "").toLowerCase();
        if (st.includes("ongoing")) status = 1;
        else if (st.includes("completed")) status = 2;

        let genres = Array.isArray(item.genres) ? item.genres : (item.genres ? [item.genres] : []);

        return {
            title: item.title || "",
            url: url,
            thumbnailUrl: item.img || "",
            author: item.author || "",
            status: status,
            description: item.desc || "",
            genre: genres
        };
    },

    getChapterList: function(url) {
        let slugMatch = url.match(/slug=([^&]+)/);
        let slug = slugMatch ? slugMatch[1] : url.replace(/^\/+|\/+$/g, "");
        let list = this.fetchCatalog();
        let item = list.find(m => m.slug === slug);
        if (!item) return [];

        let chs = [];
        let seenUrls = {};
        try {
            const pageSize = 150;
            for (let startIndex = 1; startIndex <= 1500; startIndex += pageSize) {
                let feedUrl = "https://www.mikodrive.my.id/feeds/posts/default?alt=json&max-results=" + pageSize + "&start-index=" + startIndex + "&q=" + encodeURIComponent(item.title);
                let res = this.requestUrl(feedUrl);
                if (!res || res.status !== 200 || !res.body) break;
                let json = JSON.parse(res.body);
                let entries = (json && json.feed && Array.isArray(json.feed.entry)) ? json.feed.entry : [];
                if (entries.length === 0) break;
                for (let entry of entries) {
                    let title = entry.title && entry.title.$t ? entry.title.$t : "";
                    let links = entry.link || [];
                    let chUrl = "";
                    for (let l of links) {
                        if (l.rel === "alternate") {
                            chUrl = l.href;
                            break;
                        }
                    }
                    if (chUrl && !seenUrls[chUrl]) {
                        seenUrls[chUrl] = true;
                        chs.push({
                            name: title,
                            url: chUrl,
                            uploadDate: entry.published && entry.published.$t ? entry.published.$t : ""
                        });
                    }
                }
                if (entries.length < pageSize) break;
            }
        } catch (e) {}

        return chs;
    },

    getPageList: function(url) {
        if (!url || !url.startsWith("http")) return [];
        let res = this.requestUrl(url);
        if (!res || res.status !== 200 || !res.body) return [];
        let doc = Html.parse(res.body, url);
        let imgs = doc.querySelectorAll(".post-body img, .entry-content img, article img");
        let list = [];
        let seen = {};
        for (let img of imgs) {
            let src = ((el) => {
                let cands = [el.absUrl("data-lazy-src") || el.attr("data-lazy-src"), el.absUrl("data-src") || el.attr("data-src"), el.absUrl("data-original") || el.attr("data-original"), el.absUrl("data-cfsrc") || el.attr("data-cfsrc"), el.absUrl("src") || el.attr("src")];
                for (let c of cands) {
                    if (!c) continue;
                    let low = c.trim().toLowerCase();
                    if (!low || low.startsWith("data:") || low.includes("loading.gif") || low.includes("loader.gif") || low.includes("placeholder") || low.includes("blank.gif") || low.includes("spacer.gif")) continue;
                    return c.trim();
                }
                return "";
            })(img);
            if (src && !seen[src] && !src.includes("icon") && !src.includes("banner")) {
                seen[src] = true;
                list.push(src);
            }
        }
        return list;
    }
};