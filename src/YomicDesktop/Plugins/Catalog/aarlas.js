var source = {
    name: "Aarlas",
    iconUrl: "https://raw.githubusercontent.com/keiyoushi/extensions-source/main/src/id/aarlas/res/mipmap-xxhdpi/ic_launcher.png",
    baseUrl: "https://www.arlas.online",
    apiUrl: "https://www.arlas.online",
    language: "id",
    version: "1.1.0",
    description: "Aarlas extension implemented in JavaScript using Jint Engine",
    author: "DesktopKomik",
    iconBackground: "#ff0080",
    iconForeground: "#FFFFFF",
    isNsfw: false,
    genres: [],
    formats: [],
    isHasMorePages: true,

    getPopularManga: function(page) {
        return this.getBloggerMangaPage(page, "Series", null, "published");
    },

    getLatestUpdates: function(page) {
        return this.getBloggerMangaPage(page, "Series", null, "updated");
    },

    getSearchManga: function(query, page) {
        return this.getBloggerMangaPage(page, "Series", query);
    },

    getBloggerMangaPage: function(page, label, query, orderby) {
        const appPageSize = 14;
        let startIndex = (Math.max(1, page) - 1) * appPageSize + 1;
        let url = `${this.apiUrl}/feeds/posts/default`;
        if (label) {
            url += `/-/${encodeURIComponent(label)}`;
        }
        url += `?alt=json&start-index=${startIndex}&max-results=${appPageSize}`;
        if (query) {
            url += `&q=${encodeURIComponent(query)}`;
        }
        if (orderby) {
            url += `&orderby=${encodeURIComponent(orderby)}`;
        }

        let response = fetch(url);
        if (!response || response.status !== 200) return { items: [], totalPages: page };

        let json = (() => { try { return JSON.parse(response.body) || {}; } catch (e) { return {}; } })();
        if (!json || !json.feed) return { items: [], totalPages: page };
        let entries = json.feed.entry || [];
        let totalResults = parseInt((json.feed.openSearch$totalResults && json.feed.openSearch$totalResults.$t) || "0") || 0;
        let totalPages = Math.ceil(totalResults / appPageSize);

        let items = [];
        for (let entry of entries) {
            let title = entry.title.$t;
            let href = "";
            let links = entry.link || [];
            for (let link of links) {
                if (link.rel === "alternate") {
                    href = link.href;
                    break;
                }
            }

            let relativeUrl = href;
            if (href.startsWith(this.baseUrl)) {
                relativeUrl = href.substring(this.baseUrl.length);
            }

            let thumbnailUrl = "";
            if (entry.media$thumbnail) {
                thumbnailUrl = entry.media$thumbnail.url;
                thumbnailUrl = thumbnailUrl.replace(/\/s[0-9]+(-c)?\//, "/w300/");
            }

            let status = 0;
            let categories = entry.category || [];
            for (let cat of categories) {
                let term = (cat.term || "").toLowerCase();
                if (term === "ongoing") {
                    status = 1;
                    break;
                } else if (term === "completed") {
                    status = 2;
                    break;
                }
            }

            items.push({
                title: title,
                url: relativeUrl,
                thumbnailUrl: thumbnailUrl,
                status: status
            });
        }

        return {
            items: items,
            totalPages: Math.max(page, totalPages)
        };
    },

    getMangaList: function(page, status, genre, type, sort) {
        let orderby = sort === "latest" ? "updated" : "published";
        if (status === 1) {
            return this.getBloggerMangaPage(page, "Ongoing", null, orderby);
        } else if (status === 2 || status === 4) {
            return this.getBloggerMangaPage(page, "Completed", null, orderby);
        }
        return sort === "latest" ? this.getLatestUpdates(page) : this.getPopularManga(page);
    },

    getMangaDetails: function(url) {
        let fullUrl = !url ? this.baseUrl : (url.startsWith("http") ? url : (url.startsWith("//") ? "https:" + url : (this.baseUrl + (url.startsWith("/") ? url : "/" + url))));
        let response = fetch(fullUrl);
        if (!response || response.status !== 200) return {};

        let doc = Html.parse(response.body, fullUrl);
        
        let titleEl = doc.querySelector("article.oh.a2 header h1");
        let title = titleEl ? titleEl.text().trim() : "";
        
        let thumbnailEl = doc.querySelector("div.grid div.a1 figure img");
        let thumbnailUrl = thumbnailEl ? thumbnailEl.absUrl("src") : "";

        let description = "";
        let synopsisEl = doc.querySelector("#synopsis");
        if (synopsisEl) {
            description += synopsisEl.text().trim();
        }

        let author = "";
        let status = 0; // Unknown=0, Ongoing=1, Completed=2
        
        let statusEl = doc.querySelector("aside.s1 div.y6x11p a[data]");
        if (statusEl) {
            let statusStr = statusEl.text().toLowerCase();
            if (statusStr.includes("ongoing") || statusStr.includes("on going")) {
                status = 1;
            } else if (statusStr.includes("completed") || statusStr.includes("end") || statusStr.includes("tamat")) {
                status = 2;
            }
        }

        let extraInfoRows = doc.querySelectorAll("#extra-info .y6x11p");
        for (let row of extraInfoRows) {
            let text = row.text();
            if (text.includes("Author")) {
                let valEl = row.querySelector("span.dt");
                if (valEl) {
                    author = valEl.text().trim();
                }
            }
        }

        let genres = [];
        let genreEls = doc.querySelectorAll("article.oh.a2 div.mt-15 a.label");
        for (let el of genreEls) {
            genres.push(el.text().trim());
        }

        return {
            title: title,
            url: url,
            thumbnailUrl: thumbnailUrl,
            author: author,
            status: status,
            description: description,
            genre: genres
        };
    },

    getChapterList: function(mangaUrl) {
        let fullUrl = !mangaUrl ? this.baseUrl : (mangaUrl.startsWith("http") ? mangaUrl : (mangaUrl.startsWith("//") ? "https:" + mangaUrl : (this.baseUrl + (mangaUrl.startsWith("/") ? mangaUrl : "/" + mangaUrl))));
        let response = fetch(fullUrl);
        if (!response || response.status !== 200) return [];

        let doc = Html.parse(response.body, fullUrl);
        let titleEl = doc.querySelector("article.oh.a2 header h1");
        if (!titleEl) return [];
        let title = titleEl.text().trim();

        let label = title;
        let match = response.body.match(/clwd\.run\(['"]([^'"]+)['"]\)/);
        if (match && match[1]) {
            label = match[1].trim();
        }

        let entries = [];
        let seenEntryKeys = {};
        const pageSize = 150;
        for (let startIndex = 1; startIndex <= 1500; startIndex += pageSize) {
            let feedUrl = `${this.apiUrl}/feeds/posts/default/-/${encodeURIComponent(label)}?alt=json&max-results=${pageSize}&start-index=${startIndex}`;
            let feedResponse = fetch(feedUrl);
            if (!feedResponse || feedResponse.status !== 200) break;
            let json = (() => { try { return JSON.parse(feedResponse.body) || {}; } catch (e) { return {}; } })();
            let batch = (json && json.feed && Array.isArray(json.feed.entry)) ? json.feed.entry : [];
            if (batch.length === 0) break;
            for (let e of batch) {
                let k = (e.id && e.id.$t) || (e.title && e.title.$t) || "";
                if (k && seenEntryKeys[k]) continue;
                if (k) seenEntryKeys[k] = true;
                entries.push(e);
            }
            if (batch.length < pageSize) break;
        }
        
        let chapters = [];
        for (let entry of entries) {
            let categories = entry.category || [];
            let isChapter = false;
            for (let cat of categories) {
                if (cat.term === "Chapter") {
                    isChapter = true;
                    break;
                }
            }
            if (!isChapter) continue;

            let titleSub = entry.title.$t;
            let href = "";
            let links = entry.link || [];
            for (let link of links) {
                if (link.rel === "alternate") {
                    href = link.href;
                    break;
                }
            }

            let relativeUrl = href;
            if (href.startsWith(this.baseUrl)) {
                relativeUrl = href.substring(this.baseUrl.length);
            }

            let dateUpload = 0;
            if (entry.published && entry.published.$t) {
                dateUpload = ((ts => isNaN(ts) ? 0 : ts)(new Date(entry.published.$t).getTime()));
            }

            chapters.push({
                name: titleSub,
                url: relativeUrl,
                dateUpload: dateUpload
            });
        }
        return chapters;
    },

    getPageList: function(chapterUrl) {
        let fullUrl = !chapterUrl ? this.baseUrl : (chapterUrl.startsWith("http") ? chapterUrl : (chapterUrl.startsWith("//") ? "https:" + chapterUrl : (this.baseUrl + (chapterUrl.startsWith("/") ? chapterUrl : "/" + chapterUrl))));
        let response = fetch(fullUrl);
        if (!response || response.status !== 200) return [];

        let doc = Html.parse(response.body, fullUrl);
        let pages = [];
        
        let imgEls = doc.querySelectorAll("div.check-box div.separator img");
        if (imgEls.length === 0) {
            imgEls = doc.querySelectorAll("div[data=imageProtection] div.separator img");
        }
        if (imgEls.length === 0) {
            imgEls = doc.querySelectorAll("#post-body div.separator img");
        }
        if (imgEls.length === 0) {
            imgEls = doc.querySelectorAll(".post-body div.separator img");
        }
        if (imgEls.length === 0) {
            imgEls = doc.querySelectorAll("div.separator img");
        }

        for (let img of imgEls) {
            let rawSrc = img.attr("src") || "";
            let lazySrc = img.attr("data-src") || img.attr("data-lazy-src") || img.attr("data-cfsrc") || "";
            let src = (lazySrc && (!rawSrc || rawSrc.startsWith("data:")))
                ? (img.absUrl("data-src") || img.absUrl("data-lazy-src") || lazySrc)
                : (img.absUrl("src") || rawSrc || lazySrc);
            if (src && !src.startsWith("data:image/svg")) {
                pages.push(src.trim());
            }
        }
        return pages;
    }
};
