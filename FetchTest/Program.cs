using System;
using HtmlAgilityPack;

class Program {
    static void Main() {
        var doc = new HtmlDocument();
        doc.Load("komik.html");
        var nodes = doc.DocumentNode.SelectNodes("//div[contains(@class,'tsinfo')]//div[contains(@class,'imptdt')] | //div[contains(@class,'infotable')]//tr | //div[contains(@class,'fmed')] | //th | //td | //div[contains(@class,'info-desc')]");
        if(nodes != null) {
            foreach(var n in nodes) {
                string text = n.InnerText.Trim().ToLower();
                if(text.Contains("status")) {
                    Console.WriteLine("FOUND STATUS NODE: " + text);
                    Console.WriteLine("HTML: " + n.OuterHtml);
                }
            }
        }
    }
}
