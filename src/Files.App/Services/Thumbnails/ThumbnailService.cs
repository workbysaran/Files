// Copyright (c) Files Community
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using System.IO;

namespace Files.App.Services.Thumbnails
{
	public sealed class ThumbnailService : IThumbnailService
	{
		public Task ClearCacheAsync() => _cache.ClearAsync();
		public Task<long> GetCacheSizeAsync() => _cache.GetSizeAsync();
		public Task EvictCacheAsync(long targetSizeBytes) => _cache.EvictToSizeAsync(targetSizeBytes);

		private static readonly HashSet<string> _perFileIconExtensions = new(StringComparer.OrdinalIgnoreCase)
		{
			".exe", ".lnk", ".ico", ".url", ".scr"
		};

		private readonly IThumbnailCache _cache;
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
					var result = await _defaultGenerator.GenerateAsync(path, size, isFolder, options, ct);

					if (result is not null && options.HasFlag(IconOptions.ReturnIconOnly) && !isFolder)
					{
						var ext = Path.GetExtension(path);
						if (!string.IsNullOrEmpty(ext) && !_perFileIconExtensions.Contains(ext))
							_cache.SetIcon(ext, size, result);
					}

					return result;
				}

				var cached = await _cache.GetAsync(path, size, options, ct);
				if (cached is not null && !cached.IsPlaceholder)
					return cached.Data;

				var isPlaceholder = false;

				if (!options.HasFlag(IconOptions.ReturnIconOnly))
				{
					var probe = await _defaultGenerator.GenerateAsync(path, size, isFolder, options | IconOptions.ReturnOnlyIfCached, ct);
					if (probe is not null)
					{
						ct.ThrowIfCancellationRequested();
						App.Logger.LogInformation($"Probed thumbnail for {path} is available, caching it. isFolder: {isFolder}, options: {options}");
						if (cached is not null)
							await _cache.UpdateAsync(path, size, options, probe, ct);
						else
							await _cache.SetAsync(path, size, options, probe, false, ct);
						return probe;
					}
					else
						isPlaceholder = isFolder;
				}

				var thumbnail = await _defaultGenerator.GenerateAsync(path, size, isFolder, options, ct);

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
					else if (!options.HasFlag(IconOptions.ReturnIconOnly))
					{
						if (cached is not null)
							await _cache.UpdateAsync(path, size, options, thumbnail, ct);
						else
							await _cache.SetAsync(path, size, options, thumbnail, isPlaceholder, ct);
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

	}
}
