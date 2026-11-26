/**
 * Yomic Extension: KomikRealm (komikrealm)
 * Ported from Keiyoushi id extensions-source (src/id/komikrealm)
 * Pure JavaScript compatible with Jint 4.5.0 + AngleSharp 1.10.1
 */

var source = {
    name: "KomikRealm",
    baseUrl: "https://komikrealm.org",
    apiUrl: "https://komikrealm.org",
    language: "id",
    version: "1.1.0",
    description: "Baca Komik Manhwa, Manhua, Manga di KomikRealm",
    author: "DesktopKomik",
    iconUrl: "https://raw.githubusercontent.com/keiyoushi/extensions-source/main/src/id/komikrealm/res/mipmap-xxhdpi/ic_launcher.png",
    iconBackground: "#0f172a",
    iconForeground: "#38bdf8",
    isNsfw: false,
    isHasMorePages: false,
    pageSize: 24,
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

    decodeQwik: function(qwik) {
        if (!qwik || !Array.isArray(qwik._objs)) return {};
        let objs = qwik._objs;
        let activePath = new Set();
        function resolve(idx, depth) {
            if (depth > 64) return null;
            let numIdx = parseInt(String(idx), 36);
            if (isNaN(numIdx) || numIdx < 0 || numIdx >= objs.length) return null;
            if (activePath.has(numIdx)) return null;
            let val = objs[numIdx];
            if (Array.isArray(val)) {
                activePath.add(numIdx);
                let arr = val.map((item) => resolve(item, depth + 1));
                activePath.delete(numIdx);
                return arr;
            } else if (val !== null && typeof val === "object") {
                activePath.add(numIdx);
                let res = {};
                for (let k in val) res[k] = resolve(val[k], depth + 1);
                activePath.delete(numIdx);
                return res;
            } else if (typeof val === "string") {
                if (!val || val.charCodeAt(0) >= 32) return val;
                if (val.charCodeAt(0) === 1) return null;
                return val.substring(1);
            }
            return val;
        }
        return resolve(qwik._entry, 0) || {};
    },

    normalizeCover: function(url) {
        if (!url) return "";
        let secure = url.replace("http://", "https://");
        if (secure.includes("imgkomik.xyz") || secure.includes("manhwature.com")) return secure;
        return secure.replace("https://", "https://cdn.imgkomik.xyz/");
    },

    extractSeriesList: function(decoded) {
        let loaders = decoded.loaders || {};
        for (let k in loaders) {
            if (Array.isArray(loaders[k]) && loaders[k].length > 0 && loaders[k][0].slug) {
                return loaders[k];
            }
        }
        return [];
    },

    getPopularManga: function(page) {
        if ((page || 1) > 1) return { items: [], totalPages: 1 };
        let url = this.baseUrl + "/popular/q-data.json";
        let res = this.requestUrl(url);
        if (!res || res.status !== 200 || !res.body) return { items: [], totalPages: 1 };
        let raw = (() => { try { return JSON.parse(res.body) || {}; } catch (e) { return {}; } })();
        let decoded = this.decodeQwik(raw);
        let list = this.extractSeriesList(decoded);
        let items = list.map(item => {
            let t = (item.type || "").toLowerCase();
            let typePath = (t === "manhwa" || t === "manhua" || t === "doujinshi") ? t : "manga";
            return {
                title: item.title,
                url: "/" + typePath + "/" + item.slug + "/",
                thumbnailUrl: this.normalizeCover(item.poster),
                status: 0
            };
        });
        return { items: items, totalPages: items.length >= 24 ? 2 : 1 };
    },

    getLatestUpdates: function(page) {
        if ((page || 1) > 1) return { items: [], totalPages: 1 };
        let url = this.baseUrl + "/q-data.json";
        let res = this.requestUrl(url);
        if (!res || res.status !== 200 || !res.body) return { items: [], totalPages: 1 };
        let raw = (() => { try { return JSON.parse(res.body) || {}; } catch (e) { return {}; } })();
        let decoded = this.decodeQwik(raw);
        let list = this.extractSeriesList(decoded);
        let items = list.map(item => {
            let t = (item.type || "").toLowerCase();
            let typePath = (t === "manhwa" || t === "manhua" || t === "doujinshi") ? t : "manga";
            return {
                title: item.title,
                url: "/" + typePath + "/" + item.slug + "/",
                thumbnailUrl: this.normalizeCover(item.poster),
                status: 0
            };
        });
        return { items: items, totalPages: items.length >= 24 ? 2 : 1 };
    },

    getSearchManga: function(query, page) {
        if ((page || 1) > 1) return { items: [], totalPages: 1 };
        let cleanQ = (query || "").trim();
        if (!cleanQ) return this.getPopularManga(page);
        let url = this.baseUrl + "/search/" + encodeURIComponent(cleanQ) + "/q-data.json";
        let res = this.requestUrl(url);
        if (!res || res.status !== 200 || !res.body) return { items: [], totalPages: 1 };
        let raw = (() => { try { return JSON.parse(res.body) || {}; } catch (e) { return {}; } })();
        let decoded = this.decodeQwik(raw);
        let list = this.extractSeriesList(decoded);
        let items = list.map(item => {
            let t = (item.type || "").toLowerCase();
            let typePath = (t === "manhwa" || t === "manhua" || t === "doujinshi") ? t : "manga";
            return {
                title: item.title,
                url: "/" + typePath + "/" + item.slug + "/",
                thumbnailUrl: this.normalizeCover(item.poster),
                status: 0
            };
        });
        return { items: items, totalPages: 1 };
    },

        getMangaList: function(page, status, genre, type, sort) {
        if (sort === "latest") {
            return this.getLatestUpdates(page);
        }
        return this.getPopularManga(page);
    },

    getMangaDetails: function(url) {
        let cleanUrl = url.endsWith("/") ? url : url + "/";
        let dataUrl = this.baseUrl + cleanUrl + "q-data.json";
        let res = this.requestUrl(dataUrl);
        if (!res || res.status !== 200 || !res.body) {
            return { title: "", url: url, thumbnailUrl: "", author: "", status: 0, description: "", genre: [] };
        }
        let raw = (() => { try { return JSON.parse(res.body) || {}; } catch (e) { return {}; } })();
        let decoded = this.decodeQwik(raw);
        let loaders = decoded.loaders || {};
        let series = null;
        for (let k in loaders) {
            if (loaders[k] && typeof loaders[k] === "object" && loaders[k].chapters) {
                series = loaders[k];
                break;
            }
        }
        if (!series) return { title: "", url: url, thumbnailUrl: "", author: "", status: 0, description: "", genre: [] };

        let status = 0;
        let st = (series.status || "").toLowerCase();
        if (st === "on-going") status = 1;
        else if (st === "end") status = 2;

        let genres = [series.type].filter(Boolean);
        let author = "";
        if (Array.isArray(series.taxonomy_relation)) {
            for (let rel of series.taxonomy_relation) {
                if (!rel || !rel.taxonomy) continue;
                let t = rel.taxonomy;
                if (t.type === "genre" && t.name) genres.push(t.name);
                else if (t.type === "author" && t.name && t.name !== "unknown") author = t.name;
            }
        }

        let desc = series.synopsis || "";
        if (series.alter) desc += "\n\nAlternative titles: " + series.alter;

        return {
            title: series.title || "",
            url: url,
            thumbnailUrl: this.normalizeCover(series.poster),
            author: author,
            status: status,
            description: desc.trim(),
            genre: genres
        };
    },

    getChapterList: function(url) {
        let cleanUrl = url.endsWith("/") ? url : url + "/";
        let dataUrl = this.baseUrl + cleanUrl + "q-data.json";
        let res = this.requestUrl(dataUrl);
        if (!res || res.status !== 200 || !res.body) return [];
        let raw = (() => { try { return JSON.parse(res.body) || {}; } catch (e) { return {}; } })();
        let decoded = this.decodeQwik(raw);
        let loaders = decoded.loaders || {};
        let series = null;
        for (let k in loaders) {
            if (loaders[k] && typeof loaders[k] === "object" && loaders[k].chapters) {
                series = loaders[k];
                break;
            }
        }
        if (!series || !series.chapters) return [];

        return series.chapters.map(ch => {
            let numStr = String(ch.chapter).replace(/\.0$/, "");
            return {
                name: "Chapter " + numStr,
                url: cleanUrl + "chapter-" + numStr + "/",
                uploadDate: ch.created_at || ""
            };
        });
    },

    getPageList: function(url) {
        let cleanUrl = url.endsWith("/") ? url : url + "/";
        let dataUrl = this.baseUrl + cleanUrl + "q-data.json";
        let res = this.requestUrl(dataUrl);
        if (!res || res.status !== 200 || !res.body) return [];
        let raw = (() => { try { return JSON.parse(res.body) || {}; } catch (e) { return {}; } })();
        let decoded = this.decodeQwik(raw);
        let loaders = decoded.loaders || {};
        let chapterData = null;
        for (let k in loaders) {
            if (loaders[k] && loaders[k].data && loaders[k].data.chapters) {
                chapterData = loaders[k].data;
                break;
            }
        }
        if (!chapterData || !chapterData.chapters || chapterData.chapters.length === 0) return [];
        let imagesObj = chapterData.chapters[0].images || {};
        let sortedKeys = Object.keys(imagesObj).sort((a, b) => parseInt(a) - parseInt(b));
        return sortedKeys.map(k => this.normalizeCover(imagesObj[k].src));
    }
};