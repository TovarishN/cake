﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Linq;
using Cake.Core;
using Cake.Core.Configuration;
using Cake.Core.Diagnostics;
using Cake.Core.IO;
using Cake.Core.IO.NuGet;
using Cake.Core.Packaging;

#if NETCORE
using NuGet.Versioning;
#else
using NuGet;
#endif

using IFileSystem = Cake.Core.IO.IFileSystem;
using PackageReference = Cake.Core.Packaging.PackageReference;

namespace Cake.NuGet
{
    /// <summary>
    /// Installer for NuGet URI resources.
    /// </summary>
    public sealed class NuGetPackageInstaller : INuGetPackageInstaller
    {
        private readonly IFileSystem _fileSystem;
        private readonly ICakeEnvironment _environment;
        private readonly IProcessRunner _processRunner;
        private readonly INuGetToolResolver _toolResolver;
        private readonly INuGetContentResolver _contentResolver;
        private readonly ICakeLog _log;

        private readonly ICakeConfiguration _config;

        /// <summary>
        /// Initializes a new instance of the <see cref="NuGetPackageInstaller"/> class.
        /// </summary>
        /// <param name="fileSystem">The file system.</param>
        /// <param name="environment">The environment.</param>
        /// <param name="processRunner">The process runner.</param>
        /// <param name="toolResolver">The NuGet tool resolver.</param>
        /// <param name="contentResolver">The content resolver.</param>
        /// <param name="log">The log.</param>
        /// <param name="config">The configuration.</param>
        public NuGetPackageInstaller(
            IFileSystem fileSystem,
            ICakeEnvironment environment,
            IProcessRunner processRunner,
            INuGetToolResolver toolResolver,
            INuGetContentResolver contentResolver,
            ICakeLog log,
            ICakeConfiguration config)
        {
            _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
            _environment = environment ?? throw new ArgumentNullException(nameof(environment));
            _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
            _toolResolver = toolResolver ?? throw new ArgumentNullException(nameof(toolResolver));
            _contentResolver = contentResolver ?? throw new ArgumentNullException(nameof(contentResolver));
            _log = log ?? throw new ArgumentNullException(nameof(log));
            _config = config ?? throw new ArgumentNullException(nameof(config));
        }

