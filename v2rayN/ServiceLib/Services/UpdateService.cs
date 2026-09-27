namespace ServiceLib.Services;

public partial class UpdateService(Config config, Func<bool, string, Task> updateFunc)
{
    private readonly Config? _config = config;
    private readonly Func<bool, string, Task>? _updateFunc = updateFunc;
    private static readonly string _tag = "UpdateService";

    public async Task CheckUpdateGuiN(bool preRelease, bool blProxy = true, CancellationToken cancellationToken = default)
    {
        var url = string.Empty;
        var fileName = string.Empty;

        DownloadService downloadHandle = new();
        downloadHandle.UpdateCompleted += (sender2, args) =>
        {
            if (args.Success)
            {
                _ = UpdateFunc(false, ResUI.MsgDownloadV2rayCoreSuccessfully);
                _ = UpdateFunc(true, Utils.UrlEncode(fileName));
            }
            else
            {
                _ = UpdateFunc(false, args.Msg);
            }
        };
        downloadHandle.Error += (sender2, args) =>
        {
            _ = UpdateFunc(false, args.GetException().Message);
        };

        await UpdateFunc(false, string.Format(ResUI.MsgStartUpdating, ECoreType.v2rayN));
        var result = await CheckUpdateAsync(downloadHandle, ECoreType.v2rayN, preRelease, blProxy);
        if (result.Success)
        {
            await UpdateFunc(false, string.Format(ResUI.MsgParsingSuccessfully, ECoreType.v2rayN));
            await UpdateFunc(false, result.Msg);

            url = result.Url!;
            fileName = Utils.GetTempPath(Utils.GetGuid());
            await downloadHandle.DownloadFileAsync(new() { FileUrl = url, FilePath = fileName }, blProxy, cancellationToken);
        }
        else
        {
            await UpdateFunc(false, result.Msg);
        }
    }

    public async Task CheckUpdateCore(ECoreType type, bool preRelease, bool blProxy = true, CancellationToken cancellationToken = default)
    {
        var url = string.Empty;
        var fileName = string.Empty;

        DownloadService downloadHandle = new();
        downloadHandle.UpdateCompleted += (sender2, args) =>
        {
            if (args.Success)
            {
                _ = UpdateFunc(false, ResUI.MsgDownloadV2rayCoreSuccessfully);
                _ = UpdateFunc(false, ResUI.MsgUnpacking);

                try
                {
                    _ = UpdateFunc(true, fileName);
                }
                catch (Exception ex)
                {
                    _ = UpdateFunc(false, ex.Message);
                }
            }
            else
            {
                _ = UpdateFunc(false, args.Msg);
            }
        };
        downloadHandle.Error += (sender2, args) =>
        {
            _ = UpdateFunc(false, args.GetException().Message);
        };

        await UpdateFunc(false, string.Format(ResUI.MsgStartUpdating, type));
        var result = await CheckUpdateAsync(downloadHandle, type, preRelease, blProxy);
        if (result.Success)
        {
            await UpdateFunc(false, string.Format(ResUI.MsgParsingSuccessfully, type));
            await UpdateFunc(false, result.Msg);

            url = result.Url!;
            var ext = url.Contains(".tar.gz") ? ".tar.gz" : Path.GetExtension(url);
            fileName = Utils.GetTempPath(Utils.GetGuid() + ext);
            await downloadHandle.DownloadFileAsync(new() { FileUrl = url, FilePath = fileName }, blProxy, cancellationToken);
        }
        else
        {
            if (!result.Msg.IsNullOrEmpty())
            {
                await UpdateFunc(false, result.Msg);
            }
        }
    }

