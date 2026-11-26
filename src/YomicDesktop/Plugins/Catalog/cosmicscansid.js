/**
 * Yomic Extension: CosmicScans (cosmicscansid)
 * Ported from Keiyoushi id extensions-source (src/id/cosmicscansid)
 * Pure JavaScript compatible with Jint 4.5.0 + AngleSharp 1.10.1
 */

var source = {
    name: "CosmicScans",
    baseUrl: "https://04.cosmicscans.to",
    apiUrl: "https://cdncid.csmcscns.id/v1/manga",
    language: "id",
    version: "1.1.0",
    description: "Baca Komik Manhwa & Manga di CosmicScans",
    author: "DesktopKomik",
    iconUrl: "https://raw.githubusercontent.com/keiyoushi/extensions-source/main/src/id/cosmicscansid/res/mipmap-xxhdpi/ic_launcher.png",
    iconBackground: "#0f172a",
    iconForeground: "#38bdf8",
    isNsfw: false,
    isHasMorePages: true,
    pageSize: 24,
    genres: ['Action', 'Adult', 'Adventure', 'Comedy', 'Cooking', 'Crime', 'Cultivation', 'Demon', 'Demons', 'Drama', 'Ecchi', 'Fantasy', 'Game', 'Gender Bender', 'Gore', 'Harem', 'Historical', 'Horror', 'Isekai', 'Josei', 'Magic', 'Martial Arts', 'Mature', 'Mecha', 'Medical', 'Military', 'Music', 'Mystery', 'Psychological', 'Reincarnation', 'Romance', 'School', 'School Life', 'Sci-Fi', 'Seinen', 'Shoujo', 'Shounen', 'Slice of Life', 'Smut', 'Sports', 'Super Power', 'Supernatural', 'Survival', 'Thriller', 'Time Travel', 'Tragedy', 'Webtoon'],
    formats: ['Manga', 'Manhwa', 'Manhua'],

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

    safeParseJson: function(body) {
        if (!body || typeof body !== "string") return null;
        let trimmed = body.trim();
        if (!trimmed || (trimmed[0] !== "{" && trimmed[0] !== "[")) return null;
        try {
            return JSON.parse(trimmed);
        } catch (e) {
            return null;
        }
    },

    extractSlug: function(rawUrl) {
        if (!rawUrl) return "";
        let cleaned = String(rawUrl).split("?")[0].split("#")[0];
        cleaned = cleaned.replace(/^https?:\/\/[^\/]+/i, "");
        cleaned = cleaned.replace(/^\/+/, "").replace(/\/+$/, "");
        cleaned = cleaned.replace(/^(series|manga|chapter)\//i, "");
        if (cleaned.includes("/")) {
            let parts = cleaned.split("/").filter(Boolean);
            return parts.length > 0 ? parts[parts.length - 1] : "";
        }
        return cleaned;
    },

    getPopularManga: function(page) {
        let p = Math.max(1, page || 1);
        let url = this.apiUrl + "/filter?limit=24&order_by=popular&page=" + p;
        let res = this.requestUrl(url);
        if (!res || res.status !== 200 || !res.body) return { items: [], totalPages: p };
        let json = this.safeParseJson(res.body);
        if (!json) return { items: [], totalPages: p };
        let list = json.data || [];
        let items = list.map(item => ({
            title: item.title,
            url: "/series/" + item.slug,
            thumbnailUrl: item.cover,
            status: 0
        }));
        let hasNext = Boolean(json.cursor && json.cursor.hasNext) || list.length >= 24;
        return { items: items, totalPages: hasNext ? p + 1 : p };
    },

    getLatestUpdates: function(page) {
        let p = Math.max(1, page || 1);
        let url = this.apiUrl + "/filter?limit=24&order_by=update&page=" + p;
        let res = this.requestUrl(url);
        if (!res || res.status !== 200 || !res.body) return { items: [], totalPages: p };
        let json = this.safeParseJson(res.body);
        if (!json) return { items: [], totalPages: p };
        let list = json.data || [];
        let items = list.map(item => ({
            title: item.title,
            url: "/series/" + item.slug,
            thumbnailUrl: item.cover,
            status: 0
        }));
        let hasNext = Boolean(json.cursor && json.cursor.hasNext) || list.length >= 24;
        return { items: items, totalPages: hasNext ? p + 1 : p };
    },

    getSearchManga: function(query, page) {
        let p = Math.max(1, page || 1);
        let url = this.apiUrl + "/search?limit=48&page=" + p + "&q=" + encodeURIComponent(query || "");
        let res = this.requestUrl(url);
        if (!res || res.status !== 200 || !res.body) return { items: [], totalPages: 1 };
        let json = this.safeParseJson(res.body);
        if (!json) return { items: [], totalPages: 1 };
        let list = json.data || [];
        let items = list.map(item => ({
            title: item.title,
            url: "/series/" + item.slug,
            thumbnailUrl: item.cover,
            status: 0
        }));
        let hasNext = Boolean(json.cursor && json.cursor.hasNext);
        return { items: items, totalPages: hasNext ? p + 1 : p };
    },

    getMangaList: function(page, status, genre, type, sort) {
        let p = Math.max(1, page || 1);
        let order = (sort === "latest") ? "update" : "popular";
        let url = this.apiUrl + "/filter?limit=24&order_by=" + order + "&page=" + p;
        if (status === 1) url += "&status=ongoing";
        else if (status === 2) url += "&status=completed";
        if (genre) {
            let genresArr = Array.isArray(genre) ? genre : [String(genre)];
            let cleanGenres = genresArr.map(g => String(g || "").trim().toLowerCase().replace(/\s+/g, "-")).filter(Boolean);
            if (cleanGenres.length > 0) {
                url += "&genre=" + encodeURIComponent(cleanGenres.join(","));
            }
        }
        if (type) {
            let tStr = Array.isArray(type) ? type[0] : String(type);
            url += "&type=" + encodeURIComponent(tStr.toLowerCase());
        }
        let res = this.requestUrl(url);
        if (!res || res.status !== 200 || !res.body) return { items: [], totalPages: p };
        let json = this.safeParseJson(res.body);
        if (!json) return { items: [], totalPages: p };
        let list = json.data || [];
        let items = list.map(item => ({
            title: item.title,
            url: "/series/" + item.slug,
            thumbnailUrl: item.cover,
            status: 0
        }));
        let hasNext = Boolean(json.cursor && json.cursor.hasNext) || list.length >= 24;
        return { items: items, totalPages: hasNext ? p + 1 : p };
    },

    getMangaDetails: function(url) {
        let slug = this.extractSlug(url);
        if (!slug) return { title: "", url: url, thumbnailUrl: "", author: "", status: 0, description: "", genre: [] };
        let durl = this.apiUrl + "/mangaDetail/" + encodeURIComponent(slug);
        let res = this.requestUrl(durl);
        if (!res || res.status !== 200 || !res.body) {
            return { title: "", url: url, thumbnailUrl: "", author: "", status: 0, description: "", genre: [] };
        }
        let json = this.safeParseJson(res.body);
        let d = json && json.data;
        if (!d) return { title: "", url: url, thumbnailUrl: "", author: "", status: 0, description: "", genre: [] };

        let status = 0;
        let st = (d.status || "").toLowerCase();
        if (st === "ongoing") status = 1;
        else if (st === "completed" || st === "complete") status = 2;
        else if (st.includes("hiatus") || st.includes("hold")) status = 6;

        return {
            title: d.title || "",
            url: url,
            thumbnailUrl: d.cover || "",
            author: d.author || "",
            status: status,
            description: d.sinopsis || (d.type ? "Type: " + d.type : ""),
            genre: d.genre || []
        };
    },

    getChapterList: function(url) {
        let slug = this.extractSlug(url);
        if (!slug) return [];
        let durl = this.apiUrl + "/mangaDetail/" + encodeURIComponent(slug);
        let res = this.requestUrl(durl);
        if (!res || res.status !== 200 || !res.body) return [];
        let json = this.safeParseJson(res.body);
        let d = json && json.data;
        if (!d || !d.chapters) return [];

        return d.chapters.filter(c => !c.redirect_link).map(ch => ({
            name: "Chapter " + ch.chapterNum,
            url: "/chapter/" + ch.slug,
            uploadDate: ch.time || ""
        }));
    },

    getPageList: function(url) {
        let slug = this.extractSlug(url);
        if (!slug) return [];
        let pages = [];

        // 1. Try API readingPage payload if it exposes real image URLs
        let rurl = this.apiUrl + "/readingPage/" + encodeURIComponent(slug);
        let res = this.requestUrl(rurl);
        if (res && res.status === 200 && res.body) {
            let json = this.safeParseJson(res.body);
            let data = json && json.data ? json.data : null;
            let rawList = (data && (data.pages || data.images || data.chapterImages)) || [];
            if (Array.isArray(rawList)) {
                for (let entry of rawList) {
                    let imgUrl = typeof entry === "string" ? entry : (entry && (entry.url || entry.image || entry.src) || "");
                    if (imgUrl && imgUrl.startsWith("http") && !pages.includes(imgUrl)) {
                        pages.push(imgUrl);
                    }
                }
            }
        }

        // 2. Fallback to fetching chapter web page HTML and extracting real reader images
        if (pages.length === 0) {
            let webUrl = url.startsWith("http") ? url : (this.baseUrl + (url.startsWith("/") ? url : ("/chapter/" + slug)));
            let webRes = this.requestUrl(webUrl);
            if (webRes && webRes.status === 200 && webRes.body) {
                let doc = Html.parse(webRes.body, webUrl);
                let imgs = doc.querySelectorAll("#readerarea img, .rdminimal img, .reading-content img, .chapter-content img");
                for (let img of imgs) {
                    let src = img.absUrl("data-src") || img.absUrl("data-lazy-src") || img.absUrl("src") || "";
                    if (src && src.startsWith("http") && !src.includes("logo") && !pages.includes(src)) {
                        pages.push(src);
                    }
                }
            }
        }

        return pages;
    }
};