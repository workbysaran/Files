// Copyright (c) Files Community
// Licensed under the MIT License.

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.IO;
using Windows.Storage;

namespace Files.App.Services.Thumbnails
{
	public sealed class ThumbnailCache : IThumbnailCache, IDisposable
	{
		private readonly SqliteConnection _connection;
		private readonly ConcurrentDictionary<string, byte[]> _iconCache;
		private readonly ILogger _logger;
		private readonly IUserSettingsService _userSettingsService;

		private const long DefaultCacheSizeMiB = 512;

		private const System.IO.FileAttributes CloudPinned = (System.IO.FileAttributes)0x80000;
		private const System.IO.FileAttributes CloudUnpinned = (System.IO.FileAttributes)0x100000;

		public ThumbnailCache(IUserSettingsService userSettingsService, ILogger<ThumbnailCache> logger)
		{
			_logger = logger;
			_userSettingsService = userSettingsService;
			_iconCache = new ConcurrentDictionary<string, byte[]>();

			var cacheDirectory = Path.Combine(
				ApplicationData.Current.LocalFolder.Path,
				"thumbnail_cache");

			Directory.CreateDirectory(cacheDirectory);

			var dbPath = Path.Combine(cacheDirectory, "thumbnails.db");

			_connection = new SqliteConnection($"Data Source={dbPath}");
			_connection.Open();

			InitializeDatabase();

			_logger.LogInformation("Thumbnail cache database initialized at {Path}", dbPath);
		}

		private void InitializeDatabase()
		{
			using var cmd = _connection.CreateCommand();
			cmd.CommandText = """
				CREATE TABLE IF NOT EXISTS thumbnails (
					path TEXT NOT NULL,
					size INTEGER NOT NULL,
					icon_type TEXT NOT NULL,
					file_modified INTEGER NOT NULL,
					file_size INTEGER NOT NULL,
					cloud_status INTEGER NOT NULL DEFAULT 0,
					data BLOB NOT NULL,
					last_accessed INTEGER NOT NULL,
					PRIMARY KEY (path, size, icon_type)
				);
				PRAGMA journal_mode=WAL;
				PRAGMA synchronous=NORMAL;
				""";
			cmd.ExecuteNonQuery();
		}

		public Task<byte[]?> GetAsync(string path, int size, IconOptions options, CancellationToken ct)
		{
			try
			{
				var iconType = options.HasFlag(IconOptions.ReturnIconOnly) ? "icon" : "thumb";
				var metadata = GetFileMetadata(path);

				using var cmd = _connection.CreateCommand();
				cmd.CommandText = """
					SELECT data FROM thumbnails
					WHERE path = $path AND size = $size AND icon_type = $iconType
					AND file_modified = $modified AND file_size = $fileSize AND cloud_status = $cloud
					""";
				cmd.Parameters.AddWithValue("$path", path.ToLowerInvariant());
				cmd.Parameters.AddWithValue("$size", size);
				cmd.Parameters.AddWithValue("$iconType", iconType);
				cmd.Parameters.AddWithValue("$modified", metadata.Modified.Ticks);
				cmd.Parameters.AddWithValue("$fileSize", metadata.Size);
				cmd.Parameters.AddWithValue("$cloud", (int)metadata.CloudStatus);

				var result = cmd.ExecuteScalar();
				if (result is byte[] data)
				{
					using var updateCmd = _connection.CreateCommand();
					updateCmd.CommandText = "UPDATE thumbnails SET last_accessed = $now WHERE path = $path AND size = $size AND icon_type = $iconType";
					updateCmd.Parameters.AddWithValue("$now", DateTime.UtcNow.Ticks);
					updateCmd.Parameters.AddWithValue("$path", path.ToLowerInvariant());
					updateCmd.Parameters.AddWithValue("$size", size);
					updateCmd.Parameters.AddWithValue("$iconType", iconType);
					updateCmd.ExecuteNonQuery();

					return Task.FromResult<byte[]?>(data);
				}

				return Task.FromResult<byte[]?>(null);
			}
			catch (Exception ex)
			{
				_logger.LogWarning(ex, "Error reading cache for {Path}", path);
				return Task.FromResult<byte[]?>(null);
			}
		}

		public Task SetAsync(string path, int size, IconOptions options, byte[] thumbnail, CancellationToken ct)
		{
			try
			{
				var iconType = options.HasFlag(IconOptions.ReturnIconOnly) ? "icon" : "thumb";
				var metadata = GetFileMetadata(path);

				using var cmd = _connection.CreateCommand();
				cmd.CommandText = """
					INSERT OR IGNORE INTO thumbnails (path, size, icon_type, file_modified, file_size, cloud_status, data, last_accessed)
					VALUES ($path, $size, $iconType, $modified, $fileSize, $cloud, $data, $now)
					""";
				cmd.Parameters.AddWithValue("$path", path.ToLowerInvariant());
				cmd.Parameters.AddWithValue("$size", size);
				cmd.Parameters.AddWithValue("$iconType", iconType);
				cmd.Parameters.AddWithValue("$modified", metadata.Modified.Ticks);
				cmd.Parameters.AddWithValue("$fileSize", metadata.Size);
				cmd.Parameters.AddWithValue("$cloud", (int)metadata.CloudStatus);
				cmd.Parameters.AddWithValue("$data", thumbnail);
				cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.Ticks);
				cmd.ExecuteNonQuery();

				_ = Task.Run(() => TryEvictIfNeeded());
			}
			catch (Exception ex)
			{
				_logger.LogWarning(ex, "Error writing cache for {Path}", path);
			}