    public async Task<UpdateResult> CheckHasUpdateOnly(ECoreType type, bool preRelease, bool blProxy = true, CancellationToken cancellationToken = default)
    {
        if (!CoreInfoManager.Instance.IsCheckUpdateSupported(type))
        {
            return new UpdateResult(false, ResUI.MsgNotSupport);
        }

        var downloadHandle = new DownloadService();
        var checkPreRelease = CoreInfoManager.Instance.GetCheckPreRelease(type, preRelease);
        return await CheckUpdateAsync(downloadHandle, type, checkPreRelease, blProxy, cancellationToken);
    }

    public async Task<List<string>> CheckHasUpdateOnlyAll(bool preRelease, bool blProxy = true, CancellationToken cancellationToken = default)
    {
        var msgs = new List<string>();
        foreach (var type in CoreInfoManager.Instance.GetCheckUpdateCoreTypes())
        {
            if (!(_config.CheckUpdateItem.SelectedCoreTypes?.Contains(type.ToString()) ?? true))
            {
                continue;
            }

            var result = await CheckHasUpdateOnly(type, preRelease, blProxy, cancellationToken);
            if (result.Success && result.Version != null)
            {
                var msg = string.Format(ResUI.MsgCheckUpdateHasNewVersion, type, result.Version);
                msgs.Add(msg);
                AppManager.Instance.SetLastCheckUpdateResult(type, msg);
            }
            else
            {
                AppManager.Instance.SetLastCheckUpdateResult(type, result.Msg);
            }
        }
        return msgs;
    }

    public async Task UpdateGeoFileAll(bool blProxy = true, CancellationToken cancellationToken = default)
    {
        var staged = await StageGeoFileAllAsync(blProxy, cancellationToken);
        await ApplyGeoFileStageAsync(staged, cancellationToken);
        await UpdateFunc(true, string.Format(ResUI.MsgDownloadGeoFileSuccessfully, "geo"));
    }

    public async Task<GeoFileUpdateStage> StageGeoFileAllAsync(bool blProxy = true, CancellationToken cancellationToken = default)
    {
        var requests = new List<FileDownloadRequest>();
        requests.AddRange(GetGeoFilesRequest());
        requests.AddRange(GetOtherFilesRequest());
        requests.AddRange(await GetSrsFileAllRequest());
        // NOTE: srs files are more small, so we reverse the order to ensure a good download experience for the user.
        requests.Reverse();

        var id = Guid.NewGuid().ToString("N");
        var stagedEntries = new List<GeoFileUpdateStageEntry>(requests.Count);
        try
        {
            var tempRequests = new List<FileDownloadRequest>(requests.Count);
            foreach (var request in requests)
            {
                var temporaryPath = Utils.GetTempPath($"geo-stage-{id}-{Guid.NewGuid():N}");
                stagedEntries.Add(new GeoFileUpdateStageEntry(request.FilePath, temporaryPath));
                tempRequests.Add(request with { FilePath = temporaryPath });
            }

            var failure = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
            var download = new DownloadService();
            download.Error += (_, args) => failure.TrySetResult(args.GetException());
            download.UpdateCompleted += (_, state) =>
            {
                if (!state.Success && !string.IsNullOrWhiteSpace(state.Msg))
                {
                    _ = UpdateFunc(false, state.Msg);
                }
            };
            await download.DownloadSmallFilesAsync(tempRequests, blProxy, cancellationToken);
            if (failure.Task.IsCompletedSuccessfully)
            {
                throw new IOException("GeoFiles download failed.", await failure.Task);
            }
            foreach (var item in stagedEntries)
            {
                if (!File.Exists(item.TemporaryPath) || new FileInfo(item.TemporaryPath).Length <= 0)
                {
                    throw new IOException($"GeoFiles download did not stage a complete file for {Path.GetFileName(item.TargetPath)}.");
                }
            }
            return new GeoFileUpdateStage(id, stagedEntries);
        }
        catch
        {
            foreach (var item in stagedEntries)
            {
                if (File.Exists(item.TemporaryPath)) File.Delete(item.TemporaryPath);
            }
            throw;
        }
    }

