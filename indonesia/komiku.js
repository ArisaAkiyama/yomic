var source = {
    name: "Komiku",
    baseUrl: "https://komiku.org",
    apiUrl: "https://api.komiku.org",
    language: "id",
    version: "1.0.0",
    description: "Komiku extension implemented in JavaScript using Jint Engine",
    author: "DesktopKomik",
    iconBackground: "#2E7D32",
    iconForeground: "#FFFFFF",
    isNsfw: false,
    isHasMorePages: true,

    getPopularManga: function(page) {
        let url = `${this.apiUrl}/manga`;
        if (page > 1) {
            url += `/page/${page}`;
        }
        url += "?orderby=meta_value_num";
        
        let response = fetch(url);
        if (response.status !== 200) return [];
        
        let doc = Html.parse(response.body, url);
        return this.parseMangaList(doc);
    },

    getSearchManga: function(query, page) {
        let url = `${this.apiUrl}/manga`;
        if (page > 1) {
            url += `/page/${page}`;
        }
        if (query && query.trim() !== "") {
            url += `?s=${encodeURIComponent(query)}`;
        }
        
        let response = fetch(url);
        if (response.status !== 200) return [];
        
        let doc = Html.parse(response.body, url);
        return this.parseMangaList(doc);
    },

    getMangaDetails: function(url) {
        let fullUrl = this.baseUrl + url;
        let response = fetch(fullUrl);
        if (response.status !== 200) return {};

        let doc = Html.parse(response.body, fullUrl);
        
        // Description
        let description = "";
        let sinopsisEl = doc.querySelector("#Sinopsis > p");
        if (sinopsisEl) {
            description += sinopsisEl.text();
        }
        
        let rows = doc.querySelectorAll("table.inftable tr");
        let author = "";
        let status = 0; // Unknown=0, Ongoing=1, Completed=2
        let genres = [];
        let thumbnailEl = doc.querySelector("div.ims > img");
        let thumbnailUrl = thumbnailEl ? thumbnailEl.absUrl("src") : "";

        for (let row of rows) {
            let cells = row.querySelectorAll("td");
            if (cells.length >= 2) {
                let key = cells[0].text();
                let val = cells[1].text();
                if (key.includes("Judul Indonesia")) {
                    if (val.trim() !== "") {
                        description += "\n\nJudul Indonesia: " + val.trim();
                    }
                } else if (key.includes("Pengarang") || key.includes("Komikus")) {
                    author = val.trim();
                } else if (key.includes("Status")) {
                    let statusStr = val.trim().toLowerCase();
                    if (statusStr.includes("ongoing") || statusStr.includes("on going")) {
                        status = 1;
                    } else if (statusStr.includes("end") || statusStr.includes("completed")) {
                        status = 2;
                    }
                }
            }
        }

        // Genres
        let genreEls = doc.querySelectorAll("ul.genre li.genre a span");
        for (let el of genreEls) {
            genres.push(el.text());
        }

        let titleEl = doc.querySelector("h1");
        return {
            title: titleEl ? titleEl.text() : "",
            url: url,
            thumbnailUrl: thumbnailUrl,
            author: author,
            status: status,
            description: description,
            genre: genres
        };
    },

    getChapterList: function(mangaUrl) {
        let fullUrl = this.baseUrl + mangaUrl;
        let response = fetch(fullUrl);
        if (response.status !== 200) return [];

        let doc = Html.parse(response.body, fullUrl);
        let chapters = [];
        
        let rows = doc.querySelectorAll("#Daftar_Chapter tr");
        for (let row of rows) {
            if (row.querySelector("td.judulseries")) {
                let a = row.querySelector("a");
                if (a) {
                    let href = a.attr("href");
                    let relativeUrl = href;
                    if (href.startsWith(this.baseUrl)) {
                        relativeUrl = href.substring(this.baseUrl.length);
                    }
                    
                    let name = a.text();
                    let dateUpload = 0;
                    
                    let dateEl = row.querySelector("td.tanggalseries");
                    if (dateEl) {
                        dateUpload = this.parseDate(dateEl.text());
                    }
                    
                    chapters.push({
                        name: name,
                        url: relativeUrl,
                        dateUpload: dateUpload
                    });
                }
            }
        }
        return chapters;
    },

    getPageList: function(chapterUrl) {
        let fullUrl = this.baseUrl + chapterUrl;
        let response = fetch(fullUrl);
        if (response.status !== 200) return [];

        let doc = Html.parse(response.body, fullUrl);
        let pages = [];
        let imgEls = doc.querySelectorAll("#Baca_Komik img");
        for (let img of imgEls) {
            let src = img.absUrl("src");
            if (!src) src = img.attr("src");
            if (src) {
                pages.push(src);
            }
        }
        return pages;
    },

    parseMangaList: function(doc) {
        let mangas = [];
        let items = doc.querySelectorAll("div.bge");
        for (let item of items) {
            let titleEl = item.querySelector("h3");
            let aEl = item.querySelector("a");
            let imgEl = item.querySelector("img");
            
            if (titleEl && aEl) {
                let href = aEl.attr("href");
                let relativeUrl = href;
                if (href.startsWith(this.baseUrl)) {
                    relativeUrl = href.substring(this.baseUrl.length);
                }
                
                mangas.push({
                    title: titleEl.text(),
                    url: relativeUrl,
                    thumbnailUrl: imgEl ? imgEl.absUrl("src") : ""
                });
            }
        }
        return mangas;
    },

    parseDate: function(dateStr) {
        if (!dateStr) return 0;
        dateStr = dateStr.trim();
        
        if (dateStr.includes("lalu")) {
            let parts = dateStr.split(" ");
            let val = parseInt(parts[0]);
            let unit = parts[1];
            
            let date = new Date();
            if (unit === "jam") {
                date.setHours(date.getHours() - val);
            } else if (unit === "menit") {
                date.setMinutes(date.getMinutes() - val);
            } else if (unit === "detik") {
                // Keep current
            }
            return date.getTime();
        } else {
            let parts = dateStr.split("/");
            if (parts.length === 3) {
                let day = parseInt(parts[0]);
                let month = parseInt(parts[1]) - 1;
                let year = parseInt(parts[2]);
                let date = new Date(year, month, day);
                return date.getTime();
            }
        }
        return 0;
    }
};
