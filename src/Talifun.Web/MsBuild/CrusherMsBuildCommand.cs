﻿using System;
using System.Configuration;
using System.IO;
using System.Text;
using System.Threading;
using Talifun.Web.Crusher;
using Talifun.Web.Crusher.Config;
using Talifun.Web.CssSprite;
using Talifun.Web.CssSprite.Config;
using Talifun.Web.Helper;

namespace Talifun.Web.MsBuild
{
    [Serializable]
    public class CrusherMsBuildCommand : MarshalByRefObject, ICloneable //IClonable is marker interface so we can use msbuild with different app domain
    {
        private readonly string _applicationPath;
        private readonly string _binDirectoryPath;
        private readonly string _configPath;
        private readonly Action<string> _logMessage;
        private readonly Action<string> _logError;
        private const string CrusherSectionName = "Crusher";
        private const string CssSpriteSectionName = "CssSprite";
        private const int BufferSize = 32768;
        private static readonly Encoding Encoding = Encoding.UTF8;
        private readonly IRetryableFileOpener _retryableFileOpener;
        private readonly IHasher _hasher;
        private readonly IRetryableFileWriter _retryableFileWriter;
        private readonly IMetaData _fileMetaData;
        private readonly IPathProvider _pathProvider;
        private readonly ICacheManager _cacheManager;
        private readonly CssSpriteSection _cssSpriteConfiguration;
        private readonly CrusherSection _crusherConfiguration;

        public CrusherMsBuildCommand(string applicationPath, string binDirectoryPath, string configPath, Action<string> logMessage, Action<string> logError)
        {
            if (string.IsNullOrEmpty(applicationPath))
            {
                throw new ArgumentNullException("applicationPath");
            }

            if (string.IsNullOrEmpty(binDirectoryPath))
            {
                throw new ArgumentNullException("binDirectoryPath");
            }

            if (string.IsNullOrEmpty(configPath))
            {
                throw new ArgumentNullException("configPath");
            }

            if (logMessage == null)
            {
                throw new ArgumentNullException("logMessage");
            }

            if (logError == null)
            {
                throw new ArgumentNullException("logError");
            }

            _applicationPath = applicationPath;
            _binDirectoryPath = binDirectoryPath;
            _configPath = configPath;
            _logMessage = logMessage;
            _logError = logError;

            _retryableFileOpener = new RetryableFileOpener();
            _hasher = new Hasher(_retryableFileOpener);
            _retryableFileWriter = new RetryableFileWriter(BufferSize, Encoding, _retryableFileOpener, _hasher);
            _fileMetaData = new MultiFileMetaData(_retryableFileOpener, _retryableFileWriter);

            _cssSpriteConfiguration = GetCssSpriteSection(_configPath, CssSpriteSectionName);
            _crusherConfiguration = GetCrusherSection(_configPath, CrusherSectionName);

            var configUri = new Uri(_configPath, UriKind.RelativeOrAbsolute);
            if (!configUri.IsAbsoluteUri)
            {
                configUri = new Uri(Path.Combine(Environment.CurrentDirectory, configUri.ToString()));
            }

            var physicalApplicationPath = new FileInfo(configUri.LocalPath).DirectoryName;
            _pathProvider = new PathProvider(_applicationPath, physicalApplicationPath);
            _cacheManager = new HttpCacheManager();
        }

        private CrusherSection GetCrusherSection(string configPath, string sectionName)
        {
            var map = new ExeConfigurationFileMap
            {
                ExeConfigFilename = configPath
            };
            var config = ConfigurationManager.OpenMappedExeConfiguration(map, ConfigurationUserLevel.None);
            var crusherSection = config.GetSection(sectionName) as CrusherSection;

            return crusherSection;
        }