    public async Task ApplyGeoFileStageAsync(GeoFileUpdateStage staged, CancellationToken cancellationToken = default)
    {
        var backupDirectory = Utils.GetTempPath($"geo-backup-{staged.Id}");
        Directory.CreateDirectory(backupDirectory);
        var backups = new List<(string Target, string? Backup)>();
        var replacements = new List<string>();
        var applySucceeded = false;
        var rollbackSucceeded = false;
        try
        {
            // Snapshot every current target before replacing any of them. If one replacement
            // fails, the Core/Geo set is restored as a unit rather than left half-applied.
            foreach (var item in staged.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string? backupPath = null;
                if (File.Exists(item.TargetPath))
                {
                    backupPath = Path.Combine(backupDirectory, Guid.NewGuid().ToString("N"));
                    File.Copy(item.TargetPath, backupPath);
                }
                backups.Add((item.TargetPath, backupPath));
            }

            foreach (var item in staged.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var parent = Path.GetDirectoryName(item.TargetPath)
                    ?? throw new IOException("A GeoFiles target has no parent directory.");
                Directory.CreateDirectory(parent);
                var replacement = Path.Combine(parent, $".{Path.GetFileName(item.TargetPath)}.{staged.Id}.new");
                replacements.Add(replacement);
                File.Copy(item.TemporaryPath, replacement, overwrite: true);
                File.Move(replacement, item.TargetPath, overwrite: true);
            }
            applySucceeded = true;
        }
        catch (Exception applyException)
        {
            try
            {
                foreach (var item in backups.AsEnumerable().Reverse())
                {
                    if (item.Backup is null)
                    {
                        if (File.Exists(item.Target)) File.Delete(item.Target);
                    }
                    else if (File.Exists(item.Backup))
                    {
                        File.Copy(item.Backup, item.Target, overwrite: true);
                    }
                }
                rollbackSucceeded = true;
            }
            catch (Exception rollbackException)
            {
                throw new IOException(
                    $"GeoFiles apply failed and rollback is incomplete; the backup set was retained at {backupDirectory}.",
                    new AggregateException(applyException, rollbackException));
            }
            throw;
        }
        finally
        {
            foreach (var item in staged.Files)
            {
                if (File.Exists(item.TemporaryPath)) File.Delete(item.TemporaryPath);
            }
            foreach (var replacement in replacements)
            {
                if (File.Exists(replacement)) File.Delete(replacement);
            }
            if ((applySucceeded || rollbackSucceeded) && Directory.Exists(backupDirectory))
                Directory.Delete(backupDirectory, recursive: true);
        }
    }

    public void DiscardGeoFileStage(GeoFileUpdateStage staged)
    {
        foreach (var item in staged.Files)
        {
            try
            {
                if (File.Exists(item.TemporaryPath)) File.Delete(item.TemporaryPath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Logging.SaveLog(_tag, exception);
            }
        }
    }

    #region CheckUpdate private

    private async Task<UpdateResult> CheckUpdateAsync(DownloadService downloadHandle, ECoreType type, bool preRelease, bool blProxy, CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await GetRemoteVersion(downloadHandle, type, preRelease, blProxy, cancellationToken);
            if (!result.Success || result.Version is null)
            {
                return result;
            }
            return await ParseDownloadUrl(type, result, cancellationToken);
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
            await UpdateFunc(false, ex.Message);
            return new UpdateResult(false, ex.Message);
        }
    }

