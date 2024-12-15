﻿using Helldivers2ModManager.Models;
using Helldivers2ModManager.Services;
using Microsoft.Extensions.Logging;
using SharpCompress.Archives;
using System.IO;
using System.Text.RegularExpressions;

namespace Helldivers2ModManager.Stores;

internal sealed class ModEventArgs(ModData mod) : EventArgs
{
	public ModData Mod { get; } = mod;
}

internal delegate void ModEventHandler(object sender, ModEventArgs e);

internal sealed partial class ModStore
{
	private readonly struct PatchFileTriplet
	{
		public FileInfo? Patch { get; init; }

		public FileInfo? GpuResources { get; init; }

		public FileInfo? Stream { get; init; }
	}

	public IReadOnlyList<ModData> Mods => _mods;

	public event ModEventHandler? ModAdded;
	public event ModEventHandler? ModRemoved;

	private readonly ILogger<ModStore> _logger;
	private readonly SettingsStore _settingsStore;
	private readonly List<ModData> _mods;
	private readonly IModManifestService _manifestService;

	public ModStore(ILogger<ModStore> logger, SettingsStore settingsStore, IModManifestService manifestService)
	{
		_logger = logger;
		_settingsStore = settingsStore;
		_manifestService = manifestService;

		_logger.LogInformation("Retrieving mods for startup");
		var modDir = new DirectoryInfo(Path.Combine(_settingsStore.StorageDirectory, "Mods"));
		if (modDir.Exists)
		{
			var dirs = modDir.GetDirectories();
			var tasks = new Task<object?>[dirs.Length];
			for (int i = 0; i < tasks.Length; i++)
			{
				var file = dirs[i].GetFiles("manifest.json").FirstOrDefault();
				if (file is null)
					continue;
				tasks[i] = Task.Run(async () => await _manifestService.FromFileAsync(file));
			}

			var manifests = Task.WhenAll(tasks).Result;
			_mods = new(manifests.Length);

			for (int i = 0; i < manifests.Length; i++)
			{
				var dir = dirs[i];
				var man = manifests[i];
				if (man is not null)
					_mods.Add(new ModData(dirs[i], new ModManifest(man)));
				else
				{
					_logger.LogWarning("No manifest found in \"{}\"", dir.FullName);
					_logger.LogWarning("Skipping \"{}\"", dir.Name);
				}
			}

			/*
			// Old version
			foreach (var dir in modDir.GetDirectories())
			{
				var manifestFile = dir.GetFiles("manifest.json").FirstOrDefault();
				if (manifestFile is null)
				{
					_logger.LogWarning("No manifest found in \"{}\"", dir.FullName);
					_logger.LogWarning("Skipping \"{}\"", dir.Name);
					continue;
				}

				try
				{
					var manifest = _manifestService.FromFileAsync(manifestFile).Result;
					if (manifest is null)
					{
						_logger.LogWarning("Unable to parse manifest \"{}\"", manifestFile.FullName);
						_logger.LogWarning("Skipping \"{}\"", dir.Name);
						continue;
					}

					_mods.Add(new ModData(dir, manifest));
				}
				catch (JsonException ex)
				{
					_logger.LogWarning(ex, "An Exception occurred while parsing manifest \"{}\"", manifestFile.FullName);
					_logger.LogWarning("Skipping \"{}\"", dir.Name);
					continue;
				}
			}
			*/
		}
		else
		{
			_mods = [];
			_logger.LogInformation("Mod directory does not exist yet");
		}
	}

