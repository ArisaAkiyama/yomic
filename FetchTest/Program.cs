using System;
using System.IO;
using HtmlAgilityPack;

class Program {
    static void Main() {
        string path = "komiktap_direct.html";
        if (!File.Exists(path)) {
            Console.WriteLine("File not found!");
            return;
        }

        var doc = new HtmlDocument();
        doc.LoadHtml(File.ReadAllText(path));

        var titleNode = doc.DocumentNode.SelectSingleNode("//title");
        Console.WriteLine($"Title of direct: '{titleNode?.InnerText}'");

        var chapterlist = doc.DocumentNode.SelectSingleNode("//div[@id='chapterlist']");
        Console.WriteLine($"chapterlist in direct: {chapterlist != null}");

        var divs = doc.DocumentNode.SelectNodes("//div[@id or @class]");
        if (divs != null) {
            Console.WriteLine("\n--- Divs of Interest in Direct ---");
            foreach (var div in divs.Take(20)) {
                string id = div.GetAttributeValue("id", "");
                string cl = div.GetAttributeValue("class", "");
                Console.WriteLine($"Div ID: '{id}', Class: '{cl}'");
            }
        }
    }
}
