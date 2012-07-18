﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Caching;
using Talifun.Web.Helper;
using Yahoo.Yui.Compressor;

namespace Talifun.Web.Crusher
{
    /// <summary>
    /// Manages the adding and removing of js files to crush. It also does the js crushing.
    /// </summary>
    public class JsCrusher : IJsCrusher
    {
        protected readonly ICacheManager CacheManager;
        protected readonly IPathProvider PathProvider;
        protected readonly IRetryableFileOpener RetryableFileOpener;
        protected readonly IRetryableFileWriter RetryableFileWriter;
        
        protected static string JsCrusherType = typeof(JsCrusher).ToString();

        public JsCrusher(ICacheManager cacheManager, IPathProvider pathProvider, IRetryableFileOpener retryableFileOpener, IRetryableFileWriter retryableFileWriter)
        {
            CacheManager = cacheManager;
            PathProvider = pathProvider;
            RetryableFileOpener = retryableFileOpener;
            RetryableFileWriter = retryableFileWriter;
        }

    	/// <summary>
    	/// Add js files to be crushed.
    	/// </summary>
    	/// <param name="outputUri">The virtual path for the crushed js file.</param>
    	/// <param name="files">The js files to be crushed.</param>
    	/// <param name="directories">The js directories to be crushed. </param>
    	public virtual JsCrushedOutput AddGroup(Uri outputUri, IEnumerable<JsFile> files, IEnumerable<JsDirectory> directories)
        {
            var outputFileInfo = new FileInfo(PathProvider.MapPath(outputUri));
			var crushedContent = ProcessGroup(outputFileInfo, files, directories);            
            RetryableFileWriter.SaveContentsToFile(crushedContent.Output, outputFileInfo);
            AddGroupToCache(outputUri, crushedContent.FilesToWatch, files, crushedContent.FoldersToWatch, directories);

    	    return crushedContent;
        }

        /// <summary>
        /// Get all files and files in directories that are going to be crushed.
        /// </summary>
        /// <param name="files"></param>
        /// <param name="directories"></param>
        /// <returns></returns>
		private IEnumerable<JsFileToWatch> GetFilesToWatch(IEnumerable<JsFile> files, IEnumerable<JsDirectory> directories)
		{
            var filesToWatch = files.Select(x => new JsFileToWatch()
            {
                CompressionType = x.CompressionType,
                FilePath = x.FilePath
            });

		    var filesInDirectoriesToWatch = directories
                .SelectMany(x => 
                    Directory.GetFiles(x.FilePath, "*", SearchOption.AllDirectories)
                    .Where(y => Regex.IsMatch(y, x.Filter, RegexOptions.Compiled | RegexOptions.IgnoreCase))
                    .Select(y => new JsFileToWatch()
                        {
                            CompressionType = x.CompressionType,
                            FilePath = y
                        }));


            return filesInDirectoriesToWatch.Concat(filesToWatch).Distinct(new JsFileToWatchEqualityComparer());
		}

    	/// <summary>
    	/// Compress the js files and store them in the specified js file.
    	/// </summary>
    	/// <param name="outputFileInfo">The output path for the crushed js file.</param>
    	/// <param name="files">The js files to be crushed.</param>
    	/// <param name="directories"> </param>
    	public virtual JsCrushedOutput ProcessGroup(FileInfo outputFileInfo, IEnumerable<JsFile> files, IEnumerable<JsDirectory> directories)
        {
            var uncompressedContents = new StringBuilder();
            var toBeCompressedContents = new StringBuilder();

    		var filesToWatch = GetFilesToWatch(files, directories);
    	    var foldersToWatch = directories
                .Select( x =>
    	            Talifun.FileWatcher.EnhancedFileSystemWatcherFactory.Instance
                    .CreateEnhancedFileSystemWatcher(x.FilePath, x.Filter, x.PollTime, x.IncludeSubDirectories));

            var filesToProcess = filesToWatch.Select(jsFile => new JsFileProcessor(RetryableFileOpener, PathProvider, jsFile.FilePath, jsFile.CompressionType));
            foreach (var fileToProcess in filesToProcess)
            {
                switch (fileToProcess.CompressionType)
                {
                    case JsCompressionType.None:
                        uncompressedContents.AppendLine(fileToProcess.GetContents());
                        break;
                    case JsCompressionType.Min:
                        toBeCompressedContents.AppendLine(fileToProcess.GetContents());
                        break;
                }
            }

            if (toBeCompressedContents.Length > 0)
            {
                uncompressedContents.Append(JavaScriptCompressor.Compress(toBeCompressedContents.ToString()));
            }

            var crushedOutput = new JsCrushedOutput
            {
                Output = uncompressedContents,
                FoldersToWatch = foldersToWatch,
				FilesToWatch = filesToWatch
            };

            return crushedOutput;
        }