	/// <summary>
	/// Attempts to add an archive file as a mod.
	/// </summary>
	/// <param name="file">The archive file to add as a mod.</param>
	/// <returns><see langword="true"/> if mod is successfully added, otherwise <see langword="false"/>.</returns>
	public async Task<bool> TryAddModFromArchiveAsync(FileInfo file)
	{
		_logger.LogInformation("Attempting to add mod from \"{}\"", file.Name);

		var tmpDir = new DirectoryInfo(Path.Combine(_settingsStore.TempDirectory, file.Name[..^file.Extension.Length]));
		_logger.LogInformation("Creating clean temporary directory \"{}\"", tmpDir.FullName);
		if (tmpDir.Exists)
			tmpDir.Delete(true);
		tmpDir.Create();

		_logger.LogInformation("Extracting archive");
		await Task.Run(() => ArchiveFactory.Open(file.FullName).ExtractToDirectory(tmpDir.FullName));

		/*
		var subDirs = tmpDir.GetDirectories();
		var rootFiles = tmpDir.GetFiles();
		var dirNames = subDirs.Select(static dir => dir.Name).ToArray();

		_logger.LogInformation("Looking for manifest");
		ModManifest? manifest;
		int option = -1;
		if (rootFiles.Where(static f => f.Name == "manifest.json").FirstOrDefault() is FileInfo manifestFile)
		{
			_logger.LogInformation("Deserializing found manifest");
			manifest = ModManifest.Deserialize(manifestFile);
			if (manifest is null)
			{
				_logger.LogError("Deserialization failed");
				tmpDir.Delete(true);
				return false;
			}

			if (!IsGuidFree(manifest.Guid))
			{
				_logger.LogError("Manifest guid {} is already taken", manifest.Guid);
				tmpDir.Delete(true);
				return false;
			}

			if (manifest.Options is not null)
			{
				option = 0;

				if (manifest.Options.Count == 0)
				{
					_logger.LogError("Options where empty");
					tmpDir.Delete(true);
					return false;
				}

				if (manifest.Options.Distinct().Count() != manifest.Options.Count)
				{
					_logger.LogError("Options contain duplicates");
					tmpDir.Delete(true);
					return false;
				}

				var opts = new HashSet<string>(manifest.Options);
				var dirs = new HashSet<string>(dirNames);
				if(!opts.IsSubsetOf(dirs))
				{
					_logger.LogError("Options and sub-directories mismatch");
					tmpDir.Delete(true);
					return false;
				}
			}
		}
		else
		{
			_logger.LogInformation("No manifest found");
			_logger.LogInformation("Attempting to infer manifest from directory structure");

			string[]? options;
			if (subDirs.Length > 0)
			{
				_logger.LogInformation("Found {} sub-directories that will be added as options", subDirs.Length);
				options = dirNames;
				option = 0;
			}
			else
			{
				_logger.LogInformation("No sub-directories found");
				options = null;
			}

			_logger.LogInformation("Writing generate manifest");
			manifest = new ModManifest
			{
				Guid = GetFreeGuid(),
				Name = file.Name[..^file.Extension.Length],
				Description = "Locally imported mod",
				Options = options
			};
			var genManifest = new FileInfo(Path.Combine(tmpDir.FullName, "manifest.json"));
			manifest.Serialize(genManifest);
		}
		*/

		var man = await _manifestService.FromDirectoryAsync(tmpDir);
		
		if (man is null)
			return false;

		await _manifestService.ToFileAsync(man, new(Path.Combine(tmpDir.FullName, "manifest.json")));

		var manifest = new ModManifest(man);

		_logger.LogInformation("Moving mod to storage");
		var modDir = new DirectoryInfo(Path.Combine(_settingsStore.StorageDirectory, "Mods", manifest.Name));
		if (modDir.Exists)
		{
			_logger.LogError("Mod directory already exists in storage");
			tmpDir.Delete(true);
			return false;
		}
		modDir.Parent?.Create();
		await Task.Run(() => tmpDir.CopyTo(modDir.FullName));

		_logger.LogInformation("Adding mod");
		var mod = new ModData(modDir, manifest);
		_mods.Add(mod);
		OnModAdded(new ModEventArgs(mod));

		tmpDir.Delete(true);
		return true;
	}

	/// <summary>
	/// Retrieves a mod by its global unique identifier.
	/// </summary>
	/// <param name="guid">The <see cref="Guid"/> to look for.</param>
	/// <returns>A <see cref="ModData"/> object if found, otherwise <see langword="null"/>.</returns>
	public ModData? GetModByGuid(Guid guid)
	{
		return _mods.FirstOrDefault(m => m.Manifest.Guid == guid);
	}

	/// <summary>
	/// Attempts to remove a mod.
	/// </summary>
	/// <param name="mod">The mod to remove.</param>
	/// <returns><see langword="true"/> if the removal was successful, otherwise <see langword="false"/>.</returns>
	public bool Remove(ModData mod)
	{
		_logger.LogInformation("Attempting to remove {}", mod.Manifest.Guid);
		if (_mods.Remove(mod))
		{
			mod.Directory.Delete(true);
			OnModRemoved(new ModEventArgs(mod));
			_logger.LogInformation("Mod \"{}\" removed", mod.Manifest.Name);
			return true;
		}
		return false;
	}

