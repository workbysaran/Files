// Copyright (c) Files Community
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using System.IO;

namespace Files.App.Services.Thumbnails
{
	public sealed class ThumbnailService : IThumbnailService
	{
		private static readonly HashSet<string> _perFileIconExtensions = new(StringComparer.OrdinalIgnoreCase)
		{
			".exe", ".lnk", ".ico", ".url", ".scr"
		};

		private readonly IThumbnailCache _cache;
		private readonly Dictionary<string, IThumbnailGenerator> _customGenerators;
		private readonly IThumbnailGenerator _defaultGenerator;
		private readonly ILogger _logger;
		private readonly IUserSettingsService _userSettingsService;

		public ThumbnailService(
			IThumbnailCache cache,
			IThumbnailGenerator defaultGenerator,
			IUserSettingsService userSettingsService,
			ILogger<ThumbnailService> logger)
		{
			_cache = cache;
			_defaultGenerator = defaultGenerator;
			_userSettingsService = userSettingsService;
			_logger = logger;
			_customGenerators = new Dictionary<string, IThumbnailGenerator>();
		}

		public async Task<byte[]?> GetThumbnailAsync(
			string path,
			int size,
			bool isFolder,
			IconOptions options,
			CancellationToken ct)
		{
			try
			{
				if (options.HasFlag(IconOptions.ReturnIconOnly) && !isFolder)
				{
					var extension = Path.GetExtension(path);
					if (!string.IsNullOrEmpty(extension) && !_perFileIconExtensions.Contains(extension))
					{
						var cachedIcon = _cache.GetIcon(extension, size);
						if (cachedIcon is not null)
							return cachedIcon;
					}
				}

				if (!_userSettingsService.GeneralSettingsService.EnableThumbnailCache)
				{
					var generator = SelectGenerator(path, isFolder);
					var result = await generator.GenerateAsync(path, size, isFolder, options, ct);

					// Store icon in memory cache
					if (result is not null && options.HasFlag(IconOptions.ReturnIconOnly) && !isFolder)
					{
						var ext = Path.GetExtension(path);
						if (!string.IsNullOrEmpty(ext) && !_perFileIconExtensions.Contains(ext))
							_cache.SetIcon(ext, size, result);
					}

					return result;
				}

				/* Shell API returns S_OK with generic folder icons for cloud drive folders when
				 * files are placeholders, making it indistinguishable from a real thumbnail.
				 * Skip disk cache for these folders to avoid persisting stale generic results.
				 */
				var skipDiskCache = IsCloudFolder(path, isFolder);

				if (!skipDiskCache)
				{
					var cached = await _cache.GetAsync(path, size, options, ct);
					if (cached is not null)
						return cached;
				}

				var selectedGenerator = SelectGenerator(path, isFolder);

				if (!options.HasFlag(IconOptions.ReturnIconOnly))
				{
					var probe = await selectedGenerator.GenerateAsync(path, size, isFolder, options | IconOptions.ReturnOnlyIfCached, ct);
					if (probe is not null)
					{
						ct.ThrowIfCancellationRequested();
						if (!skipDiskCache)
							await _cache.SetAsync(path, size, options, probe, ct);
						return probe;
					}
				}

				var thumbnail = await selectedGenerator.GenerateAsync(path, size, isFolder, options, ct);

				if (thumbnail is not null)
				{
					ct.ThrowIfCancellationRequested();
					if (options.HasFlag(IconOptions.ReturnIconOnly) && !isFolder)
					{
						// Icons go to in-memory cache only, not disk
						var ext = Path.GetExtension(path);
						if (!string.IsNullOrEmpty(ext) && !_perFileIconExtensions.Contains(ext))
							_cache.SetIcon(ext, size, thumbnail);
					}
					else if (!options.HasFlag(IconOptions.ReturnIconOnly) && !skipDiskCache)
					{
						await _cache.SetAsync(path, size, options, thumbnail, ct);
					}
				}

				return thumbnail;
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Failed to get thumbnail for {Path}", path);
				return null;
			}
		}

		public async Task<byte[]?> GetCachedThumbnailAsync(
			string path,
			int size,
			bool isFolder,
			IconOptions options,
			CancellationToken ct)
		{
			try
			{
				if (!_userSettingsService.GeneralSettingsService.EnableThumbnailCache)
					return null;

				if (IsCloudFolder(path, isFolder))
					return null;

				return await _cache.GetAsync(path, size, options, ct);
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Failed to get cached thumbnail for {Path}", path);
				return null;
			}
		}

		public void RegisterGenerator(IThumbnailGenerator generator)
		{
			foreach (var type in generator.SupportedTypes)
			{
				_customGenerators[type.ToLowerInvariant()] = generator;
				_logger.LogInformation("Registered custom generator for {Type}", type);
			}
		}

		private IThumbnailGenerator SelectGenerator(string path, bool isFolder)
		{
			if (isFolder)
			{
				if (_customGenerators.TryGetValue("folder", out var folderGen))
					return folderGen;
			}
			else
			{
				var extension = Path.GetExtension(path).ToLowerInvariant();
				if (_customGenerators.TryGetValue(extension, out var customGen))
					return customGen;
			}

			return _defaultGenerator;
		}

		private static bool IsCloudFolder(string path, bool isFolder)
		{
			if (!isFolder)
				return false;

			try
			{
				// 0x180000 = 0x80000 | 0x100000 (CloudPinned | CloudUnpinned)
				const FileAttributes CloudMask = (FileAttributes)0x180000;
				return (File.GetAttributes(path) & CloudMask) != 0;
			}
			catch
			{
				return false;
			}
		}

		public Task ClearCacheAsync() => _cache.ClearAsync();
		public Task<long> GetCacheSizeAsync() => _cache.GetSizeAsync();
		public Task EvictCacheAsync(long targetSizeBytes) => _cache.EvictToSizeAsync(targetSizeBytes);
	}
}