    private async Task<UpdateResult> GetRemoteVersion(DownloadService downloadHandle, ECoreType type, bool preRelease, bool blProxy, CancellationToken cancellationToken = default)
    {
        var coreInfo = CoreInfoManager.Instance.GetCoreInfo(type);
        var tagName = string.Empty;
        if (preRelease || coreInfo?.LockedMaxVersion != null)
        {
            var url = coreInfo?.ReleaseApiUrl;
            var result = await downloadHandle.TryDownloadString(url, blProxy, Global.AppName, cancellationToken);
            if (result.IsNullOrEmpty())
            {
                return new UpdateResult(false, "");
            }

            var gitHubReleases = JsonUtils.Deserialize<List<GitHubRelease>>(result);
            var gitHubRelease = preRelease ? gitHubReleases?.First() : gitHubReleases?.First(r => r.Prerelease == false);
            tagName = gitHubRelease?.TagName;
            //var body = gitHubRelease?.Body;

            if (coreInfo?.LockedMaxVersion != null)
            {
                var lockedMaxVersion = coreInfo.LockedMaxVersion;
                var remoteVersion = new SemanticVersion(tagName);
                if (remoteVersion > lockedMaxVersion)
                {
                    var fallbackRelease = gitHubReleases?
                        .Where(r => preRelease || !r.Prerelease)
                        .Select(r => new { Release = r, IsValid = SemanticVersion.TryParse(r.TagName, out var v), Version = v })
                        .Where(x => x.IsValid && x.Version <= coreInfo.LockedMaxVersion)
                        .MaxBy(x => x.Version)?
                        .Release;

                    gitHubRelease = fallbackRelease;
                    tagName = gitHubRelease?.TagName;
                }
            }
        }
        else
        {
            var url = Path.Combine(coreInfo.Url, "latest");
            var lastUrl = await downloadHandle.UrlRedirectAsync(url, blProxy, cancellationToken);
            if (lastUrl == null)
            {
                return new UpdateResult(false, "");
            }

            tagName = lastUrl?.Split("/tag/").LastOrDefault();
        }
        return new UpdateResult(true, new SemanticVersion(tagName));
    }

    [GeneratedRegex(@"v?(?<version>\d+\.\d+\.\d+(?:-[0-9a-zA-Z.-]+)?(?:\+[0-9a-zA-Z.-]+)?)", RegexOptions.IgnoreCase)]
    private static partial Regex SemVerRegex();

    private async Task<SemanticVersion> GetCoreVersion(ECoreType type, CancellationToken cancellationToken = default)
    {
        try
        {
            var coreInfo = CoreInfoManager.Instance.GetCoreInfo(type);
            var filePath = string.Empty;
            foreach (var name in coreInfo.CoreExes)
            {
                var vName = Utils.GetBinPath(Utils.GetExeName(name), coreInfo.CoreType.ToString());
                if (File.Exists(vName))
                {
                    filePath = vName;
                    break;
                }
            }

            if (!File.Exists(filePath))
            {
                var msg = string.Format(ResUI.NotFoundCore, @"", "", "");
                //ShowMsg(true, msg);
                return new SemanticVersion("");
            }

            var result = await Utils.GetCliWrapOutput(filePath, coreInfo.VersionArg, cancellationToken);
            var echo = result ?? "";
            var version = SemVerRegex().Match(echo).Groups["version"].Value;
            return new SemanticVersion(version);
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
            await UpdateFunc(false, ex.Message);
            return new SemanticVersion("");
        }
    }