        /// <summary>
        /// Remove all js files from being crushed
        /// </summary>
        /// <param name="outputUri">The path for the crushed js file</param>
        public virtual void RemoveGroup(Uri outputUri)
        {
            CacheManager.Remove<JsCacheItem>(GetKey(outputUri));
        }

        /// <summary>
        /// Add the js files to the cache so that they are monitored for any changes.
        /// </summary>
        /// <param name="outputUri">The path for the crushed js file.</param>
        /// <param name="filesToWatch">Files that are crushed.</param>
        /// <param name="files">The js files to be crushed.</param>
        /// <param name="foldersToWatch"> </param>
        /// <param name="directories">The js directories to be crushed.</param>
        public virtual void AddGroupToCache(Uri outputUri, IEnumerable<JsFileToWatch> filesToWatch, IEnumerable<JsFile> files, IEnumerable<Talifun.FileWatcher.IEnhancedFileSystemWatcher> foldersToWatch, IEnumerable<JsDirectory> directories)
        {
            var fileNames = new List<string>
            {
                    PathProvider.MapPath(outputUri)
            };

			fileNames.AddRange(filesToWatch.Select(file => PathProvider.MapPath(file.FilePath)));

            var cacheItem = new JsCacheItem()
            {
                OutputUri = outputUri,
				FilesToWatch = filesToWatch,
                Files = files,
                FoldersToWatch = foldersToWatch,
				Directories = directories
            };

            CacheManager.Insert(
                GetKey(outputUri),
                cacheItem,
                new CacheDependency(fileNames.ToArray(), System.DateTime.Now),
                Cache.NoAbsoluteExpiration,
                Cache.NoSlidingExpiration,
                CacheItemPriority.High,
                FileRemoved);

            foreach (var enhancedFileSystemWatcher in foldersToWatch)
            {
                enhancedFileSystemWatcher.FileActivityFinishedEvent += OnFileActivityFinishedEvent;
                enhancedFileSystemWatcher.UserState = outputUri;
                enhancedFileSystemWatcher.Start();
            }
        }

        /// <summary>
        /// When file events have finished it means we should should remove them from the cache
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        protected void OnFileActivityFinishedEvent(object sender, FileWatcher.FileActivityFinishedEventArgs e)
        {
            var outputUri = (Uri)e.UserState;
            RemoveGroup(outputUri);
        }

        /// <summary>
        /// When a file is removed from cache, keep it in the cache if it is unused or expired as we want to continue to monitor
        /// any changes to file. If it has been removed because the file has changed then regenerate the crushed js file and
        /// start the monitoring again.
        /// </summary>
        /// <param name="key">The key of the cache item</param>
        /// <param name="value">The value of the cache item</param>
        /// <param name="reason">The reason the file was removed from cache.</param>
        public virtual void FileRemoved(string key, object value, CacheItemRemovedReason reason)
        {
            var cacheItem = (JsCacheItem)value;
            foreach (var enhancedFileSystemWatcher in cacheItem.FoldersToWatch)
            {
                enhancedFileSystemWatcher.Stop();
                enhancedFileSystemWatcher.FileActivityFinishedEvent -= OnFileActivityFinishedEvent;
            }
            switch (reason)
            {
                case CacheItemRemovedReason.DependencyChanged:
                    AddGroup(cacheItem.OutputUri, cacheItem.Files, cacheItem.Directories);
                    break;
                case CacheItemRemovedReason.Underused:
                case CacheItemRemovedReason.Expired:
                    AddGroupToCache(cacheItem.OutputUri, cacheItem.FilesToWatch, cacheItem.Files, cacheItem.FoldersToWatch, cacheItem.Directories);
                    break;
            }
        }

        /// <summary>
        /// Get the cache key to use for caching.
        /// </summary>
        /// <param name="outputUri">The path for the crushed js file.</param>
        /// <returns>The cache key to use for caching.</returns>
        public virtual string GetKey(Uri outputUri)
        {
            var prefix = JsCrusherType + "|";
            return prefix + outputUri;
        }
    }
}