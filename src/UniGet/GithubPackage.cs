﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using CommandLine;
using Octokit;

namespace UniGet
{
    internal static class GithubPackage
    {
        public static async Task<Tuple<string, SemVer.Version>> DownloadPackageAsync(
            string owner, string repoName, string projectId, SemVer.Range versionRange, bool force = false)
        {
            var packages = await FetchPackagesAsync(owner, repoName, projectId);
            var package = packages.Where(p => versionRange.IsSatisfied(p.Item2))
                                  .OrderBy(r => r.Item2).LastOrDefault();
            if (package == null)
                throw new ArgumentException("Cannot find a package matched version range.");

            var cacheRootPath = GetPackageCacheRoot();
            if (Directory.Exists(cacheRootPath) == false)
                Directory.CreateDirectory(cacheRootPath);

            var saveFileName = Path.Combine(
                cacheRootPath,
                string.Format("github~{0}~{1}~{2}~{3}.unitypackage", owner, repoName, projectId, package.Item2));

            if (File.Exists(saveFileName) == false || force)
            {
                using (var httpClient = new HttpClient())
                using (var request = new HttpRequestMessage(HttpMethod.Get, package.Item1))
                {
                    var response = await httpClient.SendAsync(request);

                    using (var contentStream = await response.Content.ReadAsStreamAsync())
                    using (var fileStream = new FileStream(saveFileName, System.IO.FileMode.Create, FileAccess.Write))
                    {
                        await contentStream.CopyToAsync(fileStream);
                    }
                }
            }

            return Tuple.Create(saveFileName, package.Item2);
        }

        public static string GetPackageCacheRoot()
        {
            return Path.Combine(Environment.GetEnvironmentVariable("APPDATA"), "uniget");
        }

        public static async Task<List<Tuple<string, SemVer.Version>>> FetchPackagesAsync(string owner, string repoName, string projectId)
        {
            var packages = new List<Tuple<string, SemVer.Version>>();

            var client = new GitHubClient(new ProductHeaderValue("uniget"));
            var githubReleases = await client.Repository.Release.GetAll(owner, repoName);
            foreach (var release in githubReleases)
            {
                foreach (var a in release.Assets)
                {
                    if (Path.GetExtension(a.Name).ToLower() == ".unitypackage")
                    {
                        if (a.Name.StartsWith(projectId + "."))
                        {
                            try
                            {
                                var verStr = Path.GetFileNameWithoutExtension(a.Name).Substring(projectId.Length + 1);
                                var ver = new SemVer.Version(verStr);
                                packages.Add(Tuple.Create(a.BrowserDownloadUrl, ver));
                            }
                            catch (Exception)
                            {
                            }
                        }
                    }
                }
            }

            return packages;
        }
    }
}