    private async Task<UpdateResult> ParseDownloadUrl(ECoreType type, UpdateResult result, CancellationToken cancellationToken = default)
    {
        try
        {
            var version = result.Version ?? new SemanticVersion(0, 0, 0);
            var coreInfo = CoreInfoManager.Instance.GetCoreInfo(type);
            var coreUrl = await GetUrlFromCore(coreInfo) ?? string.Empty;
            SemanticVersion curVersion;
            string message;
            string? url;
            switch (type)
            {
                case ECoreType.v2fly:
                case ECoreType.Xray:
                case ECoreType.v2fly_v5:
                case ECoreType.mihomo:
                    {
                        curVersion = await GetCoreVersion(type, cancellationToken);
                        message = string.Format(ResUI.IsLatestCore, type, curVersion.ToStandardVersionString("v"));
                        url = string.Format(coreUrl, version);
                        break;
                    }

                case ECoreType.sing_box:
                    {
                        curVersion = await GetCoreVersion(type, cancellationToken);
                        message = string.Format(ResUI.IsLatestCore, type, curVersion.ToStandardVersionString("v"));
                        url = string.Format(coreUrl, version, version.ToString().RemovePrefix("v"));
                        break;
                    }
                case ECoreType.v2rayN:
                    {
                        curVersion = new SemanticVersion(Utils.GetVersionInfo());
                        message = string.Format(ResUI.IsLatestN, type, curVersion.ToStandardVersionString("v"));
                        url = string.Format(coreUrl, version);
                        break;
                    }
                default:
                    throw new ArgumentException("Type");
            }

            if (curVersion >= version && !version.Equals(new SemanticVersion(0, 0, 0)))
            {
                return new UpdateResult(false, message);
            }

            result.Url = url;
            return result;
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
            await UpdateFunc(false, ex.Message);
            return new UpdateResult(false, ex.Message);
        }
    }

