// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using NuGet;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json;
using Microsoft.Dnx.Runtime.Internal;

namespace Microsoft.Dnx.Tooling.Restore.NuGet
{
    internal class NuGetv3Feed : IPackageFeed
    {
        private readonly string _baseUri;
        private readonly Reports _reports;
        private readonly HttpSource _httpSource;
        private readonly HttpSource _unAuthenticatedHttpSource;
        private readonly TimeSpan _cacheAgeLimitList;
        private readonly TimeSpan _cacheAgeLimitNupkg;
        private readonly bool _ignoreFailure;
        private readonly bool _usingRegistrationUri;
        private bool _ignored;

        private readonly Dictionary<string, Task<IEnumerable<PackageInfo>>> _packageVersionsCache = new Dictionary<string, Task<IEnumerable<PackageInfo>>>();
        private readonly Dictionary<string, Task<NupkgEntry>> _nupkgCache = new Dictionary<string, Task<NupkgEntry>>();

        public string Source
        {
            get
            {
                return _httpSource.BaseUri;
            }
        }

        internal NuGetv3Feed(
            HttpSource httpSource,
            bool noCache,
            Reports reports,
            bool ignoreFailure,
            bool usingRegistrationUri = false)
        {
            _baseUri = httpSource.BaseUri;
            _reports = reports;
            _httpSource = httpSource;
            _ignoreFailure = ignoreFailure;
            _usingRegistrationUri = usingRegistrationUri;
            if (_usingRegistrationUri)
            {
                _unAuthenticatedHttpSource = new HttpSource(_baseUri, null, null, reports);
            }
            if (noCache)
            {
                _cacheAgeLimitList = TimeSpan.Zero;
                _cacheAgeLimitNupkg = TimeSpan.Zero;
            }
            else
            {
                _cacheAgeLimitList = TimeSpan.FromMinutes(30);
                _cacheAgeLimitNupkg = TimeSpan.FromHours(24);
            }
        }

        internal static NuGetv3Feed DetectNuGetV3(
            HttpSource httpSource,
            bool noCache,
            string username,
            string password,
            Reports reports,
            bool ignoreFailedSources)
        {
            var cacheAgeLimit = noCache ? TimeSpan.Zero : TimeSpan.FromDays(7);
            try
            {
                var result = httpSource.GetAsync(httpSource.BaseUri, "index_json", cacheAgeLimit).Result;
                using (var reader = new JsonTextReader(new StreamReader(result.Stream)))
                {
                    var indexJson = JObject.Load(reader);
                    var providedResources = indexJson["resources"]
                        .Select(res => new { type = res.Value<string>("@type"), id = res.Value<string>("@id") })
                        .Where(res => res.id != null)
                        .ToArray();

                    var baseAddressResource = providedResources
                        .FirstOrDefault(res => string.Equals(res.type,"PackageBaseAddress/3.0.0"));
                    if (baseAddressResource != null)
                    {
                        string uri;
                        try
                        {
                            uri = new Uri(baseAddressResource.id).AbsoluteUri;
                        }
                        catch (UriFormatException)
                        {
                            uri = new Uri(new Uri(httpSource.BaseUri), baseAddressResource.id).AbsoluteUri;
                        }
                        return new NuGetv3Feed(
                            new HttpSource(
                                uri,
                                username,
                                password,
                                reports),
                            noCache,
                            reports,
                            ignoreFailedSources);
                    }

                    var registrationsBaseUrlResource = providedResources
                        .FirstOrDefault(res => string.Equals(res.type, "RegistrationsBaseUrl/3.0.0-beta"));
                    if (registrationsBaseUrlResource != null)
                    {
                        var registrationSource = new HttpSource(
                                    new Uri(registrationsBaseUrlResource.id).AbsoluteUri + "/",
                                    username,
                                    password,
                                    reports);

                        return new NuGetv3Feed(
                            registrationSource,
                            noCache,
                            reports,
                            ignoreFailedSources,
                            usingRegistrationUri: true);
                    }
                }
                reports.Information.WriteLine(
                    $"Ignoring NuGet v3 feed {httpSource.BaseUri.Yellow().Bold()}, which doesn't provide a usable resource.");
                return null;
            }
            catch
            {
                return null;
            }
        }

        public Task<IEnumerable<PackageInfo>> FindPackagesByIdAsync(string id)
        {
            lock (_packageVersionsCache)
            {
                Task<IEnumerable<PackageInfo>> task;
                if (_packageVersionsCache.TryGetValue(id, out task))
                {
                    return task;
                }
                return _packageVersionsCache[id] = FindPackagesByIdAsyncCore(id);
            }
        }

