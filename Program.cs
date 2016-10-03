using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;
using HtmlAgilityPack;
using Nito.AsyncEx;
using Octokit;
using static System.Net.Http.HttpCompletionOption;

namespace DotnetCoreDocsWalker
{
    static class Program
    {
        static void Main()
        {
            WalkSite("https://msdn.microsoft.com/en-us/visualfsharpdocs/conceptual/", initialUrl: "visual-fsharp");

            Console.ReadLine();
        }

        static void WalkRepo(string org, string repo, string branch)
        {
            WalkSite($"https://github.com/{org}/{repo}/tree/{branch}/", $"https://github.com/{org}/{repo}/tree/");
        }

        static void WalkSite(string baseUrl, string ignoredUrl = null, string initialUrl = null)
        {
            Debug.Listeners.Add(new ConsoleTraceListener());

            bool lastNull = false;
            var processedUrls = new MultiValueDictionary<Uri, Uri>();
            var l = new AsyncLock();

            TransformManyBlock<Uri, Uri> downloadBlock = null;
            downloadBlock = new TransformManyBlock<Uri, Uri>(async url =>
            {
                using (await l.LockAsync())
                {
                    if (url == null)
                    {
                        Console.WriteLine($"Token, queue length {downloadBlock.InputCount} ({baseUrl}).");

                        if (lastNull)
                        {
                            downloadBlock.Complete();
                            return new Uri[0];
                        }
                        lastNull = true;
                        return new Uri[] { null };
                    }

                    lastNull = false;
                }

                if (!url.AbsoluteUri.StartsWith(baseUrl))
                {
                    return new Uri[0];
                }

                Debug.WriteLine($"Starting {url}.");

                string pageSource;
                using (var httpClient = new HttpClient())
                {
                    try
                    {
                        using (var response = await httpClient.GetAsync(url, ResponseHeadersRead))
                        {
                            if (!response.IsSuccessStatusCode)
                            {
                                return new Uri[0];
                            }

                            url = response.RequestMessage.RequestUri;

                            if (!url.AbsoluteUri.StartsWith(baseUrl))
                            {
                                return new Uri[0];
                            }

                            pageSource = await response.Content.ReadAsStringAsync();
                        }
                    }
                    catch (Exception)
                    {
                        return new Uri[0];
                    }
                }

                Debug.WriteLine($"Parsing {url}.");

                var document = new HtmlDocument();
                document.LoadHtml(pageSource);

                var linkInCode = document.DocumentNode.Descendants("code")
                    .FirstOrDefault(e => e.InnerHtml.Contains("http://") || e.InnerHtml.Contains("https://"));

                if (linkInCode != null)
                    Console.WriteLine($"{url}: {linkInCode.OuterHtml}");

                using (await l.LockAsync())
                {
                    var links = document.DocumentNode.Descendants("a")
                        .Select(a => HtmlEntity.DeEntitize(a.Attributes["href"]?.Value))
                        .Where(href => href != null)
                        .Select(href =>
                        {
                            if (href.StartsWith("mailto:"))
                            {
                                return null;
                            }

                            try
                            {
                                return new Uri(new Uri(url, href).GetLeftPart(UriPartial.Query));
                            }
                            catch (Exception)
                            {
                                return null;
                            }
                        })
                        .Where(linkUrl =>
                        {
                            if (linkUrl == null)
                                return false;

                            if (new[] { "javascript" }.Contains(linkUrl.Scheme))
                                return false;

                            //if (linkUrl.AbsoluteUri.StartsWith(
                            //    "https://github.com/dotnet/core-docs/new/master/apispec/new"))
                            //    return false;

                            if (ignoredUrl != null && linkUrl.AbsoluteUri.StartsWith(ignoredUrl) &&
                                !linkUrl.AbsoluteUri.StartsWith(baseUrl))
                                return false;

                            bool first = !processedUrls.ContainsKey(linkUrl);
                            processedUrls.Add(linkUrl, url);
                            return first;
                        }).ToList();

                    Debug.WriteLine($"Finished {url}, found {links.Count} new links.");

                    return links;
                }
            }, new ExecutionDataflowBlockOptions { MaxDegreeOfParallelism = 10 });

            downloadBlock.LinkTo(downloadBlock);

            var initial = new Uri(baseUrl);
            if (initialUrl != null)
                initial = new Uri(initial, initialUrl);

            downloadBlock.Post(initial);
            downloadBlock.Post(null);
            downloadBlock.Completion.Wait();

            Debug.WriteLine($"Done walking {baseUrl}.");
        }
    }
}