    private async Task<string?> GetUrlFromCore(CoreInfo? coreInfo)
    {
        if (Utils.IsWindows())
        {
            var url = RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.Arm64 => coreInfo?.DownloadUrlWinArm64,
                Architecture.X64 => coreInfo?.DownloadUrlWin64,
                _ => null,
            };

            if (coreInfo?.CoreType != ECoreType.v2rayN)
            {
                return url;
            }

            //Check for avalonia desktop windows version
            if (File.Exists(Path.Combine(Utils.GetBaseDirectory(), "libHarfBuzzSharp.dll")))
            {
                return url?.Replace(".zip", "-desktop.zip");
            }

            return url;
        }
        else if (Utils.IsLinux())
        {
            return RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.Arm64 => coreInfo?.DownloadUrlLinuxArm64,
                Architecture.RiscV64 => coreInfo?.DownloadUrlLinuxRiscV64,
                Architecture.LoongArch64 => coreInfo?.DownloadUrlLinuxLoong64,
                Architecture.X64 => coreInfo?.DownloadUrlLinux64,
                _ => null,
            };
        }
        else if (Utils.IsMacOS())
        {
            return RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.Arm64 => coreInfo?.DownloadUrlOSXArm64,
                Architecture.X64 => coreInfo?.DownloadUrlOSX64,
                _ => null,
            };
        }
        return await Task.FromResult("");
    }

    #endregion CheckUpdate private

    #region Geo private

    private List<FileDownloadRequest> GetGeoFilesRequest()
    {
        var geoUrl = string.IsNullOrEmpty(_config?.ConstItem.GeoSourceUrl)
            ? Global.GeoUrl
            : _config.ConstItem.GeoSourceUrl;

        List<string> files = ["geosite", "geoip"];
        return
        [
            .. from geoName in files
            let fileName = $"{geoName}.dat"
            let targetPath = Utils.GetBinPath($"{fileName}")
            let url = string.Format(geoUrl, geoName)
            select new FileDownloadRequest()
            {
                FileUrl = url,
                FilePath = targetPath,
                DisplayFileName = fileName,
            },
        ];
    }

    private List<FileDownloadRequest> GetOtherFilesRequest()
    {
        //If it is not in China area, no update is required
        if (_config.ConstItem.GeoSourceUrl.IsNotEmpty())
        {
            return [];
        }

        return
        [
            .. Global.OtherGeoUrls.Select(url =>
            {
                var fileName = Path.GetFileName(url);
                var targetPath = Utils.GetBinPath($"{fileName}");
                return new FileDownloadRequest()
                {
                    FileUrl = url,
                    FilePath = targetPath,
                    DisplayFileName = fileName,
                };
            }),
        ];
    }

    private async Task<List<FileDownloadRequest>> GetSrsFileAllRequest()
    {
        var geoipFiles = new List<string>();
        var geoSiteFiles = new List<string>();

        // Collect from routing rules
        var routingItems = await AppManager.Instance.RoutingItems();
        foreach (var routing in routingItems)
        {
            var rules = JsonUtils.Deserialize<List<RulesItem>>(routing.RuleSet);
            foreach (var item in rules ?? [])
            {
                AddPrefixedItems(item.Ip, Global.GeoIPPrefix, geoipFiles);
                AddPrefixedItems(item.Domain, Global.GeoSitePrefix, geoSiteFiles);
            }
        }

        // Collect from DNS configuration
        var dnsItem = await AppManager.Instance.GetDNSItem(ECoreType.sing_box);
        if (dnsItem != null)
        {
            ExtractDnsRuleSets(dnsItem.NormalDNS, geoipFiles, geoSiteFiles);
            ExtractDnsRuleSets(dnsItem.TunDNS, geoipFiles, geoSiteFiles);
        }

        // Append default items
        geoSiteFiles.AddRange(["google", "cn", "geolocation-cn", "category-ads-all"]);

        // Download files
        var path = Utils.GetBinPath("srss");
        if (!Directory.Exists(path))
        {
            Directory.CreateDirectory(path);
        }

        return
        [
            .. geoipFiles.Distinct().Select(f => (type: "geoip", file: f))
                .Concat(geoSiteFiles.Distinct().Select(f => (type: "geosite", file: f)))
                .Select(item => GetSrsFileRequest(item.type, item.file)),
        ];
    }

    private void AddPrefixedItems(List<string>? items, string prefix, List<string> output)
    {
        if (items == null)
        {
            return;
        }

        foreach (var item in items)
        {
            if (item.StartsWith(prefix))
            {
                output.Add(item.Substring(prefix.Length));
            }
        }
    }

    private void ExtractDnsRuleSets(string? dnsJson, List<string> geoipFiles, List<string> geoSiteFiles)
    {
        if (string.IsNullOrEmpty(dnsJson))
        {
            return;
        }

        try
        {
            var dns = JsonUtils.Deserialize<Dns4Sbox>(dnsJson);
            if (dns?.rules != null)
            {
                foreach (var rule in dns.rules)
                {
                    ExtractSrsRuleSets(rule, geoipFiles, geoSiteFiles);
                }
            }
        }
        catch { }
    }

    private void ExtractSrsRuleSets(Rule4Sbox? rule, List<string> geoipFiles, List<string> geoSiteFiles)
    {
        if (rule == null)
        {
            return;
        }

        AddPrefixedItems(rule.rule_set, "geosite-", geoSiteFiles);
        AddPrefixedItems(rule.rule_set, "geoip-", geoipFiles);

        // Handle nested rules recursively
        if (rule.rules != null)
        {
            foreach (var nestedRule in rule.rules)
            {
                ExtractSrsRuleSets(nestedRule, geoipFiles, geoSiteFiles);
            }
        }
    }

    private FileDownloadRequest GetSrsFileRequest(string type, string srsName)
    {
        var srsUrl = string.IsNullOrEmpty(_config.ConstItem.SrsSourceUrl)
                        ? Global.SingboxRulesetUrl
                        : _config.ConstItem.SrsSourceUrl;

        var fileName = $"{type}-{srsName}.srs";
        var targetPath = Path.Combine(Utils.GetBinPath("srss"), fileName);
        var url = string.Format(srsUrl, type, $"{type}-{srsName}", srsName);

        return new FileDownloadRequest()
        {
            FileUrl = url,
            FilePath = targetPath,
            DisplayFileName = fileName,
        };
    }

    #endregion Geo private

    private async Task UpdateFunc(bool notify, string msg)
    {
        await _updateFunc?.Invoke(notify, msg);
    }
}

public sealed record GeoFileUpdateStage(string Id, IReadOnlyList<GeoFileUpdateStageEntry> Files);

public sealed record GeoFileUpdateStageEntry(string TargetPath, string TemporaryPath);
