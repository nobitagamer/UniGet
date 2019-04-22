using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommandLine;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json;

namespace UniGet
{
    internal static class RestoreTool
    {
        internal class Options
        {
            [Value(0, Default = "UnityPackages.json", HelpText = "Project File")]
            public string ProjectFile { get; set; }

            [Option('o', "output", HelpText = "Specifies the directory for the created unity package file. If not specified, uses the current directory.")]
            public string OutputDirectory { get; set; }

            [Option('l', "local", HelpText = "Specifies the directory for the local repository.")]
            public string LocalRepositoryDirectory { get; set; }

            [Option('r', "remove", HelpText = "Remove all installed packages before restoring.")]
            public bool Remove { get; set; }

            [Option('t', "token", HelpText = "Github Token for dealing with rate limit exceed problem.")]
            public string Token { get; set; }
        }

        public static int Run(params string[] args)
        {
            var parser = new Parser(config => config.HelpWriter = Console.Out);

            Options options = null;
            var result = parser.ParseArguments<Options>(args)
                               .WithParsed(r => { options = r; });

            // Run process !

            if (options == null)
                return 1;

            var packageMap = Process(options).Result;
            foreach (var p in packageMap)
            {
                Console.WriteLine($"Restored: {p.Key}: {p.Value}");
            }
            return 0;
        }

        internal static async Task<Dictionary<string, SemVer.Version>> Process(Options options)
        {
            if (options.Remove)
            {
                var ret = RemoveTool.Process(new RemoveTool.Options
                {
                    ProjectDir = Path.GetDirectoryName(options.ProjectFile)
                });
                if (ret != 0)
                    throw new InvalidOperationException($"Cannot remove package: result code={ret}");
            }

            var p = Project.Load(options.ProjectFile);
            var projectDir = Path.GetDirectoryName(options.ProjectFile);
            var outputDir = options.OutputDirectory ?? projectDir;
            var packageMap = new Dictionary<string, SemVer.Version>();

            if (p.Dependencies == null || p.Dependencies.Any() == false)
            {
                Console.WriteLine("No dependencies.");
                return packageMap;
            }

            var context = new ProcessContext
            {
                Options = options,
                OutputDir = outputDir,
                PackageMap = packageMap,
                DepQueue = new Queue<KeyValuePair<string, Project.Dependency>>(p.Dependencies)
            };

            while (context.DepQueue.Any())
            {
                var item = context.DepQueue.Dequeue();
                await ProcessStep(item.Key, item.Value, context);
            }

            return packageMap;
        }

        private class ProcessContext
        {
            public Options Options;
            public string OutputDir;
            public Dictionary<string, SemVer.Version> PackageMap;
            public Queue<KeyValuePair<string, Project.Dependency>> DepQueue;
        }