        private static CssSpriteSection GetCssSpriteSection(string configPath, string sectionName)
        {
            var map = new ExeConfigurationFileMap
            {
                ExeConfigFilename = configPath
            };
            var config = ConfigurationManager.OpenMappedExeConfiguration(map, ConfigurationUserLevel.None);
            var cssSpriteSection = config.GetSection(sectionName) as CssSpriteSection;

            return cssSpriteSection;
        }

        public object Clone()
        {
            AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
            try
            {
                var cssSpriteOutput = string.Empty;
                var jsOutput = string.Empty;
                var cssOutput = string.Empty;

                var countdownEvents = new CountdownEvent(1);

                ThreadPool.QueueUserWorkItem(data =>
                    {
                        var countdownEvent = (CountdownEvent) data;

                        try
                        {
                            if (_cssSpriteConfiguration != null)
                            {
                                var cssSpriteGroups = _cssSpriteConfiguration.CssSpriteGroups;
                                var cssSpriteCreator = new CssSpriteCreator(_cacheManager, _retryableFileOpener, _pathProvider, _retryableFileWriter, _fileMetaData);
                                var cssSpriteGroupsProcessor = new CssSpriteGroupsProcessor();

                                cssSpriteOutput = cssSpriteGroupsProcessor.ProcessGroups(_pathProvider, cssSpriteCreator, cssSpriteGroups).ToString();

                                _logMessage(cssSpriteOutput);
                            }
                        }
                        catch (Exception exception)
                        {
                            _logError(exception.ToString());
                        }
                        countdownEvent.Signal();
                    }, countdownEvents);

                countdownEvents.Wait();

                countdownEvents = new CountdownEvent(2);

                ThreadPool.QueueUserWorkItem(data =>
                    {
                        var countdownEvent = (CountdownEvent) data;

                        try
                        {
                            if (_crusherConfiguration != null)
                            {
                                var jsCrusher = new JsCrusher(_cacheManager, _pathProvider, _retryableFileOpener, _retryableFileWriter, _fileMetaData);
                                var jsGroups = _crusherConfiguration.JsGroups;
                                var jsGroupsProcessor = new JsGroupsProcessor();

                                jsOutput = jsGroupsProcessor.ProcessGroups(_pathProvider, jsCrusher, jsGroups).ToString();

                                _logMessage(jsOutput);
                            }
                        }
                        catch (Exception exception)
                        {
                            _logError(exception.ToString());
                        }
                        countdownEvent.Signal();
                    }, countdownEvents);

                ThreadPool.QueueUserWorkItem(data =>
                    {
                        var countdownEvent = (CountdownEvent) data;

                        try
                        {
                            if (_crusherConfiguration != null)
                            {
                                var hashQueryStringKeyName = _crusherConfiguration.QuerystringKeyName;
                                var cssAssetsFileHasher = new CssAssetsFileHasher(hashQueryStringKeyName, _hasher, _pathProvider);
                                var cssPathRewriter = new CssPathRewriter(cssAssetsFileHasher, _pathProvider);
                                var cssCrusher = new CssCrusher(_cacheManager, _pathProvider, _retryableFileOpener, _retryableFileWriter, cssPathRewriter, _fileMetaData, _crusherConfiguration.WatchAssets);
                                var cssGroups = _crusherConfiguration.CssGroups;
                                var cssGroupsCrusher = new CssGroupsProcessor();
                                cssOutput = cssGroupsCrusher.ProcessGroups(_pathProvider, cssCrusher, cssGroups).ToString();

                                _logMessage(cssOutput);
                            }
                        }
                        catch (Exception exception)
                        {
                            _logError(exception.ToString());
                        }
                        countdownEvent.Signal();
                    }, countdownEvents);

                countdownEvents.Wait();
            }
            catch (Exception exception)
            {
                _logError(exception.ToString());
            }
            finally
            {
                AppDomain.CurrentDomain.UnhandledException -= OnUnhandledException;
            }

            return null;
        }

        void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            _logError(e.ToString());
        }
    }
}