	/// <summary>
	/// Deploys all mods listed by <paramref name="modGuids"/>.
	/// </summary>
	/// <param name="modGuids">The mods <see cref="Guid"/>s to deploy.</param>
	/// <exception cref="InvalidOperationException">Thrown if the Helldivers 2 path is not set.</exception>
	public async Task DeployAsync(Guid[] modGuids)
	{

		if (string.IsNullOrEmpty(_settingsStore.GameDirectory))
		{
			_logger.LogError("Helldivers 2 path not set!");
			throw new InvalidOperationException("Helldivers 2 path not set!");
		}

		if (modGuids.Length == 0)
		{
			_logger.LogInformation("No mods enabled, skipping deployment");
			return;
		}

		await PurgeAsync();

		_logger.LogInformation("Starting deployment of {} mods", modGuids.Length);

		var stageDir = new DirectoryInfo(Path.Combine(_settingsStore.TempDirectory, "Staging"));
		_logger.LogInformation("Creating clean staging directory \"{}\"", stageDir.FullName);
		if (stageDir.Exists)
			stageDir.Delete(true);
		stageDir.Create();

		var groups = new Dictionary<string, List<PatchFileTriplet>>();

		void AddFilesFromDir(DirectoryInfo dir)
		{
			var files = dir.GetFiles().Where(static f => GetPatchFileRegex().IsMatch(f.Name)).ToArray();
			var names = new HashSet<string>();
			for (int i = 0; i < files.Length; i++)
				names.Add(files[i].Name[0..16]);

			foreach (var name in names)
			{
				var indexes = new HashSet<int>();
				foreach (var file in files)
				{
					var match = GetPatchIndexRegex().Match(file.Name);
					indexes.Add(int.Parse(match.Groups[1].ValueSpan));
				}

				foreach (var index in indexes)
				{
					FileInfo? patchFile = files.FirstOrDefault(f => Regex.IsMatch(f.Name, @$"^{name}\.patch_{index}$"));
					FileInfo? gpuFile = files.FirstOrDefault(f => Regex.IsMatch(f.Name, @$"^{name}\.patch_{index}.gpu_resources$"));
					FileInfo? streamFile = files.FirstOrDefault(f => Regex.IsMatch(f.Name, @$"^{name}\.patch_{index}.stream$"));

					if (!groups.ContainsKey(name))
						groups.Add(name, []);
					groups[name].Add(new PatchFileTriplet
					{
						Patch = patchFile,
						GpuResources = gpuFile,
						Stream = streamFile
					});
				}
			}
		}

		_logger.LogInformation("Grouping files");
		foreach (var guid in modGuids)
		{
			var mod = GetModByGuid(guid);
			if (mod is null)
			{
				_logger.LogWarning("Mod with guid {} not found, skipping", guid);
				continue;
			}
			_logger.LogInformation("Working on \"{}\"", mod.Manifest.Name);

			switch (mod.Manifest.Version)
			{
				case ModManifest.ManifestVersion.Legacy:
					{
						var man = mod.Manifest.Legacy;
						var selected = mod.SelectedOptions;

						if (man.Options is not null)
						{

						}
						else
							AddFilesFromDir(mod.Directory);
					}
					break;

				case ModManifest.ManifestVersion.V1:
					{
						var man = mod.Manifest.V1;
						var enabled = mod.EnabedOptions;
						var selected = mod.SelectedOptions;

						if (man.Options is not null)
						{

						}
						else
							AddFilesFromDir(mod.Directory);
					}
					break;

				case ModManifest.ManifestVersion.Unknown:
					throw new NotSupportedException();
			}

			/*
			_logger.LogInformation("Looking for option");
			DirectoryInfo modDir;
			if (mod.Option == -1)
			{
				modDir = mod.Directory;
				_logger.LogInformation("No options found using root");
			}
			else
			{
				modDir = new DirectoryInfo(Path.Combine(mod.Directory.FullName, mod.Manifest.Options![mod.Option]));
				_logger.LogInformation("Option \"{}\" selected", modDir.Name);
			}

			var files = modDir.GetFiles().Where(static f => GetPatchFileRegex().IsMatch(f.Name)).ToArray();
			_logger.LogInformation("Found {} files", files.Length);
			var names = new HashSet<string>();
			for (int i = 0; i < files.Length; i++)
				names.Add(files[i].Name[0..16]);
			_logger.LogInformation("Grouped into {}", names.Count);

			foreach (var name in names)
			{
				var indexes = new HashSet<int>();
				foreach(var file in files)
				{
					var match = GetPatchIndexRegex().Match(file.Name);
					indexes.Add(int.Parse(match.Groups[1].ValueSpan));
				}
				_logger.LogInformation("Found {} different indexes", indexes.Count);

				foreach (var index in indexes)
				{
					FileInfo? patchFile = files.FirstOrDefault(f => Regex.IsMatch(f.Name, @$"^{name}\.patch_{index}$"));
					FileInfo? gpuFile = files.FirstOrDefault(f => Regex.IsMatch(f.Name, @$"^{name}\.patch_{index}.gpu_resources$"));
					FileInfo? streamFile = files.FirstOrDefault(f => Regex.IsMatch(f.Name, @$"^{name}\.patch_{index}.stream$"));

					if (!groups.ContainsKey(name))
						groups.Add(name, []);
					groups[name].Add(new PatchFileTriplet
					{
						Patch = patchFile,
						GpuResources = gpuFile,
						Stream = streamFile
					});
				}
			}
			*/

		}

		_logger.LogInformation("Copying files");
		var installedFiles = new List<string>();
		foreach (var (name, list) in groups)
		{
			for (int i = 0; i < list.Count; i++)
			{
				var triplet = list[i];

				var newPatchPath = Path.Combine(_settingsStore.GameDirectory, "data", $"{name}.patch_{i}");
				FileInfo pathDest;
				if (triplet.Patch is not null)
				{
					pathDest = triplet.Patch.CopyTo(newPatchPath);
				}
				else
				{
					pathDest = new FileInfo(newPatchPath);
					pathDest.Create().Dispose();
				}
				installedFiles.Add(pathDest.FullName);

				var newGpuResourcesPath = Path.Combine(_settingsStore.GameDirectory, "data", $"{name}.patch_{i}.gpu_resources");
				FileInfo gpuResourceDest;
				if (triplet.GpuResources is not null)
				{
					gpuResourceDest = triplet.GpuResources.CopyTo(newGpuResourcesPath);
				}
				else
				{
					gpuResourceDest = new FileInfo(newGpuResourcesPath);
					gpuResourceDest.Create().Dispose();
				}
				installedFiles.Add(gpuResourceDest.FullName);

				var newStreamPath = Path.Combine(_settingsStore.GameDirectory, "data", $"{name}.patch_{i}.stream");
				FileInfo streamDest;
				if (triplet.Stream is not null)
				{
					streamDest = triplet.Stream.CopyTo(newStreamPath);
				}
				else
				{
					streamDest = new FileInfo(newStreamPath);
					streamDest.Create().Dispose();
				}
				installedFiles.Add(streamDest.FullName);
			}
		}

		_logger.LogInformation("Saving installed file list");
		await File.WriteAllLinesAsync(Path.Combine(_settingsStore.StorageDirectory, "installed.txt"), installedFiles);

		_logger.LogInformation("Deployment success");
	}

