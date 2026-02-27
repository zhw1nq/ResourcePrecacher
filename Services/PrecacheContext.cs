namespace ResourcePrecacher
{
    using CounterStrikeSharp.API.Core;
    using CounterStrikeSharp.API.Core.Plugin;

    using Microsoft.Extensions.Logging;

    using SteamDatabase.ValvePak;

    using System.Diagnostics;
    using System.Net.Http;
    using System.Net.Http.Headers;
    using System.Text;
    using System.Text.Json;

    public sealed class PrecacheContext
    {
        public required Plugin Plugin;

        private HashSet<string> Resources = new HashSet<string>();

        private readonly ILogger<PrecacheContext> Logger;

        private readonly PluginContext PluginContext;

        public int ResourceCount => this.Resources.Count;

        public string AssetsDirectory => Path.Combine(this.Plugin.ModuleDirectory, "Assets");

        private HashSet<string> ResourceTypes = new HashSet<string>()
        {
            "vmdl",     "vmdl_c",
            "vpcf",     "vpcf_c",
            "vmat",     "vmat_c",
            "vcompmat", "vcompmat_c",
            "vtex",     "vtex_c",
            "vsnd",     "vsnd_c",
            "vdata",    "vdata_c",
            "vpost",    "vpost_c",
            "vsurf",    "vsurf_c",
            "vanim",    "vanim_c",
            "vanmgrph", "vanmgrph_c",
            "vseq",     "vseq_c",
            "vmix",     "vmix_c",
            "vnmclip",  "vnmclip_c",
            "vrman",    "vrman_c",
            "vrr",      "vrr_c",
            "vsc",
            "vsmart",   "vsmart_c",
            "vsnap",    "vsnap_c",
            "vsndevts", "vsndevts_c",
            "vsndgrps",
            "vsndstck", "vsndstck_c",
            "vsvg",     "vsvg_c",
            "vts",      "vts_c",
            "vxml",     "vxml_c"
        };

        public PrecacheContext(ILogger<PrecacheContext> logger, IPluginContext pluginContext)
        {
            this.Logger = logger;
            this.PluginContext = (pluginContext as PluginContext)!;
        }

        public void Initialize()
        {
            this.Plugin = (this.PluginContext.Plugin as Plugin)!;

            if (Directory.Exists(this.AssetsDirectory))
            {
                foreach (string vpkPath in Directory.EnumerateFiles(this.AssetsDirectory, "*.vpk", SearchOption.AllDirectories))
                {
                    // we can only read the `_dir` vpks
                    if (vpkPath.EndsWith("_000.vpk"))
                        continue;

                    string packageName = Path.GetFileNameWithoutExtension(vpkPath);

                    using (Package package = new Package())
                    {
                        try
                        {
                            this.Logger.LogInformation("Reading Workshop Package: '{0}'", packageName);

                            package.Read(vpkPath);

                            if (package.Entries == null)
                                continue;

                            foreach (KeyValuePair<string, List<PackageEntry>> fileType in package.Entries)
                            {
                                if (!this.ResourceTypes.Contains(fileType.Key))
                                    continue;

                                foreach (PackageEntry entry in fileType.Value)
                                {
                                    string fullPath = NormalizePath(entry.GetFullPath());

                                    if (fullPath.EndsWith("_c"))
                                        fullPath = fullPath[..^2];

                                    if (!this.AddResource(fullPath))
                                    {
                                        this.Logger.LogWarning("Duplicate entry for resource: '{0}'", fullPath);
                                    }
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            this.Logger.LogError("Unable to read package: '{0}' ({1})", packageName, ex.Message);
                        }
                    }
                }
            }
            else
            {
                this.Logger.LogWarning("Assets directory not found: '{0}'. Skipping VPK loading.", this.AssetsDirectory);
            }

            this.Plugin.RegisterListener<Listeners.OnServerPrecacheResources>((manifest) =>
            {
                Stopwatch stopwatch = Stopwatch.StartNew();
                int precachedResources = 0;

                foreach (string resourcePath in this.Resources)
                {
                    if (this.Plugin.Config.Log)
                    {
                        this.Logger.LogInformation("Precaching \"{Resource}\" (context: {PrecacheContext}) [{Amount}/{Count}]", resourcePath, $"0x{manifest.Handle:X}", ++precachedResources, this.ResourceCount);
                    }

                    manifest.AddResource(resourcePath);
                }

                stopwatch.Stop();

                if (this.Plugin.Config.Log)
                {
                    this.Logger.LogInformation("Precached {ResourceCount} resources in {ElapsedMs}ms.", this.ResourceCount, stopwatch.ElapsedMilliseconds);
                }

                if (this.Plugin.Config.LogFile || !string.IsNullOrEmpty(this.Plugin.Config.DiscordWebhookUrl))
                {
                    this.WriteLogFile(stopwatch.ElapsedMilliseconds);
                }

                if (!string.IsNullOrEmpty(this.Plugin.Config.DiscordWebhookUrl))
                {
                    _ = Task.Run(() => this.SendDiscordWebhookAsync(stopwatch.ElapsedMilliseconds));
                }
            });
        }

        public bool AddResource(string resourcePath)
        {
            resourcePath = NormalizePath(resourcePath);

            string extension = Path.GetExtension(resourcePath)[1..];

            if (!this.ResourceTypes.Contains(extension))
            {
                this.Logger.LogError("Resource type '{0}' can not be precached. ({1})", extension, resourcePath);

                // it was handled "successfully", we only return false for duplicates because of HashSet<>
                return true;
            }

            return this.Resources.Add(resourcePath);
        }

        public bool RemoveResource(string resourcePath)
        {
            resourcePath = NormalizePath(resourcePath);

            return this.Resources.Remove(resourcePath);
        }

        /// <summary>
        /// Source 2 engine always uses forward slashes for resource paths.
        /// Normalize all paths to use '/' regardless of OS.
        /// </summary>
        private static string NormalizePath(string path)
        {
            return path.Replace('\\', '/');
        }

        private string LogDirectory
        {
            get
            {
                // Navigate from ModuleDirectory (plugins/ResourcePrecacher) up to plugins/, then into Logs/
                string pluginsDir = Path.GetDirectoryName(this.Plugin.ModuleDirectory)!;
                return Path.Combine(pluginsDir, "Logs");
            }
        }

        private void WriteLogFile(long elapsedMs)
        {
            try
            {
                Directory.CreateDirectory(this.LogDirectory);

                string logPath = Path.Combine(this.LogDirectory, "precache_log.txt");
                StringBuilder sb = new StringBuilder();

                string hostname = System.Net.Dns.GetHostName();

                sb.AppendLine("============================================");
                sb.AppendLine($"Precache Log - {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                sb.AppendLine($"Server: {hostname}");
                sb.AppendLine($"Precache Time: {elapsedMs}ms");
                sb.AppendLine("============================================");
                sb.AppendLine();
                sb.AppendLine($"Total files precached: {this.ResourceCount}");
                sb.AppendLine();

                int index = 1;
                foreach (string resourcePath in this.Resources)
                {
                    string name = Path.GetFileName(resourcePath);
                    sb.AppendLine($"[{index}] {name}");
                    sb.AppendLine($"    Path: {resourcePath}");
                    sb.AppendLine();
                    index++;
                }

                sb.AppendLine("============================================");

                File.WriteAllText(logPath, sb.ToString());

                this.Logger.LogInformation("Precache log written to: {LogPath}", logPath);
            }
            catch (Exception ex)
            {
                this.Logger.LogError("Failed to write precache log file: {Error}", ex.Message);
            }
        }

        private async Task SendDiscordWebhookAsync(long elapsedMs)
        {
            try
            {
                string logPath = Path.Combine(this.LogDirectory, "precache_log.txt");

                using HttpClient client = new HttpClient();

                using MultipartFormDataContent form = new MultipartFormDataContent();

                // Build the embed payload
                var payload = new
                {
                    embeds = new[]
                    {
                        new
                        {
                            title = "📦 Resource Precacher",
                            description = $"Precache completed successfully.",
                            color = 0x00D166, // green
                            fields = new[]
                            {
                                new { name = "Server", value = System.Net.Dns.GetHostName(), inline = false },
                                new { name = "Total Files", value = this.ResourceCount.ToString(), inline = true },
                                new { name = "Time", value = $"{elapsedMs}ms", inline = true },
                                new { name = "Timestamp", value = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), inline = false }
                            }
                        }
                    }
                };

                string jsonPayload = JsonSerializer.Serialize(payload);
                form.Add(new StringContent(jsonPayload, Encoding.UTF8, "application/json"), "payload_json");

                // Attach log file if it exists
                if (File.Exists(logPath))
                {
                    byte[] fileBytes = await File.ReadAllBytesAsync(logPath);
                    ByteArrayContent fileContent = new ByteArrayContent(fileBytes);
                    fileContent.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
                    form.Add(fileContent, "files[0]", "precache_log.txt");
                }

                HttpResponseMessage response = await client.PostAsync(this.Plugin.Config.DiscordWebhookUrl, form);

                if (response.IsSuccessStatusCode)
                {
                    this.Logger.LogInformation("Discord webhook sent successfully.");
                }
                else
                {
                    this.Logger.LogWarning("Discord webhook returned status: {StatusCode}", response.StatusCode);
                }
            }
            catch (Exception ex)
            {
                this.Logger.LogError("Failed to send Discord webhook: {Error}", ex.Message);
            }
        }
    }
}
