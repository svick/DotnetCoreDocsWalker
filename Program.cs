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
            WalkSite("https://www.microsoft.com/net");
            WalkSite("https://docs.microsoft.com/en-us/dotnet/");
            //Console.ReadLine();
            //return;

            WalkSite("Microsoft", "dotnet", "master");

            var orgs = new[] { "dotnet", "aspnet" };

            var client = new GitHubClient(new ProductHeaderValue("Svick.DotnetCoreDocsWalker"));

            var repos = from org in orgs
                        from repo in client.Repository.GetAllForOrg(org).Result
                        select new { org, repo = repo.Name, branch = repo.DefaultBranch };

            //repos = repos.SkipWhile(x => x.repo != "core-docs");

            foreach (var repo in repos)
            {
                Console.WriteLine(repo);
                WalkSite(repo.org, repo.repo, repo.branch);
            }

            Console.ReadLine();
        }

        static void WalkSite(string org, string repo, string branch)
        {
            WalkSite($"https://github.com/{org}/{repo}/tree/{branch}/", $"https://github.com/{org}/{repo}/tree/");
        }

        static void WalkSite(string baseUrl, string ignoredUrl = null)
        {
            Debug.Listeners.Add(new ConsoleTraceListener());

            bool lastNull = false;
            //bool firstNull = true;
            var processedUrls = new MultiValueDictionary<Uri, Uri>();
            var l = new AsyncLock();

            //AsyncAutoResetEvent doneEvent = new AsyncAutoResetEvent();

            TransformManyBlock<Uri, Uri> downloadBlock = null;
            downloadBlock = new TransformManyBlock<Uri, Uri>(async url =>
            {
                using (await l.LockAsync())
                {
                    if (url == null)
                    {
                        Console.WriteLine($"Token, queue length {downloadBlock.InputCount} ({baseUrl}).");

                        //if (!firstNull)
                        //    doneEvent.Set();

                        if (lastNull)
                        {
                            downloadBlock.Complete();
                            return new Uri[0];
                        }
                        lastNull = true;
                        //firstNull = false;
                        return new Uri[] { null };
                    }

                    lastNull = false;
                }

                Debug.WriteLine($"Starting {url}.");

                string pageSource;
                using (var httpClient = new HttpClient())
                {
                    Func<string, Task> writeError = async cause =>
                    {
                        using (await l.LockAsync())
                        {
                            Console.ForegroundColor = ConsoleColor.Red;

                            Console.WriteLine($"{url}: {cause}");

                            var sourceUrls = processedUrls[url];
                            var count = 5;
                            foreach (var sourceUrl in sourceUrls.Take(count))
                            {
                                Console.WriteLine($"  {sourceUrl}");
                            }

                            if (sourceUrls.Count > count)
                                Console.WriteLine("...");

                            Console.ResetColor();
                        }
                    };

                    try
                    {
                        using (var response = await httpClient.GetAsync(url, ResponseHeadersRead))
                        {
                            if (!response.IsSuccessStatusCode)
                            {
                                await writeError(response.StatusCode.ToString());
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
                    catch (Exception e)
                    {
                        await writeError(e.Message);
                        return new Uri[0];
                    }
                }

                Debug.WriteLine($"Parsing {url}.");

                var document = new HtmlDocument();
                document.LoadHtml(pageSource);

                using (await l.LockAsync())
                {
                    var links = document.DocumentNode.Descendants("a")
                        .Select(a => HtmlEntity.DeEntitize(a.Attributes["href"]?.Value))
                        .Where(href => href != null)
                        //.Select(Uri.UnescapeDataString)
                        .Select(href =>
                        {
                            Func<ConsoleColor, Action<string>> writeColor = color => cause =>
                            {
                                Console.ForegroundColor = color;

                                Console.WriteLine($"{href}: {cause}");
                                Console.WriteLine($"    {url}");

                                Console.ResetColor();
                            };

                            Action<string> writeError = writeColor(ConsoleColor.Red);
                            Action<string> writeWarning = writeColor(ConsoleColor.Yellow);

                            if (href.StartsWith("mailto:"))
                            {
                                writeWarning("Email address");

                                return null;
                            }

                            try
                            {
                                return new Uri(new Uri(url, href).GetLeftPart(UriPartial.Query));
                            }
                            catch (Exception e)
                            {
                                writeError($"{e.GetType().Name}: {e.Message}");

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

            downloadBlock.Post(new Uri(baseUrl));
            downloadBlock.Post(null);
            downloadBlock.Completion.Wait();
            //doneEvent.Wait();

            Debug.WriteLine($"Done walking {baseUrl}.");
        }
    }
}