	public async Task PurgeAsync()
	{
		_logger.LogInformation("Purging mods");
		var path = Path.Combine(_settingsStore.StorageDirectory, "installed.txt");

		if (File.Exists(path))
		{
			_logger.LogInformation("Reading installed file list");
			var installedFiles = await File.ReadAllLinesAsync(path);

			_logger.LogInformation("Deleting files");
			foreach (var file in installedFiles)
				if (File.Exists(file))
					File.Delete(file);

			_logger.LogInformation("Deleting installed file list");
			File.Delete(path);
		}

		_logger.LogInformation("Purge complete");
	}

	private void OnModAdded(ModEventArgs e)
	{
		ModAdded?.Invoke(this, e);
	}

	private void OnModRemoved(ModEventArgs e)
	{
		ModRemoved?.Invoke(this, e);
	}

	[GeneratedRegex(@"^[a-z0-9]{16}\.patch_[0-9]+(\.(stream|gpu_resources))?$")]
	private static partial Regex GetPatchFileRegex();

	[GeneratedRegex(@"\.patch_[0-9]+")]
	private static partial Regex GetPatchRegex();

	[GeneratedRegex(@"^(?:[a-z0-9]{16}\.patch_)([0-9]+)(?:(?:\.(?:stream|gpu_resources))?)$")]
	private static partial Regex GetPatchIndexRegex();
}