			return Task.CompletedTask;
		}

		public Task<long> GetSizeAsync()
		{
			try
			{
				using var cmd = _connection.CreateCommand();
				cmd.CommandText = "SELECT COALESCE(SUM(LENGTH(data)), 0) FROM thumbnails";
				var result = cmd.ExecuteScalar();
				return Task.FromResult(result is long val ? val : 0L);
			}
			catch
			{
				return Task.FromResult(0L);
			}
		}

		public Task EvictToSizeAsync(long targetSizeBytes)
		{
			try
			{
				EvictToSizeCore(targetSizeBytes);
			}
			catch (Exception ex)
			{
				_logger.LogWarning(ex, "Error during eviction");
			}

			return Task.CompletedTask;
		}

		private void TryEvictIfNeeded()
		{
			try
			{
				var currentSize = GetSizeSync();
				var maxCacheSizeBytes = GetMaxCacheSizeBytes();

				if (currentSize > maxCacheSizeBytes)
				{
					_logger.LogInformation("Cache size {Current} MiB exceeds limit {Max} MiB, evicting...",
						currentSize / 1024 / 1024, maxCacheSizeBytes / 1024 / 1024);

					EvictToSizeCore(maxCacheSizeBytes * 3 / 4);
				}
			}
			catch (Exception ex)
			{
				_logger.LogWarning(ex, "Error during auto-eviction");
			}
		}

		private void EvictToSizeCore(long targetSizeBytes)
		{
			var currentSize = GetSizeSync();
			if (currentSize <= targetSizeBytes)
				return;

			var bytesToRemove = currentSize - targetSizeBytes;

			using var cmd = _connection.CreateCommand();
			cmd.CommandText = """
				DELETE FROM thumbnails WHERE rowid IN (
					SELECT rowid FROM thumbnails ORDER BY last_accessed ASC LIMIT $limit
				)
				""";

			var estimatedRowSize = currentSize / Math.Max(GetRowCount(), 1);
			var rowsToDelete = (int)Math.Max(bytesToRemove / Math.Max(estimatedRowSize, 1), 1);
			cmd.Parameters.AddWithValue("$limit", rowsToDelete);

			var removed = cmd.ExecuteNonQuery();

			// No VACUUM here — freed pages get reused by future inserts.
			// VACUUM copies the entire DB which is too expensive for partial eviction.

			_logger.LogInformation("Evicted {Count} cache entries", removed);
		}

		private long GetSizeSync()
		{
			using var cmd = _connection.CreateCommand();
			cmd.CommandText = "SELECT COALESCE(SUM(LENGTH(data)), 0) FROM thumbnails";
			var result = cmd.ExecuteScalar();
			return result is long val ? val : 0L;
		}

		private long GetRowCount()
		{
			using var cmd = _connection.CreateCommand();
			cmd.CommandText = "SELECT COUNT(*) FROM thumbnails";
			var result = cmd.ExecuteScalar();
			return result is long val ? val : 0L;
		}

		private long GetMaxCacheSizeBytes()
		{
			var cacheSizeMiB = _userSettingsService.GeneralSettingsService.ThumbnailCacheSizeLimit;
			if (cacheSizeMiB <= 0)
				cacheSizeMiB = DefaultCacheSizeMiB;

			return (long)(cacheSizeMiB * 1024 * 1024);
		}

		private static FileMetadata GetFileMetadata(string path)
		{
			try
			{
				if (Directory.Exists(path))
				{
					var dirInfo = new DirectoryInfo(path);
					var cloudStatus = dirInfo.Attributes & (CloudPinned | CloudUnpinned);
					return new FileMetadata
					{
						Modified = dirInfo.LastWriteTimeUtc,
						Size = 0,
						CloudStatus = cloudStatus
					};
				}
				else
				{
					var fileInfo = new FileInfo(path);
					var cloudStatus = fileInfo.Attributes & (CloudPinned | CloudUnpinned);
					return new FileMetadata
					{
						Modified = fileInfo.LastWriteTimeUtc,
						Size = fileInfo.Length,
						CloudStatus = cloudStatus
					};
				}
			}
			catch
			{
				return new FileMetadata
				{
					Modified = DateTime.MinValue,
					Size = 0,
					CloudStatus = 0
				};
			}
		}

		public byte[]? GetIcon(string extension, int size)
		{
			var key = $"{extension.ToLowerInvariant()}|{size}";
			return _iconCache.TryGetValue(key, out var data) ? data : null;
		}

		public void SetIcon(string extension, int size, byte[] iconData)
		{
			var key = $"{extension.ToLowerInvariant()}|{size}";
			_iconCache.TryAdd(key, iconData);
		}

		public Task ClearAsync()
		{
			try
			{
				using var cmd = _connection.CreateCommand();
				cmd.CommandText = "DELETE FROM thumbnails";
				cmd.ExecuteNonQuery();

				using var vacuumCmd = _connection.CreateCommand();
				vacuumCmd.CommandText = "VACUUM";
				vacuumCmd.ExecuteNonQuery();

				using var walCmd = _connection.CreateCommand();
				walCmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
				walCmd.ExecuteNonQuery();

				_iconCache.Clear();
				_logger.LogInformation("Cache cleared");
			}
			catch (Exception ex)
			{
				_logger.LogWarning(ex, "Error clearing cache");
			}

			return Task.CompletedTask;
		}

		public void Dispose()
		{
			_connection?.Dispose();
		}
	}

	internal record FileMetadata
	{
		public DateTime Modified { get; init; }
		public long Size { get; init; }
		public System.IO.FileAttributes CloudStatus { get; init; }
	}
}