        /// <summary>
        /// Determines whether this instance can install the specified resource.
        /// </summary>
        /// <param name="package">The package reference.</param>
        /// <param name="type">The package type.</param>
        /// <returns>
        ///   <c>true</c> if this installer can install the
        ///   specified resource; otherwise <c>false</c>.
        /// </returns>
        public bool CanInstall(PackageReference package, PackageType type)
        {
            if (package == null)
            {
                throw new ArgumentNullException(nameof(package));
            }
            return package.Scheme.Equals("nuget", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Installs the specified resource at the given location.
        /// </summary>
        /// <param name="package">The package reference.</param>
        /// <param name="type">The package type.</param>
        /// <param name="path">The location where to install the package.</param>
        /// <returns>The installed files.</returns>
        public IReadOnlyCollection<IFile> Install(PackageReference package, PackageType type, DirectoryPath path)
        {
            if (package == null)
            {
                throw new ArgumentNullException(nameof(package));
            }
            if (path == null)
            {
                throw new ArgumentNullException(nameof(path));
            }

            // Create the addin directory if it doesn't exist.
            path = GetPackagePath(path.MakeAbsolute(_environment), package);
            var root = _fileSystem.GetDirectory(path);
            var createdDirectory = false;
            if (!root.Exists)
            {
                _log.Debug("Creating package directory {0}...", path);
                root.Create();
                createdDirectory = true;
            }
            _log.Verbose("Package directory: {0}", path);
            // Package already exist?
            var packagePath = GetPackagePath(root, package.Package);
            _log.Verbose("Package path: {0}", packagePath);
            if (packagePath != null)
            {
                // Fetch available content from disc.
                var content = _contentResolver.GetFiles(packagePath, package, type);
                if (content.Any())
                {
                    _log.Debug("Package {0} has already been installed.", package.Package);
                    return content;
                }
            }

            // Install the package.
            if (!InstallPackage(package, path))
            {
                _log.Warning("An error occurred while installing package {0}.", package.Package);
                if (createdDirectory)
                {
                    _log.Debug("Deleting package directory {0}...", path);
                    root.Delete(true);
                    return Array.Empty<IFile>();
                }
            }

            var directories = root.GetDirectories("*", SearchScope.Current).ToArray();
            foreach (var directory in directories)
            {
                _log.Verbose(directory.Path.FullPath);
            }
            _log.Verbose("looking for {0}", package.Package);
            // Try locating the install folder again.
            packagePath = GetPackagePath(root, package.Package);

            // Get the files.
            var result = _contentResolver.GetFiles(packagePath, package, type);
            if (result.Count == 0)
            {
                if (type == PackageType.Addin)
                {
                    var framework = _environment.Runtime.BuiltFramework;
                    _log.Warning("Could not find any assemblies compatible with {0}.", framework.FullName);
                }
                else if (type == PackageType.Tool)
                {
                    const string format = "Could not find any relevant files for tool '{0}'. Perhaps you need an include parameter?";
                    _log.Warning(format, package.Package);
                }
            }

            return result;
        }

        private static DirectoryPath GetPackagePath(IDirectory root, string package)
        {
            var directories = root.GetDirectories("*", SearchScope.Current).ToArray();
            var toolsDir = directories.FirstOrDefault(p => p.Path.GetDirectoryName().Equals("tools", StringComparison.OrdinalIgnoreCase))?.Path;
            var packageDir = directories.FirstOrDefault(p => p.Path.GetDirectoryName().Equals(package, StringComparison.OrdinalIgnoreCase))?.Path;
            if (packageDir != null)
            {
                return packageDir;
            }
            return toolsDir;
        }

        private static DirectoryPath GetPackagePath(DirectoryPath root, PackageReference package)
        {
            if (package.Parameters.ContainsKey("version"))
            {
                var version = package.Parameters["version"].First();
                return root.Combine($"{package.Package}.{version}".ToLowerInvariant());
            }
            return root.Combine(package.Package.ToLowerInvariant());
        }

        private bool InstallPackage(PackageReference package, DirectoryPath path)
        {
            _log.Debug("Installing NuGet package {0}...", package.Package);

            var nugetPath = GetNuGetPath();
            var process = _processRunner.Start(nugetPath, new ProcessSettings
            {
                Arguments = GetArguments(package, path, _config),
                RedirectStandardOutput = true,
                Silent = _log.Verbosity < Verbosity.Diagnostic
            });
            process.WaitForExit();

            var exitCode = process.GetExitCode();
            if (exitCode != 0)
            {
                _log.Warning("NuGet exited with {0}", exitCode);
                var output = string.Join(Environment.NewLine, process.GetStandardOutput());
                _log.Verbose(Verbosity.Diagnostic, "Output:\r\n{0}", output);
                return false;
            }

            return true;
        }

        private FilePath GetNuGetPath()
        {
            var nugetPath = _toolResolver.ResolvePath();
            if (nugetPath == null)
            {
                throw new CakeException("Failed to find nuget.exe.");
            }
            return nugetPath;
        }

        private static ProcessArgumentBuilder GetArguments(
            PackageReference definition,
            DirectoryPath installationRoot, ICakeConfiguration config)
        {
            var arguments = new ProcessArgumentBuilder();

            arguments.Append("install");
            arguments.AppendQuoted(definition.Package);

            // Output directory
            arguments.Append("-OutputDirectory");
            arguments.AppendQuoted(installationRoot.FullPath);

            // if an absolute uri is specified for source, use this
            // otherwise check config for customise package source/s
            if (definition.Address != null)
            {
                arguments.Append("-Source");
                arguments.AppendQuoted(definition.Address.AbsoluteUri);
            }
            else
            {
                var nugetSource = config.GetValue(Constants.NuGet.Source);
                if (!string.IsNullOrWhiteSpace(nugetSource))
                {
                    arguments.Append("-Source");
                    arguments.AppendQuoted(nugetSource);
                }
            }

            // Config
            var nugetConfig = config.GetValue(Constants.NuGet.ConfigFile);
            if (!string.IsNullOrWhiteSpace(nugetConfig))
            {
                arguments.Append("-ConfigFile");
                arguments.AppendQuoted(nugetConfig);
            }

            // Version
            if (definition.Parameters.ContainsKey("version"))
            {
                arguments.Append("-Version");
                arguments.AppendQuoted(definition.Parameters["version"].First());
            }

            // Prerelease
            if (definition.Parameters.ContainsKey("prerelease"))
            {
                arguments.Append("-Prerelease");
            }

            // NoCache
            if (definition.Parameters.ContainsKey("nocache"))
            {
                arguments.Append("-NoCache");
            }

            arguments.Append("-ExcludeVersion");
            arguments.Append("-NonInteractive");
            return arguments;
        }
    }
}