        public async Task<IEnumerable<PackageInfo>> FindPackagesByIdAsyncCore(string id)
        {
            for (int retry = 0; retry != 3; ++retry)
            {
                if (_ignored)
                {
                    return new List<PackageInfo>();
                }

                try
                {
                    var uri = _baseUri + id.ToLowerInvariant() + "/index.json";
                    var results = new List<PackageInfo>();
                    using (var data = await _httpSource.GetAsync(uri,
                        string.Format("list_{0}", id),
                        retry == 0 ? _cacheAgeLimitList : TimeSpan.Zero,
                        ensureValidContents: stream => EnsureValidFindPackagesResponse(stream, uri),
                        throwNotFound: false))
                    {
                        if (data.Stream == null)
                        {
                            return Enumerable.Empty<PackageInfo>();
                        }

                        try
                        {
                            JObject doc;
                            using (var reader = new StreamReader(data.Stream))
                            {
                                doc = JObject.Load(new JsonTextReader(reader));
                            }

                            if (_usingRegistrationUri)
                            {
                                try
                                {
                                    var result = doc["items"]
                                        .SelectMany(page => page["items"]
                                            .Select(item => BuildModel(
                                                id,
                                                item["catalogEntry"]["version"].Value<string>(),
                                                item))
                                            .Where(item => item != null));

                                    results.AddRange(result);
                                }
                                catch (Exception e)
                                {
                                    _reports.Information.WriteLine(e.Message);
                                    throw;
                                }
                            }
                            else
                            {
                                var versions = doc["versions"];

                                if (versions == null)
                                {
                                    // Absence of "versions" property is equivalent to an empty "versions" array
                                    return Enumerable.Empty<PackageInfo>();
                                }

                                var result = versions
                                    .Select(x => BuildModel(id, x.Value<string>(), doc))
                                    .Where(x => x != null);

                                results.AddRange(result);
                            }
                        }
                        catch
                        {
                            _reports.Information.WriteLine("The file {0} is corrupt",
                                data.CacheFileName.Yellow().Bold());
                            throw;
                        }
                    }

                    return results;
                }
                catch (Exception ex)
                {
                    var isFinalAttempt = (retry == 2);
                    var message = ex.Message;
                    if (ex is TaskCanceledException)
                    {
                        message = ErrorMessageUtils.GetFriendlyTimeoutErrorMessage(
                            ex as TaskCanceledException,
                            isFinalAttempt,
                            _ignoreFailure);
                    }

                    if (isFinalAttempt)
                    {
                        // Fail silently by returning empty result list
                        if (_ignoreFailure)
                        {
                            _ignored = true;
                            _reports.Information.WriteLine(
                                $"Warning: FindPackagesById: {id}{Environment.NewLine}  {message}".Yellow().Bold());
                            return new List<PackageInfo>();
                        }

                        _reports.Error.WriteLine(
                            $"Error: FindPackagesById: {id}{Environment.NewLine}  {message}".Red().Bold());
                        throw;
                    }
                    else
                    {
                        _reports.Information.WriteLine(
                            $"Warning: FindPackagesById: {id}{Environment.NewLine}  {message}".Yellow().Bold());
                    }
                }
            }
            return null;
        }

        public PackageInfo BuildModel(string id, string version, JToken item)
        {
            var lowerInvariantId = id.ToLowerInvariant();
            var lowerInvariantVersion = version.ToLowerInvariant();

            string contentUri;
            if (_usingRegistrationUri)
            {
                contentUri = item["packageContent"].Value<string>();
            }
            else
            {
                contentUri = $"{_baseUri}{lowerInvariantId}/{lowerInvariantVersion}/{lowerInvariantId}.{lowerInvariantVersion}{Constants.PackageExtension}";
            }

            return new PackageInfo {
                // If 'Id' element exist, use its value as accurate package Id
                // Otherwise, use the value of 'title' if it exist
                // Use the given Id as final fallback if all elements above don't exist
                Id = id,
                Version = SemanticVersion.Parse(version),
                ContentUri = contentUri,

                // v3 feed doesn't indicate if listed?
                Listed = true
            };
        }

        public async Task<Stream> OpenNuspecStreamAsync(PackageInfo package)
        {
            return await PackageUtilities.OpenNuspecStreamFromNupkgAsync(package, OpenNupkgStreamAsync, _reports.Information);
        }

        public async Task<Stream> OpenRuntimeStreamAsync(PackageInfo package)
        {
            return await PackageUtilities.OpenRuntimeStreamFromNupkgAsync(package, OpenNupkgStreamAsync, _reports.Information);
        }

        public async Task<Stream> OpenNupkgStreamAsync(PackageInfo package)
        {
            Task<NupkgEntry> task;
            HttpSource httpSource = _usingRegistrationUri ? _unAuthenticatedHttpSource : _httpSource;
            lock (_nupkgCache)
            {
                if (!_nupkgCache.TryGetValue(package.ContentUri, out task))
                {
                    task = _nupkgCache[package.ContentUri] = PackageUtilities.OpenNupkgStreamAsync(
                        httpSource, package, _cacheAgeLimitNupkg, _reports);
                }
            }
            var result = await task;
            if (result == null)
            {
                return null;
            }

            // Acquire the lock on a file before we open it to prevent this process
            // from opening a file deleted by the logic in HttpSource.GetAsync() in another process
            return await ConcurrencyUtilities.ExecuteWithFileLocked(result.TempFileName, _ => {
                return Task.FromResult(
                    new FileStream(result.TempFileName, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete));
            });
        }

        private static void EnsureValidFindPackagesResponse(Stream stream, string uri)
        {
            var message = $"Response from {uri} is not a valid NuGet v3 service response.";
            try
            {
                var json = JObject.Load(new JsonTextReader(new StreamReader(stream)));
                var versions = json["versions"];
                if (versions == null)
                {
                    // If the response is a valid JSON that doesn't contain "versions" property,
                    // we treat it as an empty "versions" array
                    return;
                }

                if (!(versions is JArray))
                {
                    throw new InvalidDataException(
                        $"{message} The value of 'versions' property is not an array.");
                }
            }
            catch (JsonException e)
            {
                throw new InvalidDataException(message, innerException: e);
            }
        }
    }
}