        private static async Task ProcessStep(string projectId, Project.Dependency projectDependency, ProcessContext context)
        {
            var versionRange = new SemVer.Range(projectDependency.Version, true);

            // if already resolved dependency, skip it

            if (context.PackageMap.ContainsKey(projectId))
            {
                if (versionRange.IsSatisfied(context.PackageMap[projectId]) == false)
                    throw new InvalidDataException($"Cannot meet version requirement: {projectId} {projectDependency.Version} (but {context.PackageMap[projectId]})");
                return;
            }

            // download package

            Console.WriteLine("Restore: " + projectId);

            var packageFile = "";
            var packageVersion = new SemVer.Version(0, 0, 0);
            var nugetTargetFrameworkMoniker = string.Empty;

            if (projectDependency.Source != "local" && string.IsNullOrEmpty(context.Options.LocalRepositoryDirectory) == false)
            {
                var packages = LocalPackage.GetPackages(context.Options.LocalRepositoryDirectory, projectId);
                var versionIndex = versionRange.GetSatisfiedVersionIndex(packages.Select(x => x.Item2).ToList());
                if (versionIndex != -1)
                {
                    packageFile = packages[versionIndex].Item1;
                    packageVersion = packages[versionIndex].Item2;
                }
            }

            if (string.IsNullOrEmpty(packageFile) == false)
            {
            }
            else if (projectDependency.Source == "local")
            {
                var packages = LocalPackage.GetPackages(context.Options.LocalRepositoryDirectory ?? "", projectId);
                var versionIndex = versionRange.GetSatisfiedVersionIndex(packages.Select(x => x.Item2).ToList());
                if (versionIndex == -1)
                    throw new InvalidOperationException("Cannot find package from local repository: " + projectId);

                packageFile = packages[versionIndex].Item1;
                packageVersion = packages[versionIndex].Item2;
            }
            else if (projectDependency.Source.StartsWith("github:"))
            {
                var parts = projectDependency.Source.Substring(7).Split('/');
                if (parts.Length != 2)
                    throw new InvalidDataException("Cannot determine github repo information from url: " + projectDependency.Source);

                var r = await GithubPackage.DownloadPackageAsync(parts[0], parts[1], projectId, versionRange, context.Options.Token);
                packageFile = r.Item1;
                packageVersion = r.Item2;
            }
            else if (projectDependency.Source.StartsWith("nuget:"))
            {
                nugetTargetFrameworkMoniker = projectDependency.Source.Substring(6);

                var r = NugetPackage.DownloadPackage(projectId, projectDependency.Version);
                packageFile = r.Item1;
                packageVersion = r.Item2;
            }
            else
            {
                throw new InvalidOperationException("Cannot recognize source: " + projectDependency.Source);
            }

            context.PackageMap.Add(projectId, packageVersion);

            if (string.IsNullOrEmpty(nugetTargetFrameworkMoniker))
            {
                // apply package

                Extracter.ExtractUnityPackage(packageFile, context.OutputDir,
                                              projectId, projectDependency.IncludeExtra, projectDependency.IncludeMerged);

                // deep into dependencies

                var projectFile = Path.Combine(context.OutputDir, $"Assets/UnityPackages/{projectId}.unitypackage.json");
                if (File.Exists(projectFile))
                {
                    var project = Project.Load(projectFile);
                    if (project.MergedDependencies != null && projectDependency.IncludeMerged)
                    {
                        foreach (var d in project.MergedDependencies)
                        {
                            if (context.PackageMap.ContainsKey(d.Key) == false)
                                context.PackageMap[d.Key] = new SemVer.Version(d.Value.Version, true);
                        }
                    }
                    if (project.Dependencies != null)
                    {
                        foreach (var d in project.Dependencies)
                            context.DepQueue.Enqueue(d);
                    }
                }
            }
            else
            {
                // apply package
                var outputDir = NugetPackage.ExtractPackage(projectId, packageVersion.ToString(),
                                            nugetTargetFrameworkMoniker, context.OutputDir);

                // create proxy project file

                // var outputDir = Path.Combine(context.OutputDir, $"Assets/UnityPackages/{projectId}");
                var projectAssetPath = $"Assets/UnityPackages/{projectId}.unitypackage.json";
                var projectFile = Path.Combine(context.OutputDir, projectAssetPath);
                var p = new Project { Id = projectId, Version = packageVersion.ToString() };
                p.Description = $"Nuget package (TFM:{nugetTargetFrameworkMoniker})";
                if (Directory.Exists(outputDir))
                {
                    p.Files = Directory.GetFiles(outputDir, "*")
                        .Where(f => Path.GetExtension(f).ToLower() != ".meta")
                        .Select(f => JToken.FromObject(f.Substring(context.OutputDir.Length + 1).Replace("\\", "/"))).ToList();
                }

                var jsonSettings = new JsonSerializerSettings
                {
                    DefaultValueHandling = DefaultValueHandling.Ignore,
                };
                File.WriteAllText(projectFile, JsonConvert.SerializeObject(p, Formatting.Indented, jsonSettings));

                File.WriteAllBytes(projectFile + ".meta",
                                   Packer.GenerateMeta(projectFile, projectAssetPath).Item2);
            }
        }
    }
}
